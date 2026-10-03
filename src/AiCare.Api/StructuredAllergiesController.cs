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
[Route("api/phase1/service-users/{serviceUserId:guid}/allergies")]
public sealed class StructuredAllergiesController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid serviceUserId, CancellationToken token)
    {
        if (!await PersonExists(serviceUserId, token)) return NotFound();
        return Ok(await Summary(serviceUserId, token));
    }

    [HttpGet("history")]
    public async Task<IActionResult> History(Guid serviceUserId, CancellationToken token)
    {
        if (!await PersonExists(serviceUserId, token)) return NotFound();
        return Ok(await Query(
            "select id,allergy_id,action,snapshot::text,reason,actor,occurred_at from service_user_allergy_history where service_user_id=@person and organization_id=@organization and branch_id=@branch order by occurred_at desc,id desc",
            c => Add(c, "person", serviceUserId),
            r => new { id = r.GetGuid(0), allergyId = r.IsDBNull(1) ? (Guid?)null : r.GetGuid(1), action = r.GetString(2), snapshot = JsonSerializer.Deserialize<JsonElement>(r.GetString(3)), reason = r.GetString(4), actor = r.GetString(5), occurredAt = r.GetFieldValue<DateTimeOffset>(6) }, token));
    }

    [HttpPost]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public async Task<IActionResult> Create(Guid serviceUserId, AllergyCreateRequest request, CancellationToken token)
    {
        if (!await PersonExists(serviceUserId, token)) return NotFound();
        if (string.IsNullOrWhiteSpace(request.Allergen) || request.Allergen.Length > 250 ||
            string.IsNullOrWhiteSpace(request.Reaction) || request.Reaction.Length > 500 ||
            string.IsNullOrWhiteSpace(request.InformationSource) ||
            !OneOf(request.AllergenType, "Medicine", "Ingredient", "Food", "Environmental", "Other") ||
            !OneOf(request.Severity, "Mild", "Moderate", "Severe", "Unknown") ||
            !OneOf(request.RecordType, "Allergy", "Intolerance", "AdverseReaction"))
            return BadRequest(new { message = "Allergen, reaction, source, type, severity and record type are required." });
        var id = Guid.NewGuid();
        return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () =>
        {
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await LockStatus(serviceUserId, token);
        if (await Scalar<bool>("select exists(select 1 from service_user_allergies where service_user_id=@person and organization_id=@organization and branch_id=@branch and clinical_status='Active' and lower(allergen)=lower(@allergen) and record_type=@recordType)", c => { Add(c,"person",serviceUserId); Add(c,"allergen",request.Allergen.Trim()); Add(c,"recordType",request.RecordType); }, token))
            return Conflict(new { message = "An active record for this allergen and type already exists." });
        await Exec("""
            insert into service_user_allergies(id,service_user_id,organization_id,branch_id,allergen,allergen_type,terminology_code,terminology_system,reaction,severity,record_type,clinical_status,verification_status,information_source,notes,recorded_by_user_id,recorded_by)
            values(@id,@person,@organization,@branch,@allergen,@allergenType,@code,@system,@reaction,@severity,@recordType,'Active','Unverified',@source,@notes,@actorId,@actor)
            """, c => { Add(c,"id",id); Add(c,"person",serviceUserId); Add(c,"allergen",request.Allergen.Trim()); Add(c,"allergenType",request.AllergenType); Add(c,"code",request.TerminologyCode?.Trim() ?? ""); Add(c,"system",request.TerminologySystem?.Trim() ?? ""); Add(c,"reaction",request.Reaction.Trim()); Add(c,"severity",request.Severity); Add(c,"recordType",request.RecordType); Add(c,"source",request.InformationSource.Trim()); Add(c,"notes",request.Notes?.Trim() ?? ""); }, token);
        await SetStatus(serviceUserId, "Unknown", "New allergy requires clinical verification", token);
        await Snapshot(serviceUserId, id, "Created", "", token);
        Audit("allergy.record_created", id);
        await db.SaveChangesAsync(token);
        await tx.CommitAsync(token);
        return Created($"/api/phase1/service-users/{serviceUserId}/allergies/{id}", await Summary(serviceUserId, token));
        });
    }

    [HttpPost("{allergyId:guid}/review")]
    [Authorize(Roles = "CareManager,Administrator")]
    public async Task<IActionResult> Review(Guid serviceUserId, Guid allergyId, AllergyReviewRequest request, CancellationToken token)
    {
        if (!await PersonExists(serviceUserId, token)) return NotFound();
        if (!OneOf(request.ClinicalStatus, "Active", "Resolved", "EnteredInError") ||
            string.IsNullOrWhiteSpace(request.Reason) ||
            (request.ClinicalStatus == "Active" && request.Verified != true))
            return BadRequest(new { message = "A reason and verified active status are required." });
        return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () =>
        {
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await LockStatus(serviceUserId, token);
        var changed = await Exec("""
            update service_user_allergies set clinical_status=@status,verification_status=@verification,reviewed_by_user_id=@actorId,reviewed_by=@actor,reviewed_at=now(),version=version+1
            where id=@id and service_user_id=@person and organization_id=@organization and branch_id=@branch and clinical_status='Active'
            """, c => { Add(c,"id",allergyId); Add(c,"person",serviceUserId); Add(c,"status",request.ClinicalStatus); Add(c,"verification",request.Verified ? "Verified" : "Unverified"); }, token);
        if (changed == 0) return NotFound();
        var active = await Scalar<long>("select count(*) from service_user_allergies where service_user_id=@person and organization_id=@organization and branch_id=@branch and clinical_status='Active' and verification_status='Verified'", c => Add(c,"person",serviceUserId), token);
        var unverified = await Scalar<long>("select count(*) from service_user_allergies where service_user_id=@person and organization_id=@organization and branch_id=@branch and clinical_status='Active' and verification_status='Unverified'", c => Add(c,"person",serviceUserId), token);
        await SetStatus(serviceUserId, unverified > 0 || active == 0 ? "Unknown" : "KnownAllergies", request.Reason.Trim(), token);
        await Snapshot(serviceUserId, allergyId, "Reviewed", request.Reason.Trim(), token);
        Audit("allergy.record_reviewed", allergyId);
        await db.SaveChangesAsync(token);
        await tx.CommitAsync(token);
        return Ok(await Summary(serviceUserId, token));
        });
    }

    [HttpPut("status")]
    [Authorize(Roles = "CareManager,Administrator")]
    public async Task<IActionResult> ReviewStatus(Guid serviceUserId, AllergyStatusRequest request, CancellationToken token)
    {
        if (!await PersonExists(serviceUserId, token)) return NotFound();
        if (!OneOf(request.Status, "Unknown", "KnownAllergies", "NoKnownAllergies") || string.IsNullOrWhiteSpace(request.Source))
            return BadRequest(new { message = "A valid status and information source are required." });
        return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () =>
        {
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await LockStatus(serviceUserId, token);
        var active = await Scalar<long>("select count(*) from service_user_allergies where service_user_id=@person and organization_id=@organization and branch_id=@branch and clinical_status='Active'", c => Add(c,"person",serviceUserId), token);
        var unverified = await Scalar<long>("select count(*) from service_user_allergies where service_user_id=@person and organization_id=@organization and branch_id=@branch and clinical_status='Active' and verification_status='Unverified'", c => Add(c,"person",serviceUserId), token);
        if (request.Status == "NoKnownAllergies" && active > 0) return Conflict(new { message = "Active allergy records must be resolved before confirming no known allergies." });
        if (request.Status == "KnownAllergies" && (active == 0 || unverified > 0)) return Conflict(new { message = "Known allergies requires at least one verified active record and no unverified active records." });
        await SetStatus(serviceUserId, request.Status, request.Source.Trim(), token);
        await Snapshot(serviceUserId, null, "StatusReviewed", request.Source.Trim(), token);
        Audit("allergy.status_reviewed", serviceUserId);
        await db.SaveChangesAsync(token);
        await tx.CommitAsync(token);
        return Ok(await Summary(serviceUserId, token));
        });
    }

    private async Task<object> Summary(Guid person, CancellationToken token)
    {
        var status = await Scalar<string?>("select status from service_user_allergy_status where service_user_id=@person and organization_id=@organization and branch_id=@branch", c => Add(c,"person",person), token) ?? "Unknown";
        var records = await Query("""
            select id,allergen,allergen_type,terminology_code,terminology_system,reaction,severity,record_type,clinical_status,verification_status,information_source,notes,recorded_by,recorded_at,reviewed_by,reviewed_at,version
            from service_user_allergies where service_user_id=@person and organization_id=@organization and branch_id=@branch order by case when clinical_status='Active' then 0 else 1 end,recorded_at desc
            """, c => Add(c,"person",person), r => new { id=r.GetGuid(0), allergen=r.GetString(1), allergenType=r.GetString(2), terminologyCode=r.GetString(3), terminologySystem=r.GetString(4), reaction=r.GetString(5), severity=r.GetString(6), recordType=r.GetString(7), clinicalStatus=r.GetString(8), verificationStatus=r.GetString(9), informationSource=r.GetString(10), notes=r.GetString(11), recordedBy=r.GetString(12), recordedAt=r.GetFieldValue<DateTimeOffset>(13), reviewedBy=r.GetString(14), reviewedAt=r.IsDBNull(15) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(15), version=r.GetInt32(16) }, token);
        return new { serviceUserId=person, status, requiresReview=status=="Unknown" || records.Any(x => x.clinicalStatus=="Active" && x.verificationStatus!="Verified"), records };
    }

    private async Task<bool> PersonExists(Guid id, CancellationToken token)
    {
        var person = await db.ServiceUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        return person is not null && tenant.CanAccess(person.OrganizationId, person.BranchId);
    }
    private async Task LockStatus(Guid person, CancellationToken token)
    {
        await Exec("insert into service_user_allergy_status(service_user_id,organization_id,branch_id,status) values(@person,@organization,@branch,'Unknown') on conflict(service_user_id) do nothing", c=>Add(c,"person",person), token);
        await Scalar<string>("select status from service_user_allergy_status where service_user_id=@person and organization_id=@organization and branch_id=@branch for update", c=>Add(c,"person",person), token);
    }
    private Task<int> SetStatus(Guid person,string status,string source,CancellationToken token) =>
        Exec("update service_user_allergy_status set status=@status,source=@source,reviewed_by_user_id=@actorId,reviewed_by=@actor,reviewed_at=now(),updated_at=now() where service_user_id=@person and organization_id=@organization and branch_id=@branch",
            c=>{Add(c,"person",person);Add(c,"status",status);Add(c,"source",source);},token);
    private async Task Snapshot(Guid person,Guid? allergy,string action,string reason,CancellationToken token)
    {
        var snapshot = JsonSerializer.Serialize(await Summary(person,token));
        await Exec("insert into service_user_allergy_history(id,service_user_id,allergy_id,organization_id,branch_id,action,snapshot,reason,actor_user_id,actor) values(@id,@person,@allergy,@organization,@branch,@action,cast(@snapshot as jsonb),@reason,@actorId,@actor)",
            c=>{Add(c,"id",Guid.NewGuid());Add(c,"person",person);Add(c,"allergy",allergy);Add(c,"action",action);Add(c,"snapshot",snapshot);Add(c,"reason",reason);},token);
    }
    private void Audit(string action,Guid id) => db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),action,user.UserName,"ServiceUserAllergy",id,DateTimeOffset.UtcNow,tenant.OrganizationId,tenant.BranchId ?? TenantDefaults.BranchId));
    private static bool OneOf(string? value,params string[] options) => value is not null && options.Contains(value,StringComparer.Ordinal);
    private async Task<DbCommand> Command(string sql,CancellationToken token)
    {
        var connection=db.Database.GetDbConnection();
        if(connection.State!=ConnectionState.Open) await connection.OpenAsync(token);
        var command=connection.CreateCommand();command.CommandText=sql;command.Transaction=db.Database.CurrentTransaction?.GetDbTransaction();
        Add(command,"organization",tenant.OrganizationId);Add(command,"branch",tenant.BranchId ?? TenantDefaults.BranchId);Add(command,"actorId",user.UserId);Add(command,"actor",user.UserName);
        return command;
    }
    private async Task<int> Exec(string sql,Action<DbCommand> bind,CancellationToken token) { await using var command=await Command(sql,token);bind(command);return await command.ExecuteNonQueryAsync(token); }
    private async Task<T?> Scalar<T>(string sql,Action<DbCommand> bind,CancellationToken token) { await using var command=await Command(sql,token);bind(command);var result=await command.ExecuteScalarAsync(token);return result is null or DBNull ? default : (T)result; }
    private async Task<List<T>> Query<T>(string sql,Action<DbCommand> bind,Func<DbDataReader,T> map,CancellationToken token)
    {
        await using var command=await Command(sql,token);bind(command);await using var reader=await command.ExecuteReaderAsync(token);var rows=new List<T>();while(await reader.ReadAsync(token))rows.Add(map(reader));return rows;
    }
    private static void Add(DbCommand command,string name,object? value) { var parameter=command.CreateParameter();parameter.ParameterName=name;parameter.Value=value ?? DBNull.Value;command.Parameters.Add(parameter); }
}
public sealed record AllergyCreateRequest(string Allergen,string AllergenType,string? TerminologyCode,string? TerminologySystem,string Reaction,string Severity,string RecordType,string InformationSource,string? Notes);
public sealed record AllergyReviewRequest(string ClinicalStatus,bool Verified,string Reason);
public sealed record AllergyStatusRequest(string Status,string Source);
