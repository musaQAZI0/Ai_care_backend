using System.Data;
using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AiCare.Api;

[ApiController, Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
[RequestSizeLimit(2 * 1024 * 1024)]
[Route("api/phase1/assessment-templates")]
public sealed class AssessmentTemplatesController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken token) => Ok(await Read("", _ => { }, token));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken token)
    {
        var rows = await Read("and id=@id", c => Add(c, "id", id), token);
        return rows.Count == 1 ? Ok(rows[0]) : NotFound();
    }

    [HttpPost]
    public Task<IActionResult> Create(AssessmentTemplateRequest r, CancellationToken token)
    {
        if (user.UserId is null) return Task.FromResult<IActionResult>(Unauthorized());
        if (r.OrganizationWide && !user.IsAdministrator) return Task.FromResult<IActionResult>(Forbid());
        if (!r.OrganizationWide && tenant.BranchId is null) return Task.FromResult<IActionResult>(BadRequest(new { message = "Select a branch or an organisation-wide template scope." }));
        if (r.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(r.Key) || !Regex.IsMatch(r.Key, "^[a-z][a-z0-9-]{0,63}$") ||
            string.IsNullOrWhiteSpace(r.Title) || r.Title.Length > 200 || string.IsNullOrWhiteSpace(r.AssessmentType) || r.AssessmentType.Length > 200 ||
            string.IsNullOrWhiteSpace(r.ChangeReason) || string.IsNullOrWhiteSpace(r.ImpactReview))
            return Task.FromResult<IActionResult>(BadRequest(new { message = "Key, title, assessment type, change reason and impact review are required." }));
        if (AssessmentTemplateValidation.Definition(r.Fields) is { } error) return Task.FromResult<IActionResult>(BadRequest(new { message = error }));
        return Transaction(async () =>
        {
            await Lock(token);
            var snapshot = JsonSerializer.Serialize(r);
            await using (var replay = await Command("select request_snapshot=cast(@request as jsonb) and created_by=@actor from assessment_template_versions where id=@id and organization_id=@org and (branch_id is null or @wide or branch_id=@branch)", token))
            {
                Add(replay, "id", r.RequestId); Add(replay, "request", snapshot);
                if (await replay.ExecuteScalarAsync(token) is bool same)
                    return same ? Ok(new { id = r.RequestId, replayed = true }) : Conflict(new { message = "Request ID already contains a different template draft." });
            }
            if (r.SourceVersionId is Guid source)
            {
                var sources = await Read("and id=@id", c => Add(c, "id", source), token);
                if (sources.Count != 1) return NotFound();
            }
            await using var command = await Command("""
                insert into assessment_template_versions(id,organization_id,branch_id,template_key,title,assessment_type,version,
                  definition_json,source_version_id,change_reason,impact_review,request_snapshot,created_by)
                values(@id,@org,@target,@key,@title,@type,
                  (select coalesce(max(version),0)+1 from assessment_template_versions where organization_id=@org and branch_id is not distinct from @target and template_key=@key),
                  cast(@fields as jsonb),@source,@reason,@impact,cast(@request as jsonb),@actor) returning version
                """, token);
            Add(command, "id", r.RequestId); Add(command, "target", r.OrganizationWide ? null : tenant.BranchId, DbType.Guid);
            Add(command, "key", r.Key); Add(command, "title", r.Title.Trim()); Add(command, "type", r.AssessmentType.Trim());
            Add(command, "fields", JsonSerializer.Serialize(r.Fields)); Add(command, "source", r.SourceVersionId, DbType.Guid);
            Add(command, "reason", r.ChangeReason.Trim()); Add(command, "impact", r.ImpactReview.Trim()); Add(command, "request", snapshot);
            var version = Convert.ToInt32(await command.ExecuteScalarAsync(token));
            await Audit("assessment_template.draft_created", r.RequestId, token);
            return CreatedAtAction(nameof(Get), new { id = r.RequestId }, new { id = r.RequestId, version, status = "Draft" });
        }, token);
    }

    [HttpPost("{id:guid}/submit")]
    public Task<IActionResult> Submit(Guid id, CancellationToken token) => Transaction(async () =>
    {
        await Lock(token);
        await using var c = await Command("update assessment_template_versions set status='InReview',submitted_at=now() where id=@id and organization_id=@org and (branch_id is null or @wide or branch_id=@branch) and status='Draft'", token);
        Add(c, "id", id);
        if (await c.ExecuteNonQueryAsync(token) != 1) return Conflict(new { message = "Select an accessible draft template." });
        await Audit("assessment_template.submitted", id, token);
        return Ok(new { id, status = "InReview" });
    }, token);

    [HttpPost("{id:guid}/approve"), Authorize(Roles = "CareManager,Administrator")]
    public Task<IActionResult> Approve(Guid id, TemplateApprovalRequest r, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(r.Reason)) return Task.FromResult<IActionResult>(BadRequest(new { message = "An approval reason is required." }));
        return Transaction(async () =>
        {
            await Lock(token);
            var rows = await Read("and id=@id", c => Add(c, "id", id), token);
            if (rows.Count != 1) return NotFound();
            var row = rows[0];
            if (row.Status != "InReview" || row.CreatedBy == user.UserId) return Conflict(new { message = "An independent manager must approve a submitted template." });
            // Only organisation admins may publish shared templates for all branches.
            if (row.BranchId is null && !user.IsAdministrator) return Forbid();
            await using (var newer = await Command("select exists(select 1 from assessment_template_versions where organization_id=@org and branch_id is not distinct from @target and template_key=@key and status='Published' and version>@version)", token))
            {
                Add(newer, "target", row.BranchId, DbType.Guid); Add(newer, "key", row.Key); Add(newer, "version", row.Version);
                if (await newer.ExecuteScalarAsync(token) is true) return Conflict(new { message = "A newer template is already published. Copy the latest version instead." });
            }
            await using (var retire = await Command("update assessment_template_versions set status='Retired' where organization_id=@org and branch_id is not distinct from @target and template_key=@key and status='Published'", token))
            {
                Add(retire, "target", row.BranchId, DbType.Guid); Add(retire, "key", row.Key); await retire.ExecuteNonQueryAsync(token);
            }
            await using (var approve = await Command("update assessment_template_versions set status='Published',approved_by=@actor,approved_at=now(),approval_reason=@reason where id=@id and organization_id=@org and status='InReview'", token))
            {
                Add(approve, "id", id); Add(approve, "reason", r.Reason.Trim()); await approve.ExecuteNonQueryAsync(token);
            }
            await Audit("assessment_template.published", id, token);
            return Ok(new { id, status = "Published" });
        }, token);
    }

    private async Task<List<AssessmentTemplateVersion>> Read(string filter, Action<DbCommand> bind, CancellationToken token)
    {
        await using var c = await Command($"""
            select id,branch_id,template_key,title,assessment_type,version,status,definition_json::text,created_by,change_reason,impact_review,source_version_id
            from assessment_template_versions where organization_id=@org and (branch_id is null or @wide or branch_id=@branch)
            {filter} order by template_key,version desc
            """, token);
        bind(c); await using var reader = await c.ExecuteReaderAsync(token); var rows = new List<AssessmentTemplateVersion>();
        while (await reader.ReadAsync(token)) rows.Add(new(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetInt32(5), reader.GetString(6), JsonSerializer.Deserialize<AssessmentField[]>(reader.GetString(7))!, reader.GetGuid(8),
            reader.GetString(9), reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetGuid(11)));
        return rows;
    }

    private async Task<IActionResult> Transaction(Func<Task<IActionResult>> action, CancellationToken token)
    {
        try { return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token); var result = await action();
            if (result is not (ConflictObjectResult or NotFoundResult or ForbidResult or BadRequestObjectResult)) await tx.CommitAsync(token);
            return result;
        }); }
        catch (Npgsql.PostgresException e) when (e.SqlState is "23505" or "23514") { return Conflict(new { message = "Template scope/version changed. Reload before retrying." }); }
    }
    private async Task Lock(CancellationToken token)
    {
        await using var c = await Command("select pg_advisory_xact_lock(hashtextextended(@lock,0))", token);
        Add(c, "lock", $"assessment-templates:{tenant.OrganizationId}"); await c.ExecuteNonQueryAsync(token);
    }
    private async Task<DbCommand> Command(string sql, CancellationToken token)
    {
        var connection = db.Database.GetDbConnection(); if (connection.State != ConnectionState.Open) await connection.OpenAsync(token);
        var c = connection.CreateCommand(); c.Transaction = db.Database.CurrentTransaction?.GetDbTransaction(); c.CommandText = sql;
        Add(c, "org", tenant.OrganizationId); Add(c, "branch", tenant.BranchId ?? Guid.Empty); Add(c, "wide", tenant.IsOrganizationWide); Add(c, "actor", user.UserId, DbType.Guid); return c;
    }
    private static void Add(DbCommand c, string key, object? value, DbType? type = null)
    { var p = c.CreateParameter(); p.ParameterName = key; if (type.HasValue) p.DbType = type.Value; p.Value = value ?? DBNull.Value; c.Parameters.Add(p); }
    private async Task Audit(string action, Guid id, CancellationToken token)
    { db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), action, user.UserName, "AssessmentTemplate", id, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId)); await db.SaveChangesAsync(token); }
}

public sealed record AssessmentTemplateRequest(Guid RequestId, string Key, string Title, string AssessmentType,
    AssessmentField[] Fields, string ChangeReason, string ImpactReview, bool OrganizationWide = false, Guid? SourceVersionId = null);
public sealed record TemplateApprovalRequest(string Reason);
public sealed record AssessmentTemplateVersion(Guid Id, Guid? BranchId, string Key, string Title, string AssessmentType,
    int Version, string Status, AssessmentField[] Fields, Guid CreatedBy, string ChangeReason, string ImpactReview, Guid? SourceVersionId);
