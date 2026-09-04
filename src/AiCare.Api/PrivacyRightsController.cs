using System.Data;
using System.Data.Common;
using System.Text.Json;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[ApiController]
[Authorize(Roles = "Administrator,CareManager")]
[Route("api/phase1/privacy-rights")]
public sealed class PrivacyRightsController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Workspace(CancellationToken ct) => Ok(new
    {
        requests = await Rows("select id,service_user_id,request_type,requester_name,requester_relationship,status,identity_status,owner,received_at,due_at,completed_at,decision from privacy_requests where organization_id=@organization and branch_id=@branch order by received_at desc", ct),
        restrictions = await Rows("select id,service_user_id,privacy_request_id,scope,reason,status,applied_by,applied_at,lifted_by,lifted_at,lift_evidence from processing_restrictions where organization_id=@organization and branch_id=@branch order by applied_at desc", ct),
        holds = await Rows("select id,service_user_id,scope,reason,authority,status,review_due_at,created_by,created_at,release_requested_by,released_by,released_at from legal_holds where organization_id=@organization and (branch_id is null or branch_id=@branch) order by created_at desc", ct)
    });

    [HttpGet("requests/{id:guid}")]
    public async Task<IActionResult> GetCase(Guid id, CancellationToken ct)
    {
        if (!await RequestExists(id, ct)) return NotFound();
        return Ok(new
        {
            request = (await Rows("select * from privacy_requests where id=@id and organization_id=@organization and branch_id=@branch", c => Add(c, "id", id), ct)).Single(),
            records = await Rows("select id,record_type,record_id,field_name,discovery_reference,review_action,review_reason,reviewed_by,reviewed_at from privacy_request_records where request_id=@id order by record_type", c => Add(c, "id", id), ct),
            disclosures = await Rows("select id,version,manifest_json,generated_by,generated_at,released_by,released_at,release_evidence from privacy_disclosures where request_id=@id order by version desc", c => Add(c, "id", id), ct),
            events = await Rows("select id,event_type,detail,actor,occurred_at from privacy_case_events where request_id=@id and organization_id=@organization order by occurred_at", c => Add(c, "id", id), ct)
        });
    }

    [HttpPost("requests")]
    public async Task<IActionResult> Create(CreatePrivacyRequest request, CancellationToken ct)
    {
        if (request.RequestType is not ("SubjectAccess" or "Rectification" or "Restriction" or "Erasure" or "Portability") || string.IsNullOrWhiteSpace(request.RequesterName) || string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new { message = "Valid request type, requester and reason are required." });
        if (!await PersonExists(request.ServiceUserId, ct)) return NotFound();
        var id = Guid.NewGuid();
        var received = request.ReceivedAt ?? DateTimeOffset.UtcNow;
        await Exec("insert into privacy_requests(id,organization_id,branch_id,service_user_id,request_type,requester_name,requester_relationship,request_channel,reason,received_at,due_at,created_by) values(@id,@organization,@branch,@person,@type,@name,@relationship,@channel,@reason,@received,@due,@actor)", c =>
        {
            Add(c, "id", id); Add(c, "person", request.ServiceUserId); Add(c, "type", request.RequestType);
            Add(c, "name", request.RequesterName.Trim()); Add(c, "relationship", request.RequesterRelationship.Trim());
            Add(c, "channel", request.RequestChannel.Trim()); Add(c, "reason", request.Reason.Trim());
            Add(c, "received", received); Add(c, "due", received.AddDays(30));
        }, ct);
        await Event(id, "Received", "Privacy rights request recorded", ct);
        Audit("privacy.request_received", "PrivacyRequest", id);
        await db.SaveChangesAsync(ct);
        return Created("", new { id, dueAt = received.AddDays(30) });
    }

    [HttpPost("requests/{id:guid}/verify")]
    public async Task<IActionResult> Verify(Guid id, VerifyPrivacyRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.IdentityEvidence) || string.IsNullOrWhiteSpace(request.AuthorityEvidence))
            return BadRequest(new { message = "Identity and authority evidence are required." });
        var changed = await Exec("update privacy_requests set identity_status='Verified',identity_evidence=@identity,authority_evidence=@authority,status='Verified' where id=@id and organization_id=@organization and branch_id=@branch and identity_status='Pending'", c =>
        { Add(c, "id", id); Add(c, "identity", request.IdentityEvidence); Add(c, "authority", request.AuthorityEvidence); }, ct);
        if (changed == 0) return Conflict(new { message = "Only a pending identity check can be verified." });
        await Event(id, "IdentityVerified", "Identity and requester authority verified", ct);
        Audit("privacy.identity_verified", "PrivacyRequest", id);
        await db.SaveChangesAsync(ct);
        return Ok();
    }

    [HttpPost("requests/{id:guid}/assign")]
    public async Task<IActionResult> Assign(Guid id, AssignPrivacyRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Owner)) return BadRequest(new { message = "Owner is required." });
        var changed = await Exec("update privacy_requests set owner=@owner,status='InProgress' where id=@id and organization_id=@organization and branch_id=@branch and identity_status='Verified' and status in ('Verified','Reopened','InProgress')", c => { Add(c, "id", id); Add(c, "owner", request.Owner.Trim()); }, ct);
        if (changed == 0) return Conflict(new { message = "Verified identity is required before assignment." });
        await Event(id, "Assigned", $"Case assigned to {request.Owner.Trim()}", ct);
        Audit("privacy.request_assigned", "PrivacyRequest", id);
        await db.SaveChangesAsync(ct);
        return Ok();
    }

    [HttpPost("requests/{id:guid}/discover")]
    public async Task<IActionResult> Discover(Guid id, CancellationToken ct)
    {
        if (!await Exists("select exists(select 1 from privacy_requests where id=@id and organization_id=@organization and branch_id=@branch and identity_status='Verified' and owner<>'')", c => Add(c, "id", id), ct))
            return Conflict(new { message = "Verified identity and an owner are required before discovery." });
        var person = await ScalarGuid("select service_user_id from privacy_requests where id=@id", c => Add(c, "id", id), ct);
        var sources = new[] { ("ServiceUser", "ServiceUsers", "Id"), ("PersonRecord", "PersonRecords", "ServiceUserId"), ("Assessment", "CareAssessments", "ServiceUserId"), ("Risk", "RiskAssessments", "ServiceUserId"), ("CarePlan", "CarePlans", "ServiceUserId"), ("Visit", "Visits", "ServiceUserId"), ("Medication", "Medications", "ServiceUserId"), ("CareNote", "CareNotes", "ServiceUserId"), ("Observation", "HealthObservations", "ServiceUserId"), ("Incident", "Incidents", "ServiceUserId"), ("Document", "Documents", "ServiceUserId"), ("Invoice", "Invoices", "ServiceUserId") };
        var created = 0;
        foreach (var (type, table, personColumn) in sources)
        {
            var ids = await Query($"select \"Id\" from \"{table}\" where \"OrganizationId\"=@organization and \"{personColumn}\"=@person", c => Add(c, "person", person), r => r.GetGuid(0), ct);
            foreach (var recordId in ids)
                created += await Exec("insert into privacy_request_records(id,request_id,record_type,record_id,discovery_reference) select @row,@id,@type,@record,@reference where not exists(select 1 from privacy_request_records where request_id=@id and record_type=@type and record_id=@record)", c =>
                { Add(c, "row", Guid.NewGuid()); Add(c, "id", id); Add(c, "type", type); Add(c, "record", recordId); Add(c, "reference", $"{type}:{recordId}"); }, ct);
        }
        await Event(id, "DiscoveryCompleted", $"{created} records added to the review manifest", ct);
        Audit("privacy.discovery_completed", "PrivacyRequest", id);
        await db.SaveChangesAsync(ct);
        return Ok(new { created });
    }

    [HttpPost("requests/{id:guid}/records/{recordId:guid}/review")]
    public async Task<IActionResult> Review(Guid id, Guid recordId, ReviewPrivacyRecord request, CancellationToken ct)
    {
        if (request.Action is not ("Disclose" or "Redact" or "Exempt") || string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new { message = "Disclosure action and review reason are required." });
        var changed = await Exec("update privacy_request_records set review_action=@action,review_reason=@reason,reviewed_by=@actor,reviewed_at=now() where id=@record and request_id=@id and exists(select 1 from privacy_requests where id=@id and organization_id=@organization and branch_id=@branch)", c =>
        { Add(c, "id", id); Add(c, "record", recordId); Add(c, "action", request.Action); Add(c, "reason", request.Reason); }, ct);
        if (changed == 0) return NotFound();
        await Event(id, "RecordReviewed", $"Record {recordId} marked {request.Action}", ct);
        return Ok();
    }

    [HttpPost("requests/{id:guid}/pack")]
    public async Task<IActionResult> GeneratePack(Guid id, CancellationToken ct)
    {
        if (!await RequestExists(id, ct)) return NotFound();
        if (await Exists("select exists(select 1 from privacy_request_records where request_id=@id and review_action='Pending')", c => Add(c, "id", id), ct))
            return Conflict(new { message = "Every discovered record must be reviewed before pack generation." });
        var count = await Scalar("select count(*) from privacy_request_records where request_id=@id", c => Add(c, "id", id), ct);
        if (count == 0) return Conflict(new { message = "Discovery must identify records before pack generation." });
        var version = (int)await Scalar("select coalesce(max(version),0)+1 from privacy_disclosures where request_id=@id", c => Add(c, "id", id), ct);
        var disclosureId = Guid.NewGuid();
        var manifest = JsonSerializer.Serialize(new { requestId = id, version, recordCount = count, generatedAt = DateTimeOffset.UtcNow });
        await Exec("insert into privacy_disclosures(id,request_id,version,manifest_json,generated_by) values(@disclosure,@id,@version,cast(@manifest as jsonb),@actor);update privacy_requests set status='PackReady' where id=@id", c =>
        { Add(c, "disclosure", disclosureId); Add(c, "id", id); Add(c, "version", version); Add(c, "manifest", manifest); }, ct);
        await Event(id, "PackGenerated", $"Disclosure pack version {version} generated", ct);
        Audit("privacy.pack_generated", "PrivacyRequest", id);
        await db.SaveChangesAsync(ct);
        return Ok(new { id = disclosureId, version });
    }

    [HttpPost("requests/{id:guid}/release")]
    public async Task<IActionResult> Release(Guid id, ReleasePrivacyPack request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ReleaseEvidence)) return BadRequest(new { message = "Release evidence is required." });
        var changed = await Exec("update privacy_disclosures set released_by=@actor,released_at=now(),release_evidence=@evidence where id=(select id from privacy_disclosures where request_id=@id and released_at is null order by version desc limit 1);update privacy_requests set status='Released',completed_at=now(),decision=@decision where id=@id and organization_id=@organization and branch_id=@branch and status='PackReady'", c =>
        { Add(c, "id", id); Add(c, "evidence", request.ReleaseEvidence); Add(c, "decision", request.Decision); }, ct);
        if (changed < 2) return Conflict(new { message = "A generated unreleased pack is required." });
        await Event(id, "Released", "Disclosure pack released through the recorded secure channel", ct);
        Audit("privacy.pack_released", "PrivacyRequest", id);
        await db.SaveChangesAsync(ct);
        return Ok();
    }

    [HttpPost("requests/{id:guid}/transition")]
    public async Task<IActionResult> Transition(Guid id, TransitionPrivacyRequest request, CancellationToken ct)
    {
        var next = request.Action switch { "Close" => "Closed", "Reopen" => "Reopened", "Refuse" => "Refused", _ => "" };
        if (next == "" || string.IsNullOrWhiteSpace(request.Evidence)) return BadRequest(new { message = "Valid action and evidence are required." });
        var allowed = request.Action == "Close" ? "status='Released'" : request.Action == "Reopen" ? "status in ('Closed','Refused')" : "status not in ('Closed','Released')";
        var changed = await Exec($"update privacy_requests set status=@next,decision=@evidence,completed_at=case when @next in ('Closed','Refused') then now() else null end where id=@id and organization_id=@organization and branch_id=@branch and {allowed}", c =>
        { Add(c, "id", id); Add(c, "next", next); Add(c, "evidence", request.Evidence); }, ct);
        if (changed == 0) return Conflict(new { message = "The requested transition is not valid for the current state." });
        await Event(id, next, request.Evidence, ct);
        Audit($"privacy.request_{next.ToLowerInvariant()}", "PrivacyRequest", id);
        await db.SaveChangesAsync(ct);
        return Ok();
    }

    [HttpPost("restrictions")]
    public async Task<IActionResult> Restrict(ProcessingRestrictionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Scope) || string.IsNullOrWhiteSpace(request.Reason)) return BadRequest(new { message = "Scope and reason are required." });
        if (!await PersonExists(request.ServiceUserId, ct)) return NotFound();
        var id = Guid.NewGuid();
        await Exec("insert into processing_restrictions(id,organization_id,branch_id,service_user_id,privacy_request_id,scope,reason,applied_by) values(@id,@organization,@branch,@person,@request,@scope,@reason,@actor)", c =>
        { Add(c, "id", id); Add(c, "person", request.ServiceUserId); Add(c, "request", request.PrivacyRequestId); Add(c, "scope", request.Scope); Add(c, "reason", request.Reason); }, ct);
        Audit("privacy.restriction_applied", "ProcessingRestriction", id);
        await db.SaveChangesAsync(ct);
        return Created("", new { id });
    }

    [HttpPost("restrictions/{id:guid}/lift")]
    public async Task<IActionResult> LiftRestriction(Guid id, EvidenceRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Evidence)) return BadRequest(new { message = "Lift evidence is required." });
        var changed = await Exec("update processing_restrictions set status='Lifted',lifted_by=@actor,lifted_at=now(),lift_evidence=@evidence where id=@id and organization_id=@organization and branch_id=@branch and status='Active'", c => { Add(c, "id", id); Add(c, "evidence", request.Evidence); }, ct);
        if (changed == 0) return Conflict();
        Audit("privacy.restriction_lifted", "ProcessingRestriction", id);
        await db.SaveChangesAsync(ct);
        return Ok();
    }

    [HttpPost("legal-holds")]
    public async Task<IActionResult> CreateHold(LegalHoldRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Scope) || string.IsNullOrWhiteSpace(request.Reason) || string.IsNullOrWhiteSpace(request.Authority) || request.ReviewDueAt <= DateTimeOffset.UtcNow)
            return BadRequest(new { message = "Scope, reason, authority and a future review are required." });
        if (request.ServiceUserId is not null && !await PersonExists(request.ServiceUserId.Value, ct)) return NotFound();
        var id = Guid.NewGuid();
        await Exec("insert into legal_holds(id,organization_id,branch_id,service_user_id,scope,reason,authority,review_due_at,created_by) values(@id,@organization,@holdBranch,@person,@scope,@reason,@authority,@review,@actor)", c =>
        { Add(c, "id", id); Add(c, "holdBranch", request.OrganizationWide ? null : tenant.BranchId ?? TenantDefaults.BranchId); Add(c, "person", request.ServiceUserId); Add(c, "scope", request.Scope); Add(c, "reason", request.Reason); Add(c, "authority", request.Authority); Add(c, "review", request.ReviewDueAt); }, ct);
        Audit("privacy.legal_hold_created", "LegalHold", id);
        await db.SaveChangesAsync(ct);
        return Created("", new { id });
    }

    [HttpPost("legal-holds/{id:guid}/release-request")]
    public async Task<IActionResult> RequestHoldRelease(Guid id, EvidenceRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Evidence)) return BadRequest();
        var changed = await Exec("update legal_holds set status='ReleaseRequested',release_requested_by=@actor,release_requested_at=now(),release_evidence=@evidence where id=@id and organization_id=@organization and status='Active' and (branch_id is null or branch_id=@branch)", c => { Add(c, "id", id); Add(c, "evidence", request.Evidence); }, ct);
        if (changed == 0) return Conflict();
        Audit("privacy.legal_hold_release_requested", "LegalHold", id);
        await db.SaveChangesAsync(ct);
        return Ok();
    }

    [HttpPost("legal-holds/{id:guid}/release")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> ReleaseHold(Guid id, EvidenceRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Evidence)) return BadRequest();
        var changed = await Exec("update legal_holds set status='Released',released_by=@actor,released_at=now(),release_evidence=@evidence where id=@id and organization_id=@organization and status='ReleaseRequested'", c => { Add(c, "id", id); Add(c, "evidence", request.Evidence); }, ct);
        if (changed == 0) return Conflict();
        Audit("privacy.legal_hold_released", "LegalHold", id);
        await db.SaveChangesAsync(ct);
        return Ok();
    }

    private Task Event(Guid id, string type, string detail, CancellationToken ct) => Exec("insert into privacy_case_events(id,request_id,organization_id,event_type,detail,actor) values(@event,@id,@organization,@type,@detail,@actor)", c => { Add(c, "event", Guid.NewGuid()); Add(c, "id", id); Add(c, "type", type); Add(c, "detail", detail); }, ct);
    private Task<bool> RequestExists(Guid id, CancellationToken ct) => Exists("select exists(select 1 from privacy_requests where id=@id and organization_id=@organization and branch_id=@branch)", c => Add(c, "id", id), ct);
    private Task<bool> PersonExists(Guid id, CancellationToken ct) => Exists("select exists(select 1 from \"ServiceUsers\" where \"Id\"=@id and \"OrganizationId\"=@organization and \"BranchId\"=@branch)", c => Add(c, "id", id), ct);
    private async Task<Guid> ScalarGuid(string sql, Action<DbCommand> bind, CancellationToken ct) { await using var command = await Command(sql, ct); bind(command); return (Guid)(await command.ExecuteScalarAsync(ct))!; }
    private async Task<long> Scalar(string sql, Action<DbCommand> bind, CancellationToken ct) { await using var command = await Command(sql, ct); bind(command); return Convert.ToInt64(await command.ExecuteScalarAsync(ct)); }
    private async Task<bool> Exists(string sql, Action<DbCommand> bind, CancellationToken ct) { await using var command = await Command(sql, ct); bind(command); return Convert.ToBoolean(await command.ExecuteScalarAsync(ct)); }
    private async Task<int> Exec(string sql, Action<DbCommand> bind, CancellationToken ct) { await using var command = await Command(sql, ct); bind(command); return await command.ExecuteNonQueryAsync(ct); }
    private Task<List<Dictionary<string, object?>>> Rows(string sql, CancellationToken ct) => Rows(sql, _ => { }, ct);
    private Task<List<Dictionary<string, object?>>> Rows(string sql, Action<DbCommand> bind, CancellationToken ct) => Query(sql, bind, reader => { var row = new Dictionary<string, object?>(); for (var i = 0; i < reader.FieldCount; i++) row[Camel(reader.GetName(i))] = reader.IsDBNull(i) ? null : reader.GetValue(i); return row; }, ct);
    private async Task<List<T>> Query<T>(string sql, Action<DbCommand> bind, Func<DbDataReader, T> map, CancellationToken ct) { await using var command = await Command(sql, ct); bind(command); await using var reader = await command.ExecuteReaderAsync(ct); var rows = new List<T>(); while (await reader.ReadAsync(ct)) rows.Add(map(reader)); return rows; }
    private async Task<DbCommand> Command(string sql, CancellationToken ct) { var connection = db.Database.GetDbConnection(); if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct); var command = connection.CreateCommand(); command.CommandText = sql; Add(command, "organization", tenant.OrganizationId); Add(command, "branch", tenant.BranchId ?? TenantDefaults.BranchId); Add(command, "actor", user.UserName); return command; }
    private void Audit(string action, string entityType, Guid id) => db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), action, user.UserName, entityType, id, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    private static void Add(DbCommand command, string name, object? value) { if (command.Parameters.Contains(name)) return; var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; command.Parameters.Add(parameter); }
    private static string Camel(string value) { var parts = value.Split('_'); return parts[0] + string.Concat(parts.Skip(1).Select(part => char.ToUpperInvariant(part[0]) + part[1..])); }
}

public sealed record CreatePrivacyRequest(Guid ServiceUserId, string RequestType, string RequesterName, string RequesterRelationship, string RequestChannel, string Reason, DateTimeOffset? ReceivedAt);
public sealed record VerifyPrivacyRequest(string IdentityEvidence, string AuthorityEvidence);
public sealed record AssignPrivacyRequest(string Owner);
public sealed record ReviewPrivacyRecord(string Action, string Reason);
public sealed record ReleasePrivacyPack(string ReleaseEvidence, string Decision);
public sealed record TransitionPrivacyRequest(string Action, string Evidence);
public sealed record ProcessingRestrictionRequest(Guid ServiceUserId, Guid? PrivacyRequestId, string Scope, string Reason);
public sealed record EvidenceRequest(string Evidence);
public sealed record LegalHoldRequest(Guid? ServiceUserId, string Scope, string Reason, string Authority, DateTimeOffset ReviewDueAt, bool OrganizationWide);
