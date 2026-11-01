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

[ApiController, Authorize(Roles = "CareCoordinator,CareManager,Administrator,CareWorker")]
[Route("api/phase1/care-plans/{planId:guid}/measurable-goals")]
[RequestSizeLimit(256 * 1024)]
public sealed class MeasurableCareGoalsController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user,
    IContextualAuthorization authorization) : ControllerBase
{
    private bool Manage => user.HasAnyRole(UserRole.Administrator, UserRole.CareManager, UserRole.CareCoordinator);

    [HttpGet]
    public async Task<IActionResult> Get(Guid planId, CancellationToken token)
    {
        var plan = await Plan(planId, false, token); if (plan is null) return NotFound();
        var goals = await Rows("""
            select g.*,not exists(select 1 from care_plan_measurable_goals newer where newer.supersedes_goal_id=g.id) is_current,
              exists(select 1 from "AppUsers" u where u."Id"=g.owner_user_id and u."OrganizationId"=g.organization_id and u."IsActive"
                and (u."BranchId"=g.branch_id or (u."BranchId" is null and u."Role"='Administrator'))
                and u."Role" in ('Administrator','CareManager','CareCoordinator','CareWorker')) owner_is_current
            from care_plan_measurable_goals g where g.care_plan_id=@plan and g.organization_id=@org order by g.created_at,g.id
            """, c => Add(c, "plan", planId), token);
        // Preserve original plan/definition/unit provenance across plan revisions.
        var progress = await Rows("""
            select e.*,g.root_goal_id,g.unit,g.title,v.version_number plan_version,
              exists(select 1 from care_goal_progress correction where correction.corrects_event_id=e.id) is_corrected
            from care_goal_progress e join care_plan_measurable_goals g on g.id=e.goal_id join care_plan_versions v on v.care_plan_id=e.care_plan_id
            where e.organization_id=@org and e.branch_id=@target and g.service_user_id=@person and g.root_goal_id in
              (select root_goal_id from care_plan_measurable_goals where care_plan_id=@plan and organization_id=@org)
            order by e.observed_at desc,e.recorded_at desc,e.id
            """, c => { Add(c, "plan", planId); Add(c, "person", plan.Person); Add(c, "target", plan.Branch); }, token);
        var owners = Manage ? await Rows("""
            select "Id" id,"UserName" name from "AppUsers" where "OrganizationId"=@org and "IsActive"
              and ("BranchId"=@target or ("BranchId" is null and "Role"='Administrator'))
              and "Role" in ('Administrator','CareManager','CareCoordinator','CareWorker') order by "UserName"
            """, c => Add(c, "target", plan.Branch), token) : new List<JsonElement>();
        return Ok(new { planStatus = plan.Status, revision = plan.Revision, asOf = DateTimeOffset.UtcNow, goals, progress, owners });
    }

    [HttpPost, Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public Task<IActionResult> Create(Guid planId, CareGoalRequest r, CancellationToken token)
    {
        if (user.UserId is null) return Task.FromResult<IActionResult>(Unauthorized());
        var today = AppointmentWorkflow.UkDate(DateTimeOffset.UtcNow);
        var error = MeasurableCareGoalPolicy.Definition(r.Title, r.Unit, r.PlannedActions, r.ChangeReason,
            r.BaselineValue, r.TargetValue, r.BaselineDate, r.ReviewDueDate, r.TargetDate, today);
        if (r.RequestId == Guid.Empty || r.OwnerUserId == Guid.Empty || r.ExpectedPlanRevision < 1 || error is not null)
            return Task.FromResult<IActionResult>(BadRequest(new { message = error ?? "Request ID, owner and current plan revision are required." }));
        return Transaction(async () =>
        {
            var plan = await Plan(planId, true, token); if (plan is null) return NotFound();
            var snapshot = JsonSerializer.Serialize(r);
            if (await Replay("care_plan_measurable_goals", planId, r.RequestId, snapshot, token) is { } replay) return replay;
            if (plan.Status != "Draft" || plan.Revision != r.ExpectedPlanRevision) return Conflict(new { message = "Goal changes require the current draft plan revision. Reload before saving." });
            var owners = await Rows("""
                select "UserName" name from "AppUsers" where "Id"=@owner and "OrganizationId"=@org and "IsActive"
                  and ("BranchId"=@target or ("BranchId" is null and "Role"='Administrator'))
                  and "Role" in ('Administrator','CareManager','CareCoordinator','CareWorker')
                """, c => { Add(c, "owner", r.OwnerUserId); Add(c, "target", plan.Branch); }, token);
            if (owners.Count != 1) return BadRequest(new { message = "Select an active care-team owner in this branch." });
            var root = r.RequestId;
            if (r.SupersedesGoalId is Guid previous)
            {
                var originals = await Rows("""
                    select g.root_goal_id from care_plan_measurable_goals g where g.id=@id and g.care_plan_id=@plan and g.organization_id=@org
                    and not exists(select 1 from care_plan_measurable_goals n where n.supersedes_goal_id=g.id)
                    """, c => { Add(c, "id", previous); Add(c, "plan", planId); }, token);
                if (originals.Count != 1) return Conflict(new { message = "Replace the latest goal definition in this draft." });
                root = originals[0].GetProperty("root_goal_id").GetGuid();
            }
            await Exec("""
                insert into care_plan_measurable_goals(id,root_goal_id,care_plan_id,service_user_id,organization_id,branch_id,
                  supersedes_goal_id,title,baseline_value,target_value,unit,baseline_date,review_due_date,target_date,
                  owner_user_id,owner_name,planned_actions,change_reason,created_by,request_snapshot)
                values(@id,@root,@plan,@person,@org,@target,@previous,@title,@baseline,@value,@unit,@baselineDate,@review,@due,@owner,@ownerName,@actions,@reason,@actor,cast(@snapshot as jsonb))
                """, c =>
            {
                Add(c, "id", r.RequestId); Add(c, "root", root); Add(c, "plan", planId); Add(c, "person", plan.Person); Add(c, "target", plan.Branch);
                Add(c, "previous", r.SupersedesGoalId, DbType.Guid); Add(c, "title", r.Title.Trim()); Add(c, "baseline", r.BaselineValue); Add(c, "value", r.TargetValue);
                Add(c, "unit", r.Unit.Trim()); Add(c, "baselineDate", r.BaselineDate); Add(c, "review", r.ReviewDueDate); Add(c, "due", r.TargetDate);
                Add(c, "owner", r.OwnerUserId); Add(c, "ownerName", owners[0].GetProperty("name").GetString()); Add(c, "actions", r.PlannedActions.Trim());
                Add(c, "reason", r.ChangeReason.Trim()); Add(c, "snapshot", snapshot);
            }, token);
            await Exec("update care_plan_versions set revision=revision+1,updated_at=now() where care_plan_id=@plan and organization_id=@org", c => Add(c, "plan", planId), token);
            await Audit("care_goal.definition_created", r.RequestId, plan.Branch, token);
            return Created($"/api/phase1/care-plans/{planId}/measurable-goals/{r.RequestId}", new { id = r.RequestId, revision = plan.Revision + 1 });
        }, token);
    }

    [HttpPost("{goalId:guid}/progress")]
    public Task<IActionResult> Progress(Guid planId, Guid goalId, CareGoalProgressRequest r, CancellationToken token) => Transaction(async () =>
    {
        if (r.RequestId == Guid.Empty || user.UserId is null) return BadRequest(new { message = "A request ID and named user are required." });
        var plan = await Plan(planId, true, token); if (plan is null) return NotFound();
        var snapshot = JsonSerializer.Serialize(new { goalId, request = r });
        if (await Replay("care_goal_progress", planId, r.RequestId, snapshot, token) is { } replay) return replay;
        if (plan.Status != "Active" && !(Manage && plan.Status == "Superseded" && r.CorrectsEventId.HasValue))
            return Conflict(new { message = "New progress belongs to the active plan; managers may correct historical entries on a superseded plan." });
        var goals = await Rows("""
            select g.baseline_date from care_plan_measurable_goals g where g.id=@id and g.care_plan_id=@plan and g.organization_id=@org
              and not exists(select 1 from care_plan_measurable_goals n where n.supersedes_goal_id=g.id)
            """, c => { Add(c, "id", goalId); Add(c, "plan", planId); }, token);
        if (goals.Count != 1) return NotFound();
        var error = MeasurableCareGoalPolicy.Progress(r.Status, r.MeasuredValue, r.Note, r.ObservedAt,
            DateOnly.Parse(goals[0].GetProperty("baseline_date").GetString()!), DateTimeOffset.UtcNow);
        if (error is not null) return BadRequest(new { message = error });
        if (r.CorrectsEventId is Guid original)
        {
            if (string.IsNullOrWhiteSpace(r.CorrectionReason) || r.CorrectionReason.Length > 2000) return BadRequest(new { message = "A correction reason is required." });
            var entries = await Rows("""
                select e.recorded_by from care_goal_progress e where e.id=@id and e.goal_id=@goal and e.organization_id=@org
                  and not exists(select 1 from care_goal_progress correction where correction.corrects_event_id=e.id)
                """, c => { Add(c, "id", original); Add(c, "goal", goalId); }, token);
            if (entries.Count != 1) return Conflict(new { message = "Correct the latest entry in this correction chain." });
            if (!Manage && entries[0].GetProperty("recorded_by").GetGuid() != user.UserId) return Forbid();
        }
        await Exec("""
            insert into care_goal_progress(id,goal_id,care_plan_id,organization_id,branch_id,corrects_event_id,correction_reason,
              measured_value,status,note,observed_at,recorded_by,recorded_by_name,request_snapshot)
            values(@id,@goal,@plan,@org,@target,@original,@reason,@value,@status,@note,@observed,@actor,@name,cast(@snapshot as jsonb))
            """, c =>
        {
            Add(c, "id", r.RequestId); Add(c, "goal", goalId); Add(c, "plan", planId); Add(c, "target", plan.Branch);
            Add(c, "original", r.CorrectsEventId, DbType.Guid); Add(c, "reason", r.CorrectionReason?.Trim() ?? ""); Add(c, "value", r.MeasuredValue, DbType.Decimal);
            Add(c, "status", r.Status); Add(c, "note", r.Note.Trim()); Add(c, "observed", r.ObservedAt); Add(c, "name", user.UserName); Add(c, "snapshot", snapshot);
        }, token);
        await Audit(r.CorrectsEventId.HasValue ? "care_goal.progress_corrected" : "care_goal.progress_recorded", r.RequestId, plan.Branch, token);
        return Created($"/api/phase1/care-plans/{planId}/measurable-goals/{goalId}/progress/{r.RequestId}", new { id = r.RequestId });
    }, token);

    private async Task<PlanScope?> Plan(Guid id, bool locking, CancellationToken token)
    {
        var plan = await db.CarePlans.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && p.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || p.BranchId == tenant.BranchId), token);
        if (plan is null || !await authorization.CanReadServiceUserAsync(plan.ServiceUserId, token)) return null;
        await using var c = await Command("select status,revision from care_plan_versions where care_plan_id=@plan and organization_id=@org" + (locking ? " for update" : ""), token);
        Add(c, "plan", id); await using var reader = await c.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var status = reader.GetString(0);
        if (!Manage && status != "Active" && !(locking && status == "Superseded")) return null;
        return new(plan.ServiceUserId, plan.BranchId ?? Guid.Empty, status, reader.GetInt64(1));
    }
    private async Task<IActionResult?> Replay(string table, Guid plan, Guid id, string snapshot, CancellationToken token)
    {
        var actorColumn = table == "care_goal_progress" ? "recorded_by" : "created_by";
        await using var c = await Command($"select request_snapshot=cast(@snapshot as jsonb) and {actorColumn}=@actor from {table} where id=@id and care_plan_id=@plan and organization_id=@org", token);
        Add(c, "id", id); Add(c, "plan", plan); Add(c, "snapshot", snapshot);
        return await c.ExecuteScalarAsync(token) is bool same ? same ? Ok(new { id, replayed = true }) : Conflict(new { message = "Request ID already has different content or author." }) : null;
    }
    private async Task<IActionResult> Transaction(Func<Task<IActionResult>> action, CancellationToken token)
    {
        try { return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(token); var result = await action();
            if (result is not (ConflictObjectResult or NotFoundResult or ForbidResult or BadRequestObjectResult)) await tx.CommitAsync(token);
            return result;
        }); }
        catch (Npgsql.PostgresException e) when (e.SqlState is "23505" or "23514") { return Conflict(new { message = "Goal, plan or progress history changed. Reload before retrying." }); }
    }
    private async Task<List<JsonElement>> Rows(string sql, Action<DbCommand> bind, CancellationToken token)
    {
        await using var c = await Command($"select row_to_json(q)::text from ({sql})q", token); bind(c);
        await using var reader = await c.ExecuteReaderAsync(token); var rows = new List<JsonElement>();
        while (await reader.ReadAsync(token)) rows.Add(JsonSerializer.Deserialize<JsonElement>(reader.GetString(0))); return rows;
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
    { db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), action, user.UserName, "CareGoal", id, DateTimeOffset.UtcNow, tenant.OrganizationId, branch)); await db.SaveChangesAsync(token); }
    private sealed record PlanScope(Guid Person, Guid Branch, string Status, long Revision);
}

public sealed record CareGoalRequest(Guid RequestId, long ExpectedPlanRevision, string Title,
    [property: System.Text.Json.Serialization.JsonRequired] decimal BaselineValue,
    [property: System.Text.Json.Serialization.JsonRequired] decimal TargetValue,
    string Unit, DateOnly BaselineDate, DateOnly ReviewDueDate, DateOnly TargetDate, Guid OwnerUserId, string PlannedActions, string ChangeReason, Guid? SupersedesGoalId = null);
public sealed record CareGoalProgressRequest(Guid RequestId, decimal? MeasuredValue, string Status, string Note, DateTimeOffset ObservedAt,
    Guid? CorrectsEventId = null, string? CorrectionReason = null);
