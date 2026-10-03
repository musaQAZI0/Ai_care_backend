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
[Route("api/phase1/medication-safety/medications/{medicationId:guid}/profile")]
public sealed class MedicationInitialVerificationController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user) : ControllerBase
{
    [HttpGet("reviews")]
    public async Task<IActionResult> List(Guid medicationId, CancellationToken token)
    {
        if (await Medication(medicationId, token) is null) return NotFound();
        await using var command = await Command("""
            select id,status,submitted_profile_version,profile_snapshot::text,submitted_by_user_id,submitted_by,submitted_at,
                   reviewed_by,reviewed_at,decision_reason
            from medication_initial_verification_requests
            where medication_id=@medication and organization_id=@organization and branch_id=@branch
            order by submitted_at desc,id desc
            """, token);
        Add(command,"medication",medicationId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new List<object>();
        while (await reader.ReadAsync(token)) rows.Add(new {
            id=reader.GetGuid(0),status=reader.GetString(1),submittedProfileVersion=reader.GetInt32(2),
            profileSnapshot=JsonSerializer.Deserialize<JsonElement>(reader.GetString(3)),
            submittedByUserId=reader.GetGuid(4),submittedBy=reader.GetString(5),submittedAt=reader.GetFieldValue<DateTimeOffset>(6),
            reviewedBy=reader.GetString(7),reviewedAt=Date(reader,8),decisionReason=reader.GetString(9)
        });
        return Ok(rows);
    }

    [HttpPost("submit")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public async Task<IActionResult> Submit(Guid medicationId, CancellationToken token)
    {
        var medication = await Medication(medicationId, token);
        if (medication is null) return NotFound();
        return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var profile = await Profile(medicationId, token);
            if (profile is null) return Conflict(new { message="Save a draft medication profile before submitting it." });
            if (profile.Value.Status is not ("Draft" or "NeedsReview"))
                return Conflict(new { message="Only a draft or returned profile can be submitted for initial verification." });
            if (await Exists("""
                select exists(select 1 from medication_initial_verification_requests
                where medication_id=@medication and organization_id=@organization and branch_id=@branch and status='Pending')
                """, medicationId, token))
                return Conflict(new { message="This profile already has a pending independent review." });
            if (string.IsNullOrWhiteSpace(profile.Value.Indication) || string.IsNullOrWhiteSpace(profile.Value.Prescriber) ||
                string.IsNullOrWhiteSpace(profile.Value.Form) || string.IsNullOrWhiteSpace(profile.Value.Strength) ||
                string.IsNullOrWhiteSpace(profile.Value.SourceType) || string.IsNullOrWhiteSpace(profile.Value.SourceReference))
                return BadRequest(new { message="Complete indication, prescriber, form, strength, source type and source reference before review." });
            if (medication.IsPrn && (string.IsNullOrWhiteSpace(profile.Value.PrnIndication) ||
                profile.Value.MaxPrn is null or < 1 || profile.Value.MinInterval is null or < 0 ||
                profile.Value.EffectReview is null or < 1))
                return BadRequest(new { message="Complete the PRN indication, dose limit, interval and effect review before verification." });

            await Execute("""
                update medication_safety_profiles set reconciliation_status='NeedsReview',
                  last_reconciled_at=null,reconciled_by='',reviewed_by_user_id=null,reviewed_at=null,updated_at=now()
                where medication_id=@medication and organization_id=@organization and branch_id=@branch
                """, medicationId, token);
            var snapshot = await Scalar<string>("select to_jsonb(p)::text from medication_safety_profiles p where p.medication_id=@medication and p.organization_id=@organization and p.branch_id=@branch", medicationId, token);
            var id = Guid.NewGuid();
            await Execute("""
                insert into medication_initial_verification_requests
                  (id,medication_id,organization_id,branch_id,submitted_profile_version,profile_snapshot,status,submitted_by_user_id,submitted_by)
                values(@id,@medication,@organization,@branch,@version,cast(@snapshot as jsonb),'Pending',@actorId,@actor)
                """, medicationId, token, c=>{Add(c,"id",id);Add(c,"version",profile.Value.Version);Add(c,"snapshot",snapshot);});
            await History(medicationId, profile.Value.Status, "NeedsReview", profile.Value.SourceType,
                profile.Value.SourceReference, "", null, "Submitted for independent review.", token);
            Audit("medication.initial_verification_submitted", medicationId);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Created($"/api/phase1/medication-safety/medications/{medicationId}/profile/reviews/{id}",
                new { id, status="Pending", submittedProfileVersion=profile.Value.Version });
        });
    }

