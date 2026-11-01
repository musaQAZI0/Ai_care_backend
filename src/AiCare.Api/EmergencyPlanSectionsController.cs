using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AiCare.Api;
[ApiController, Authorize(Roles = "CareCoordinator,CareManager,Administrator,CareWorker")]
[Route("api/phase1/care-plans/{planId:guid}/emergency-sections")]
[RequestSizeLimit(32 * 1024)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed partial class EmergencyPlanSectionsController(CareDbContext db, ITenantContext tenant,
    ICurrentUserContext user, IContextualAuthorization authorization) : ControllerBase
{
    private bool Manage => user.HasAnyRole(UserRole.Administrator, UserRole.CareManager, UserRole.CareCoordinator);
    [HttpGet]
    public async Task<IActionResult> Get(Guid planId, CancellationToken token)
    {
        var plan = await Plan(planId, "", token); if (plan is null) return NotFound();
        var history = await Selections(planId, token); var latest = history.FirstOrDefault();
        return Ok(new { planStatus = plan.Status, revision = plan.Revision,
            sections = EmergencyPlanCopy.Sections.Select(s => new { key = s.Key, label = s.Value }),
            selectedSections = latest.ValueKind == JsonValueKind.Undefined ? Array.Empty<string>() : latest.GetProperty("sections_json").Deserialize<string[]>(),
            history = Manage ? history : new List<JsonElement>() });
    }

    [HttpPost, Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public Task<IActionResult> Select(Guid planId, EmergencySectionsRequest r, CancellationToken token)
    {
        if (r.RequestId == Guid.Empty || r.ExpectedPlanRevision < 1 || !EmergencyPlanCopy.ValidSections(r.Sections)
            || string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Length > 2000)
            return Task.FromResult<IActionResult>(BadRequest(new { message = "Select distinct supported sections and provide the current plan revision and a change reason." }));
        return Transaction(async () => {
            var plan = await Plan(planId, " for update", token); if (plan is null) return NotFound();
            var snapshot = JsonSerializer.Serialize(r);
            var replay = await Rows("""
                select request_snapshot=cast(@snapshot as jsonb) and created_by=@actor same from care_plan_emergency_selections
                where id=@id and care_plan_id=@plan and organization_id=@org
                """, c => { Add(c, "snapshot", snapshot); Add(c, "id", r.RequestId); Add(c, "plan", planId); }, token);
            if (replay.Count > 0) return replay[0].GetProperty("same").GetBoolean() ? Ok(new { id = r.RequestId, replayed = true })
                : Conflict(new { message = "Request ID has different content or author." });
            if (plan.Status != "Draft" || plan.Revision != r.ExpectedPlanRevision)
                return Conflict(new { message = "Section designation requires the current draft revision. Reload before saving." });
            var history = await Selections(planId, token);
            var version = history.Count == 0 ? 1 : history[0].GetProperty("selection_version").GetInt32() + 1;
            await Exec("""
                insert into care_plan_emergency_selections(id,care_plan_id,organization_id,branch_id,selection_version,sections_json,reason,created_by,request_snapshot)
                values(@id,@plan,@org,@branch,@version,cast(@sections as jsonb),@reason,@actor,cast(@snapshot as jsonb));
                update care_plan_versions set revision=revision+1,updated_at=now() where care_plan_id=@plan and organization_id=@org;
                """, c => { Add(c, "id", r.RequestId); Add(c, "plan", planId); Add(c, "branch", plan.Branch); Add(c, "version", version);
                    Add(c, "sections", JsonSerializer.Serialize(r.Sections)); Add(c, "reason", r.Reason.Trim()); Add(c, "snapshot", snapshot); }, token);
            await Audit("care_plan.emergency_sections_designated", r.RequestId, plan.Branch, token);
            return Created($"/api/phase1/care-plans/{planId}/emergency-sections/{r.RequestId}", new { id = r.RequestId, revision = plan.Revision + 1 });
        }, token);
    }

    [HttpPost("handover-copy")]
    public Task<IActionResult> Copy(Guid planId, EmergencyCopyRequest r, CancellationToken token)
    {
        if (r.RequestId == Guid.Empty || r.ExpectedPlanRevision < 1 || string.IsNullOrWhiteSpace(r.Purpose) || r.Purpose.Length > 2000)
            return Task.FromResult<IActionResult>(BadRequest(new { message = "Provide the active plan revision and a purpose for the handover copy." }));
        return Transaction(async () => {
            var plan = await Plan(planId, " for update", token); if (plan is null) return NotFound();
            if (plan.Status != "Active" || plan.Revision != r.ExpectedPlanRevision)
                return Conflict(new { message = "Generate the handover copy from the current active plan revision." });
            var person = await db.ServiceUsers.AsNoTracking().SingleOrDefaultAsync(p => p.Id == plan.Content.ServiceUserId
                && p.OrganizationId == tenant.OrganizationId && p.BranchId == plan.Branch, token);
            if (person is null) return NotFound();
            var snapshot = JsonSerializer.Serialize(r);
            var replay = await Rows("""
                select html,sha256,file_name,request_snapshot=cast(@snapshot as jsonb) and created_by=@actor same
                from care_plan_handover_copies where id=@id and care_plan_id=@plan and organization_id=@org
                """, c => { Add(c, "id", r.RequestId); Add(c, "plan", planId); Add(c, "snapshot", snapshot); }, token);
            if (replay.Count > 0)
            {
                if (!replay[0].GetProperty("same").GetBoolean()) return Conflict(new { message = "Request ID has different content or author." });
                await Audit("care_plan.handover_downloaded", r.RequestId, plan.Branch, token);
                return Ok(new { id = r.RequestId, html = replay[0].GetProperty("html").GetString(), fileName = replay[0].GetProperty("file_name").GetString(), sha256 = replay[0].GetProperty("sha256").GetString() });
            }
            var history = await Selections(planId, token);
            var selected = history.Count == 0 ? Array.Empty<string>() : history[0].GetProperty("sections_json").Deserialize<string[]>()!;
            if (selected.Length == 0) return Conflict(new { message = "No sections were designated on this plan version. Create a draft revision to designate sections." });
            var content = selected.ToDictionary(s => s, s => s switch {
                "PersonalCare" => plan.Content.PersonalCare, "MedicationSupport" => plan.Content.MedicationSupport,
                "MobilityAndTransfers" => plan.Content.MobilityAndTransfers, "Nutrition" => plan.Content.Nutrition,
                _ => throw new InvalidOperationException("Unsupported emergency section") });
            var html = EmergencyPlanCopy.Render(person.FullName, person.DateOfBirth, plan.Version, plan.Revision,
                r.RequestId, DateTimeOffset.UtcNow, plan.Content.ReviewDueAt, content);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html))).ToLowerInvariant();
            var fileName = $"care-plan-handover-{r.RequestId:N}.html";
            await Exec("""
                insert into care_plan_handover_copies(id,care_plan_id,selection_id,organization_id,branch_id,plan_revision,purpose,html,sha256,file_name,created_by,request_snapshot)
                values(@id,@plan,@selection,@org,@branch,@revision,@purpose,@html,@hash,@file,@actor,cast(@snapshot as jsonb))
                """, c => { Add(c, "id", r.RequestId); Add(c, "plan", planId); Add(c, "selection", history[0].GetProperty("id").GetGuid());
                    Add(c, "branch", plan.Branch); Add(c, "revision", plan.Revision); Add(c, "purpose", r.Purpose.Trim());
                    Add(c, "html", html); Add(c, "hash", hash); Add(c, "file", fileName); Add(c, "snapshot", snapshot); }, token);
            await Audit("care_plan.handover_downloaded", r.RequestId, plan.Branch, token);
            return Ok(new { id = r.RequestId, html, fileName, sha256 = hash });
        }, token);
    }
    private Task<List<JsonElement>> Selections(Guid plan, CancellationToken token) => Rows("""
        select * from care_plan_emergency_selections where care_plan_id=@plan and organization_id=@org order by selection_version desc
        """, c => Add(c, "plan", plan), token);
    private async Task<PlanScope?> Plan(Guid id, string locking, CancellationToken token)
    {
        if (user.UserId is null) return null;
        var p = await db.CarePlans.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && p.OrganizationId == tenant.OrganizationId
            && (tenant.IsOrganizationWide || p.BranchId == tenant.BranchId), token);
        if (p is null || !await authorization.CanReadServiceUserAsync(p.ServiceUserId, token)) return null;
        await using var c = await Command("select status,revision,version_number from care_plan_versions where care_plan_id=@plan and organization_id=@org" + locking, token);
        Add(c, "plan", id); await using var reader = await c.ExecuteReaderAsync(token); if (!await reader.ReadAsync(token)) return null;
        var status = reader.GetString(0);
        return !Manage && status != "Active" ? null : new(p, p.BranchId ?? Guid.Empty, status, reader.GetInt64(1), reader.GetInt32(2));
    }
    private async Task<IActionResult> Transaction(Func<Task<IActionResult>> action, CancellationToken token)
    {
        try { return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () => {
            await using var tx = await db.Database.BeginTransactionAsync(token); var result = await action();
            if (result is OkObjectResult or CreatedResult) await tx.CommitAsync(token); return result;
        }); }
        catch (Npgsql.PostgresException e) when (e.SqlState is "23505" or "23514" or "23503")
        { return Conflict(new { message = "The plan or section selection changed. Reload before retrying." }); }
    }
    private async Task<List<JsonElement>> Rows(string sql, Action<DbCommand> bind, CancellationToken token)
    {
        await using var c = await Command($"select row_to_json(q)::text from ({sql})q", token); bind(c);
        await using var r = await c.ExecuteReaderAsync(token); var result = new List<JsonElement>();
        while (await r.ReadAsync(token)) result.Add(JsonSerializer.Deserialize<JsonElement>(r.GetString(0))); return result;
    }
    private async Task Exec(string sql, Action<DbCommand> bind, CancellationToken token)
    { await using var c = await Command(sql, token); bind(c); await c.ExecuteNonQueryAsync(token); }
    private async Task<DbCommand> Command(string sql, CancellationToken token)
    {
        var connection = db.Database.GetDbConnection(); if (connection.State != ConnectionState.Open) await connection.OpenAsync(token);
        var c = connection.CreateCommand(); c.Transaction = db.Database.CurrentTransaction?.GetDbTransaction(); c.CommandText = sql;
        Add(c, "org", tenant.OrganizationId); Add(c, "actor", user.UserId, DbType.Guid); return c;
    }
    private static void Add(DbCommand c, string name, object? value, DbType? type = null)
    { var p = c.CreateParameter(); p.ParameterName = name; if (type.HasValue) p.DbType = type.Value; p.Value = value ?? DBNull.Value; c.Parameters.Add(p); }
    private async Task Audit(string action, Guid id, Guid branch, CancellationToken token)
    { db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), action, user.UserName, "CarePlanHandover", id, DateTimeOffset.UtcNow, tenant.OrganizationId, branch)); await db.SaveChangesAsync(token); }
    private sealed record PlanScope(CarePlan Content, Guid Branch, string Status, long Revision, int Version);
}
public sealed record EmergencySectionsRequest(Guid RequestId, long ExpectedPlanRevision, string[] Sections, string Reason);
public sealed record EmergencyCopyRequest(Guid RequestId, long ExpectedPlanRevision, string Purpose);
