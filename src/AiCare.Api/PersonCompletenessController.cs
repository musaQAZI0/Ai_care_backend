using System.Data;
using System.Data.Common;
using System.Text.Json;
using AiCare.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AiCare.Api;
public sealed partial class PersonOnboardingController
{
    [HttpGet("remediation")]
    public async Task<IActionResult> Remediation(Guid serviceUserId, CancellationToken token)
    {
        var p = await FindAccessiblePerson(serviceUserId, token); if (p?.BranchId is not Guid branch) return NotFound();
        var status = await BuildStatus(p, token);
        var history = await RemediationRows("select * from person_completeness_events where service_user_id=@person and organization_id=@org and branch_id=@branch order by check_key,revision desc",
            serviceUserId, branch, _ => { }, token);
        var owners = await RemediationRows("""
            select "Id" id,"UserName" name from "AppUsers" where "OrganizationId"=@org and "IsActive"
              and "Role" in ('CareCoordinator','CareManager','Administrator')
              and ("BranchId"=@branch or ("BranchId" is null and "Role"='Administrator')) order by "UserName"
            """, serviceUserId, branch, _ => { }, token);
        var checks = status.Checks.Concat(await DocumentCompletenessChecks(serviceUserId, branch, token)).ToArray();
        return Ok(new { checks, history, owners, asOf = DateTimeOffset.UtcNow });
    }