    [HttpPost("reviews/{reviewId:guid}")]
    [Authorize(Roles = "CareManager,Administrator")]
    public async Task<IActionResult> Review(Guid medicationId, Guid reviewId, InitialVerificationDecision request, CancellationToken token)
    {
        if (await Medication(medicationId, token) is null) return NotFound();
        if (request.Decision is not ("Approve" or "Reject") || string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new { message="Approve or Reject with a documented reason." });
        return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await using var command = await Command("""
                select id,status,submitted_profile_version,profile_snapshot::text,submitted_by_user_id
                from medication_initial_verification_requests
                where id=@review and medication_id=@medication and organization_id=@organization and branch_id=@branch
                for update
                """, token);
            Add(command,"review",reviewId);Add(command,"medication",medicationId);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return NotFound();
            var status = reader.GetString(1);var version = reader.GetInt32(2);
            var snapshot = reader.GetString(3);var submitter = reader.GetGuid(4);
            await reader.DisposeAsync();
            if (status != "Pending") return Conflict(new { message="Only pending initial verification can be reviewed." });
            if (submitter == user.UserId) return Conflict(new { message="The preparer cannot verify their own medication profile." });
            var profile = await Profile(medicationId, token);
            var approved = request.Decision == "Approve";
            if (approved && (profile is null || profile.Value.Status != "NeedsReview" || profile.Value.Version != version ||
                !await SnapshotMatches(medicationId, snapshot, token)))
                return Conflict(new { message="The submitted medication profile changed. Reject the stale submission and submit its current version." });

            if (approved || profile?.Status == "NeedsReview") await Execute(approved ? """
                update medication_safety_profiles set reconciliation_status='Verified',
                  last_reconciled_at=now(),reconciled_by=@actor,reviewed_by_user_id=@actorId,
                  reviewed_at=now(),profile_version=profile_version+1,updated_at=now()
                where medication_id=@medication and organization_id=@organization and branch_id=@branch
                """ : """
                update medication_safety_profiles set reconciliation_status='Draft',
                  last_reconciled_at=null,reconciled_by='',reviewed_by_user_id=null,
                  reviewed_at=null,profile_version=profile_version+1,updated_at=now()
                where medication_id=@medication and organization_id=@organization and branch_id=@branch
                """, medicationId, token);
            await Execute("""
                update medication_initial_verification_requests set status=@status,
                  reviewed_by_user_id=@actorId,reviewed_by=@actor,reviewed_at=now(),decision_reason=@reason
                where id=@review and medication_id=@medication and organization_id=@organization and branch_id=@branch
                """, medicationId, token, c=>{Add(c,"review",reviewId);Add(c,"status",approved?"Approved":"Rejected");Add(c,"reason",request.Reason.Trim());});
            await History(medicationId, "NeedsReview", approved?"Verified":"Draft", profile?.SourceType ?? "",
                profile?.SourceReference ?? "", user.UserName, DateTimeOffset.UtcNow, request.Reason.Trim(), token);
            Audit(approved?"medication.initial_verification_approved":"medication.initial_verification_rejected", medicationId);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Ok(new { id=reviewId, status=approved?"Approved":"Rejected", verified=approved });
        });
    }

    private async Task<Medication?> Medication(Guid id, CancellationToken token)
    {
        var medication = await db.Medications.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,token);
        return medication is not null && tenant.CanAccess(medication.OrganizationId,medication.BranchId) ? medication : null;
    }
    private async Task<(int Version,string Status,string Indication,string Prescriber,string Form,string Strength,string SourceType,string SourceReference,string PrnIndication,int? MaxPrn,int? MinInterval,int? EffectReview)?> Profile(Guid id,CancellationToken token)
    {
        await using var command = await Command("""
            select profile_version,reconciliation_status,indication,prescriber,form,strength,source_type,source_reference,
              prn_indication,max_prn_doses_24h,min_prn_interval_minutes,prn_effect_review_minutes
            from medication_safety_profiles where medication_id=@medication and organization_id=@organization and branch_id=@branch for update
            """,token);
        Add(command,"medication",id);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? (reader.GetInt32(0),reader.GetString(1),reader.GetString(2),
            reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetString(6),reader.GetString(7),
            reader.GetString(8),Int(reader,9),Int(reader,10),Int(reader,11)) : null;
    }
    private async Task<bool> SnapshotMatches(Guid medication,string snapshot,CancellationToken token)
    {
        await using var command = await Command("""
            select exists(select 1 from medication_safety_profiles p
              where p.medication_id=@medication and p.organization_id=@organization and p.branch_id=@branch
                and to_jsonb(p)=cast(@snapshot as jsonb))
            """,token);
        Add(command,"medication",medication);Add(command,"snapshot",snapshot);
        return (bool)(await command.ExecuteScalarAsync(token))!;
    }
    private Task<bool> Exists(string sql,Guid medication,CancellationToken token) => Scalar<bool>(sql,medication,token);
    private async Task<T> Scalar<T>(string sql,Guid medication,CancellationToken token)
    {
        await using var command=await Command(sql,token);Add(command,"medication",medication);
        return (T)(await command.ExecuteScalarAsync(token))!;
    }
    private async Task History(Guid medication,string previous,string status,string sourceType,string sourceReference,string reviewedBy,DateTimeOffset? reviewedAt,string reason,CancellationToken token) =>
        await Execute("""
            insert into medication_reconciliation_history(id,medication_id,organization_id,branch_id,previous_status,new_status,
              source_type,source_reference,reviewed_by_user_id,reviewed_by,reviewed_at,change_reason,created_by)
            values(@id,@medication,@organization,@branch,@previous,@status,@sourceType,@sourceReference,
              @reviewerId,@reviewedBy,@reviewedAt,@reason,@actor)
            """,medication,token,c=>{Add(c,"id",Guid.NewGuid());Add(c,"previous",previous);Add(c,"status",status);
                Add(c,"sourceType",sourceType);Add(c,"sourceReference",sourceReference);Add(c,"reviewerId",reviewedAt is null?null:user.UserId);
                Add(c,"reviewedBy",reviewedBy);Add(c,"reviewedAt",reviewedAt);Add(c,"reason",reason);});
    private void Audit(string action,Guid medication) =>
        db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),action,user.UserName,nameof(Medication),medication,
            DateTimeOffset.UtcNow,tenant.OrganizationId,tenant.BranchId??TenantDefaults.BranchId));
    private async Task<int> Execute(string sql,Guid medication,CancellationToken token,Action<DbCommand>? bind=null)
    {
        await using var command=await Command(sql,token);Add(command,"medication",medication);bind?.Invoke(command);
        return await command.ExecuteNonQueryAsync(token);
    }
    private async Task<DbCommand> Command(string sql,CancellationToken token)
    {
        var connection=db.Database.GetDbConnection();
        if(connection.State!=ConnectionState.Open)await connection.OpenAsync(token);
        var command=connection.CreateCommand();command.CommandText=sql;
        command.Transaction=db.Database.CurrentTransaction?.GetDbTransaction();
        Add(command,"organization",tenant.OrganizationId);Add(command,"branch",tenant.BranchId??TenantDefaults.BranchId);
        Add(command,"actorId",user.UserId);Add(command,"actor",user.UserName);
        return command;
    }
    private static int? Int(DbDataReader reader,int i)=>reader.IsDBNull(i)?null:reader.GetInt32(i);
    private static DateTimeOffset? Date(DbDataReader reader,int i)=>reader.IsDBNull(i)?null:reader.GetFieldValue<DateTimeOffset>(i);
    private static void Add(DbCommand command,string name,object? value){var p=command.CreateParameter();p.ParameterName=name;p.Value=value??DBNull.Value;command.Parameters.Add(p);}
}
public sealed record InitialVerificationDecision(string Decision,string Reason);
