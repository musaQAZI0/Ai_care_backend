using System.Data;
using System.Data.Common;
using System.Text.Json;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AiCare.Api;

[ApiController]
[Authorize(Policy = "Phase1User")]
[Route("api/phase1/medication-safety/medications/{medicationId:guid}/changes")]
public sealed class MedicationChangeController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user, MedicationChangeActivationService activation) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid medicationId, CancellationToken token)
    {
        if (!await MedicationExists(medicationId, token)) return NotFound();
        return Ok(await Changes(medicationId, token));
    }

    [HttpGet("{changeId:guid}/events")]
    public async Task<IActionResult> Events(Guid medicationId, Guid changeId, CancellationToken token)
    {
        if (!await MedicationExists(medicationId, token)) return NotFound();
        if (!await ChangeExists(medicationId, changeId, token)) return NotFound();
        return Ok(await Query("""
            select id,action,detail,actor,occurred_at from medication_change_events
            where change_id=@change and medication_id=@medication and organization_id=@organization and branch_id=@branch
            order by occurred_at,id
            """, c => { Add(c,"change",changeId); Add(c,"medication",medicationId); },
            r => new { id=r.GetGuid(0), action=r.GetString(1), detail=r.GetString(2), actor=r.GetString(3), occurredAt=r.GetFieldValue<DateTimeOffset>(4) }, token));
    }

    [HttpPost]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public async Task<IActionResult> RequestChange(Guid medicationId, MedicationChangeRequest request, CancellationToken token)
    {
        var medication = await Medication(medicationId, token);
        if (medication is null) return NotFound();
        if (request.ChangeType is not ("Regimen" or "Stop") || string.IsNullOrWhiteSpace(request.Reason) ||
            string.IsNullOrWhiteSpace(request.SourceType) || string.IsNullOrWhiteSpace(request.SourceReference) ||
            string.IsNullOrWhiteSpace(request.PrescriberInstruction))
            return BadRequest(new { message = "Change type, reason, source, reference and prescriber instruction are required." });
        if (request.EffectiveAt < DateTimeOffset.UtcNow.AddDays(-30)) return BadRequest(new { message = "Effective time is too far in the past." });
        if (request.ChangeType == "Regimen" && InvalidProposal(request.Proposal, medication.IsPrn))
            return BadRequest(new { message = "Complete proposed dose, route, schedule, prescriber, form, strength, indication, and PRN protocol are required." });
        if (request.Proposal is not null && request.Proposal.IsPrn && InvalidPrn(request.Proposal))
            return BadRequest(new { message = "PRN indication, dose limit, interval and effect review are required." });

        return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);
            var baseline = await ProfileState(medicationId, token);
            if (baseline is null || baseline.Value.Status != "Verified")
                return Conflict(new { message = "A verified active medication profile is required before proposing a change." });
            if (await Scalar<bool>("select exists(select 1 from medication_change_requests where medication_id=@medication and organization_id=@organization and branch_id=@branch and status in ('Pending','Approved'))", c => Add(c,"medication",medicationId), token))
                return Conflict(new { message = "This medication already has a pending or approved change." });
            var id = Guid.NewGuid();
            await Execute("""
                insert into medication_change_requests(id,medication_id,organization_id,branch_id,change_type,status,baseline_profile_version,proposal,reason,source_type,source_reference,prescriber_instruction,requested_by_user_id,requested_by,effective_at)
                values(@id,@medication,@organization,@branch,@type,'Pending',@version,cast(@proposal as jsonb),@reason,@sourceType,@sourceReference,@instruction,@actorId,@actor,@effective)
                """, c => {
                    Add(c,"id",id);Add(c,"medication",medicationId);Add(c,"type",request.ChangeType);
                    Add(c,"version",baseline.Value.Version);Add(c,"proposal",JsonSerializer.Serialize(request.Proposal));
                    Add(c,"reason",request.Reason.Trim());Add(c,"sourceType",request.SourceType.Trim());
                    Add(c,"sourceReference",request.SourceReference.Trim());Add(c,"instruction",request.PrescriberInstruction.Trim());
                    Add(c,"effective",request.EffectiveAt);
                }, token);
            await Event(id, medicationId, "Requested", request.Reason.Trim(), token);
            Audit("medication.change_requested", medicationId);
            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
            return Created($"/api/phase1/medication-safety/medications/{medicationId}/changes/{id}", new { id, status="Pending", baselineProfileVersion=baseline.Value.Version });
        });
    }

    [HttpPost("{changeId:guid}/review")]
    [Authorize(Roles = "CareManager,Administrator")]
    public async Task<IActionResult> Review(Guid medicationId, Guid changeId, MedicationChangeReviewRequest request, CancellationToken token)
    {
        if (!await MedicationExists(medicationId, token)) return NotFound();
        if (request.Decision is not ("Approve" or "Reject") || string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new { message = "Approve or Reject with a documented reason." });
        return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);
            var change = await Change(medicationId, changeId, true, token);
            if (change is null) return NotFound();
            if (change.Status != "Pending") return Conflict(new { message = "Only pending changes can be reviewed." });
            if (change.RequestedByUserId == user.UserId) return Conflict(new { message = "The requester cannot review their own medication change." });
            var current = await ProfileState(medicationId, token);
            if (current is null || current.Value.Version != change.BaselineProfileVersion || current.Value.Status != "Verified")
                return Conflict(new { message = "The active medication profile changed. Create a new request against its current version." });
            var status = request.Decision == "Approve" ? "Approved" : "Rejected";
            await Execute("""
                update medication_change_requests set status=@status,reviewed_by_user_id=@actorId,reviewed_by=@actor,reviewed_at=now(),review_reason=@reason,updated_at=now()
                where id=@change and medication_id=@medication and organization_id=@organization and branch_id=@branch
                """, c => { Add(c,"status",status);Add(c,"reason",request.Reason.Trim());Add(c,"change",changeId);Add(c,"medication",medicationId); }, token);
            await Event(changeId, medicationId, status, request.Reason.Trim(), token);
            Audit(request.Decision == "Approve" ? "medication.change_approved" : "medication.change_rejected", medicationId);
            if (status == "Approved" && change.EffectiveAt <= DateTimeOffset.UtcNow)
            {
                var error = await Apply(change with { Status="Approved" }, token);
                if (error is not null) return Conflict(new { message=error });
            }
            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
            return Ok(new { id=changeId, status=status == "Approved" && change.EffectiveAt <= DateTimeOffset.UtcNow ? "Applied" : status });
        });
    }

    [HttpPost("{changeId:guid}/activate")]
    [Authorize(Roles = "CareManager,Administrator")]
    public async Task<IActionResult> Activate(Guid medicationId, Guid changeId, CancellationToken token)
    {
        if (!await MedicationExists(medicationId, token)) return NotFound();
        return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token);
            var change = await Change(medicationId, changeId, true, token);
            if (change is null) return NotFound();
            if (change.Status != "Approved") return Conflict(new { message = "Only approved changes can be activated." });
            if (change.EffectiveAt > DateTimeOffset.UtcNow) return Conflict(new { message = "The approved change is not effective yet." });
            var error = await Apply(change, token);
            if (error is not null) return Conflict(new { message=error });
            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
            return Ok(new { id=changeId, status="Applied" });
        });
    }

    [HttpPost("{changeId:guid}/cancel")]
    [Authorize(Roles = "CareManager,Administrator")]
    public async Task<IActionResult> Cancel(Guid medicationId, Guid changeId, MedicationChangeReviewRequest request, CancellationToken token)
    {
        if (!await MedicationExists(medicationId, token)) return NotFound();
        if (string.IsNullOrWhiteSpace(request.Reason)) return BadRequest(new { message = "Cancellation reason is required." });
        return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () =>
        {
            await using var tx=await db.Database.BeginTransactionAsync(token);
            var change=await Change(medicationId,changeId,true,token);
            if(change is null)return NotFound();
            if(change.Status is not ("Pending" or "Approved"))return Conflict(new {message="Only an open change can be cancelled."});
            await Execute("update medication_change_requests set status='Cancelled',updated_at=now() where id=@change and medication_id=@medication and organization_id=@organization and branch_id=@branch",c=>{Add(c,"change",changeId);Add(c,"medication",medicationId);},token);
            await Event(changeId,medicationId,"Cancelled",request.Reason.Trim(),token);
            Audit("medication.change_cancelled",medicationId);
            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
            return Ok(new {id=changeId,status="Cancelled"});
        });
    }

    private Task<string?> Apply(MedicationChangeRow change, CancellationToken token) =>
        activation.Apply(change, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId, user.UserId, user.UserName, token);

    private static bool InvalidProposal(MedicationChangeProposal? p,bool currentPrn) =>
        p is null || string.IsNullOrWhiteSpace(p.Dosage) || string.IsNullOrWhiteSpace(p.Route) ||
        string.IsNullOrWhiteSpace(p.Schedule) || string.IsNullOrWhiteSpace(p.Indication) ||
        string.IsNullOrWhiteSpace(p.Prescriber) || string.IsNullOrWhiteSpace(p.Form) ||
        string.IsNullOrWhiteSpace(p.Strength) || string.IsNullOrWhiteSpace(p.DoseUnit) ||
        string.IsNullOrWhiteSpace(p.Frequency) || p.DoseWindowMinutes is < 0 or > 1440 ||
        (p.StartDate is not null && p.EndDate is not null && p.EndDate < p.StartDate) ||
        (p.IsPrn && InvalidPrn(p));
    private static bool InvalidPrn(MedicationChangeProposal p) =>
        string.IsNullOrWhiteSpace(p.PrnIndication) || p.MaxPrnDoses24h is null or < 1 ||
        p.MinPrnIntervalMinutes is null or < 0 || p.PrnEffectReviewMinutes is null or < 1;

    private async Task<List<object>> Changes(Guid medicationId,CancellationToken token) =>
        await Query("""
            select id,change_type,status,baseline_profile_version,proposal::text,reason,source_type,source_reference,prescriber_instruction,
              requested_by,requested_at,effective_at,reviewed_by,reviewed_at,review_reason,applied_by,applied_at
            from medication_change_requests where medication_id=@medication and organization_id=@organization and branch_id=@branch
            order by requested_at desc
            """, c=>Add(c,"medication",medicationId), r=>(object)new {
                id=r.GetGuid(0),changeType=r.GetString(1),status=r.GetString(2),baselineProfileVersion=r.GetInt32(3),
                proposal=JsonSerializer.Deserialize<JsonElement>(r.GetString(4)),reason=r.GetString(5),sourceType=r.GetString(6),
                sourceReference=r.GetString(7),prescriberInstruction=r.GetString(8),requestedBy=r.GetString(9),
                requestedAt=r.GetFieldValue<DateTimeOffset>(10),effectiveAt=r.GetFieldValue<DateTimeOffset>(11),
                reviewedBy=r.GetString(12),reviewedAt=Date(r,13),reviewReason=r.GetString(14),
                appliedBy=r.GetString(15),appliedAt=Date(r,16)
            },token);
    private async Task<MedicationChangeRow?> Change(Guid medicationId,Guid changeId,bool lockRow,CancellationToken token)
    {
        await using var c=await Command("""
            select id,medication_id,change_type,status,baseline_profile_version,proposal::text,reason,source_type,source_reference,requested_by_user_id,effective_at,reviewed_by_user_id,reviewed_by
            from medication_change_requests where id=@change and medication_id=@medication and organization_id=@organization and branch_id=@branch
            """+(lockRow?" for update":""),token);
        Add(c,"change",changeId);Add(c,"medication",medicationId);
        await using var r=await c.ExecuteReaderAsync(token);
        return await r.ReadAsync(token)?new MedicationChangeRow(r.GetGuid(0),r.GetGuid(1),r.GetString(2),r.GetString(3),r.GetInt32(4),r.GetString(5),r.GetString(6),r.GetString(7),r.GetString(8),r.GetGuid(9),r.GetFieldValue<DateTimeOffset>(10),r.IsDBNull(11)?null:r.GetGuid(11),r.GetString(12)):null;
    }
    private Task<bool> ChangeExists(Guid medicationId,Guid changeId,CancellationToken token) =>
        Scalar<bool>("select exists(select 1 from medication_change_requests where id=@change and medication_id=@medication and organization_id=@organization and branch_id=@branch)",c=>{Add(c,"change",changeId);Add(c,"medication",medicationId);},token);
    private async Task<(int Version,string Status)?> ProfileState(Guid medicationId,CancellationToken token)
    {
        await using var c=await Command("select profile_version,reconciliation_status from medication_safety_profiles where medication_id=@medication and organization_id=@organization and branch_id=@branch for update",token);
        Add(c,"medication",medicationId);
        await using var r=await c.ExecuteReaderAsync(token);
        return await r.ReadAsync(token)?(r.GetInt32(0),r.GetString(1)):null;
    }
    private async Task<Medication?> Medication(Guid id,CancellationToken token)
    {
        var medication=await db.Medications.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,token);
        return medication is not null && tenant.CanAccess(medication.OrganizationId,medication.BranchId)?medication:null;
    }
    private async Task<bool> MedicationExists(Guid id,CancellationToken token)=>await Medication(id,token) is not null;
    private Task<int> Event(Guid change,Guid medication,string action,string detail,CancellationToken token) =>
        Execute("insert into medication_change_events(id,change_id,medication_id,organization_id,branch_id,action,detail,actor_user_id,actor) values(@id,@change,@medication,@organization,@branch,@action,@detail,@actorId,@actor)",
            c=>{Add(c,"id",Guid.NewGuid());Add(c,"change",change);Add(c,"medication",medication);Add(c,"action",action);Add(c,"detail",detail);},token);
    private void Audit(string action,Guid medication)=>db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),action,user.UserName,nameof(Medication),medication,DateTimeOffset.UtcNow,tenant.OrganizationId,tenant.BranchId ?? TenantDefaults.BranchId));
    private async Task<DbCommand> Command(string sql,CancellationToken token)
    {
        var connection=db.Database.GetDbConnection();if(connection.State!=ConnectionState.Open)await connection.OpenAsync(token);
        var c=connection.CreateCommand();c.CommandText=sql;c.Transaction=db.Database.CurrentTransaction?.GetDbTransaction();
        Add(c,"organization",tenant.OrganizationId);Add(c,"branch",tenant.BranchId ?? TenantDefaults.BranchId);
        Add(c,"actorId",user.UserId);Add(c,"actor",user.UserName);return c;
    }
    private async Task<int> Execute(string sql,Action<DbCommand> bind,CancellationToken token){await using var c=await Command(sql,token);bind(c);return await c.ExecuteNonQueryAsync(token);}
    private async Task<T> Scalar<T>(string sql,Action<DbCommand> bind,CancellationToken token){await using var c=await Command(sql,token);bind(c);return (T)(await c.ExecuteScalarAsync(token))!;}
    private async Task<List<T>> Query<T>(string sql,Action<DbCommand> bind,Func<DbDataReader,T> map,CancellationToken token){await using var c=await Command(sql,token);bind(c);await using var r=await c.ExecuteReaderAsync(token);var rows=new List<T>();while(await r.ReadAsync(token))rows.Add(map(r));return rows;}
    private static DateTimeOffset? Date(DbDataReader r,int i)=>r.IsDBNull(i)?null:r.GetFieldValue<DateTimeOffset>(i);
    private static void Add(DbCommand c,string n,object? v){var p=c.CreateParameter();p.ParameterName=n;p.Value=v??DBNull.Value;c.Parameters.Add(p);}
}
public sealed record MedicationChangeProposal(string Dosage,string Route,string Schedule,bool IsPrn,string Indication,string Prescriber,string Form,string Strength,string DoseUnit,string Frequency,string AdministrationInstructions,DateTimeOffset? StartDate,DateTimeOffset? EndDate,int DoseWindowMinutes,int? MaxPrnDoses24h,int? MinPrnIntervalMinutes,string PrnIndication,int? PrnEffectReviewMinutes,bool RequiresWitness,DateTimeOffset? ReviewDueAt);
public sealed record MedicationChangeRequest(string ChangeType,MedicationChangeProposal? Proposal,DateTimeOffset EffectiveAt,string Reason,string SourceType,string SourceReference,string PrescriberInstruction);
public sealed record MedicationChangeReviewRequest(string Decision,string Reason);
internal sealed record MedicationChangeRow(Guid Id,Guid MedicationId,string ChangeType,string Status,int BaselineProfileVersion,string ProposalJson,string Reason,string SourceType,string SourceReference,Guid RequestedByUserId,DateTimeOffset EffectiveAt,Guid? ReviewedByUserId,string ReviewedBy);