    [HttpPost("remediation/{checkKey}")]
    public async Task<IActionResult> RecordRemediation(Guid serviceUserId, string checkKey, CompletenessEventRequest r, CancellationToken token)
    {
        if (_currentUser.UserId is null) return Unauthorized();
        if (r.RequestId == Guid.Empty || r.ExpectedRevision < 0 || r.OwnerId == Guid.Empty || r.DueAt == default || string.IsNullOrWhiteSpace(r.Evidence) || r.Evidence.Length > 2000
            || r.Action is not ("Assigned" or "ExceptionAccepted" or "Resolved")
            || (r.Action == "ExceptionAccepted") != r.ExceptionExpiresAt.HasValue)
            return BadRequest(new { message = "Provide the current check revision, action, owner, deadline and evidence. Exceptions also require an expiry." });
        if (r.Action == "ExceptionAccepted" && !_currentUser.HasAnyRole(UserRole.Administrator, UserRole.CareManager)) return Forbid();
        try { return await _context.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () => {
            await using var tx = await _context.Database.BeginTransactionAsync(token);
            var p = await FindAccessiblePerson(serviceUserId, token); if (p?.BranchId is not Guid branch) return NotFound();
            await using (var gate = await RemediationCommand("select pg_advisory_xact_lock(hashtextextended(cast(@person as text),29))", serviceUserId, branch, token))
                await gate.ExecuteNonQueryAsync(token);
            var snapshot = JsonSerializer.Serialize(new { checkKey, request = r });
            var replay = await RemediationRows("""
                select request_snapshot=cast(@snapshot as jsonb) and created_by=@actor same from person_completeness_events
                where id=@id and service_user_id=@person and organization_id=@org and branch_id=@branch
                """, serviceUserId, branch, c => { RemediationAdd(c, "id", r.RequestId); RemediationAdd(c, "snapshot", snapshot); }, token);
            if (replay.Count > 0) return replay[0].GetProperty("same").GetBoolean() ? Ok(new { id = r.RequestId, replayed = true })
                : Conflict(new { message = "Request ID has different content or author." });
            var check = (await BuildStatus(p, token)).Checks.Concat(await DocumentCompletenessChecks(serviceUserId, branch, token)).SingleOrDefault(c => c.Key == checkKey);
            if (check is null) return BadRequest(new { message = "Unknown completeness check." });
            if (r.Action == "Resolved" && !check.Complete) return Conflict(new { message = "The source evidence is still missing. Record remediation or a reviewed exception instead." });
            if ((r.Action != "Resolved" && r.DueAt <= DateTimeOffset.UtcNow)
                || (r.ExceptionExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow))
                return BadRequest(new { message = "Remediation deadlines and exception expiry must be in the future." });
            var latest = await RemediationRows("""
                select coalesce(max(revision),0) revision from person_completeness_events
                where service_user_id=@person and organization_id=@org and branch_id=@branch and check_key=@key
                """, serviceUserId, branch, c => RemediationAdd(c, "key", checkKey), token);
            if (latest[0].GetProperty("revision").GetInt32() != r.ExpectedRevision) return Conflict(new { message = "This review changed. Refresh before saving." });
            await using var command = await RemediationCommand("""
                insert into person_completeness_events(id,service_user_id,organization_id,branch_id,check_key,revision,action,owner_id,owner_name,due_at,
                  evidence,exception_expires_at,check_snapshot,created_by,created_by_name,request_snapshot)
                select @id,@person,@org,@branch,@key,@revision,@action,u."Id",u."UserName",@due,@evidence,@expiry,cast(@check as jsonb),@actor,@name,cast(@snapshot as jsonb)
                from "AppUsers" u where u."Id"=@owner and u."OrganizationId"=@org and u."IsActive" and u."Role" in ('CareCoordinator','CareManager','Administrator')
                  and (u."BranchId"=@branch or (u."BranchId" is null and u."Role"='Administrator'))
                """, serviceUserId, branch, token);
            RemediationAdd(command, "id", r.RequestId); RemediationAdd(command, "key", checkKey); RemediationAdd(command, "revision", r.ExpectedRevision + 1);
            RemediationAdd(command, "action", r.Action); RemediationAdd(command, "owner", r.OwnerId); RemediationAdd(command, "due", r.DueAt.ToUniversalTime());
            RemediationAdd(command, "evidence", r.Evidence.Trim()); RemediationAdd(command, "expiry", r.ExceptionExpiresAt?.ToUniversalTime(), DbType.DateTimeOffset);
            RemediationAdd(command, "check", JsonSerializer.Serialize(new { key = check.Key, complete = check.Complete, label = check.Label }));
            RemediationAdd(command, "name", _currentUser.UserName); RemediationAdd(command, "snapshot", snapshot);
            if (await command.ExecuteNonQueryAsync(token) != 1) return BadRequest(new { message = "Select an active accountable owner in this branch." });
            _context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "person.completeness_" + r.Action, _currentUser.UserName, "CompletenessReview",
                r.RequestId, DateTimeOffset.UtcNow, _tenant.OrganizationId, branch)); await _context.SaveChangesAsync(token); await tx.CommitAsync(token);
            return Created($"/api/phase1/service-users/{serviceUserId}/onboarding/remediation/{checkKey}/{r.RequestId}", new { id = r.RequestId, revision = r.ExpectedRevision + 1 });
        }); }
        catch (Npgsql.PostgresException e) when (e.SqlState is "23505" or "23514")
        { return Conflict(new { message = "Review state or owner eligibility changed. Refresh before saving." }); }
    }
    private async Task<List<JsonElement>> RemediationRows(string sql, Guid person, Guid branch, Action<DbCommand> bind, CancellationToken token)
    {
        await using var c = await RemediationCommand($"select row_to_json(q)::text from ({sql})q", person, branch, token); bind(c);
        await using var reader = await c.ExecuteReaderAsync(token); var result = new List<JsonElement>();
        while (await reader.ReadAsync(token)) result.Add(JsonSerializer.Deserialize<JsonElement>(reader.GetString(0))); return result;
    }
    private async Task<DbCommand> RemediationCommand(string sql, Guid person, Guid branch, CancellationToken token)
    {
        var connection = _context.Database.GetDbConnection(); if (connection.State != ConnectionState.Open) await connection.OpenAsync(token);
        var c = connection.CreateCommand(); c.Transaction = _context.Database.CurrentTransaction?.GetDbTransaction(); c.CommandText = sql;
        RemediationAdd(c, "person", person); RemediationAdd(c, "branch", branch); RemediationAdd(c, "org", _tenant.OrganizationId);
        RemediationAdd(c, "actor", _currentUser.UserId, DbType.Guid); return c;
    }
    private static void RemediationAdd(DbCommand c, string name, object? value, DbType? type = null)
    { var p = c.CreateParameter(); p.ParameterName = name; if (type.HasValue) p.DbType = type.Value; p.Value = value ?? DBNull.Value; c.Parameters.Add(p); }
}
public sealed record CompletenessEventRequest(Guid RequestId, int ExpectedRevision, string Action, Guid OwnerId,
    DateTimeOffset DueAt, string Evidence, DateTimeOffset? ExceptionExpiresAt = null);
