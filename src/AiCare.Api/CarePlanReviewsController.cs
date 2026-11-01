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

[ApiController, Authorize(Roles = "CareManager,Administrator")]
[Route("api/phase1/service-users/{personId:guid}/plan-reviews")]
[RequestSizeLimit(32 * 1024)]
public sealed class CarePlanReviewsController(CareDbContext db, ITenantContext tenant,
    ICurrentUserContext user, IContextualAuthorization authorization) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid personId, CancellationToken token)
    {
        var branch = await Scope(personId, token); if (branch is null) return NotFound();
        var rules = await Rows("""
            select r.* from care_plan_review_rules r where r.service_user_id=@person and r.organization_id=@org and r.branch_id=@branch
            order by r.event_kind,r.version desc
            """, personId, branch.Value, _ => { }, token);
        var items = await Rows("""
            select i.*,(select version_number from care_plan_versions where care_plan_id=i.care_plan_id) captured_plan_version,
              coalesce(e.revision,0) revision,coalesce(e.action,'Open') status,
              coalesce(e.reviewer_id,i.reviewer_id) current_reviewer_id,coalesce(e.reviewer_name,i.reviewer_name) current_reviewer_name,
              exists(select 1 from "AppUsers" u where u."Id"=coalesce(e.reviewer_id,i.reviewer_id) and u."OrganizationId"=i.organization_id
                and u."IsActive" and u."Role" in ('CareManager','Administrator')
                and (u."BranchId"=i.branch_id or (u."BranchId" is null and u."Role"='Administrator'))) reviewer_is_current,
              e.reason resolution_reason,e.resolved_plan_id
            from care_plan_review_items i left join lateral
              (select * from care_plan_review_events where item_id=i.id order by revision desc limit 1)e on true
            where i.service_user_id=@person and i.organization_id=@org and i.branch_id=@branch order by i.due_at,i.id
            """, personId, branch.Value, _ => { }, token);
        var history = await Rows("""
            select e.* from care_plan_review_events e join care_plan_review_items i on i.id=e.item_id
            where i.service_user_id=@person and i.organization_id=@org and i.branch_id=@branch order by e.created_at,e.revision
            """, personId, branch.Value, _ => { }, token);
        var reviewers = await Rows("""
            select "Id" id,"UserName" name from "AppUsers" where "OrganizationId"=@org and "IsActive"
              and "Role" in ('CareManager','Administrator') and ("BranchId"=@branch or ("BranchId" is null and "Role"='Administrator')) order by "UserName"
            """, personId, branch.Value, _ => { }, token);
        var plans = await Rows("""
            select p.care_plan_id id,p.version_number,p.status,
              (select max(e.performed_at) from care_plan_lifecycle_events e where e.care_plan_id=p.care_plan_id and e.to_status='Active') activated_at
            from care_plan_versions p where service_user_id=@person and organization_id=@org
              and branch_id=@branch and status in ('Active','Superseded') order by version_number desc
            """, personId, branch.Value, _ => { }, token);
        return Ok(new { rules, items, history, reviewers, plans, asOf = DateTimeOffset.UtcNow });
    }

    [HttpPost("rules")]
    public Task<IActionResult> Configure(Guid personId, CarePlanReviewRuleRequest r, CancellationToken token)
    {
        if (r.RequestId == Guid.Empty || r.ExpectedVersion < 0 || r.EventKind is not ("AssessmentApproved" or "RiskApproved" or "IncidentReported")
            || r.ReviewerId == Guid.Empty || r.DueHours is < 1 or > 2160 || string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Length > 2000)
            return Task.FromResult<IActionResult>(BadRequest(new { message = "Select an event, reviewer, deadline of 1–2,160 hours and a change reason." }));
        return Transaction(async () =>
        {
            var branch = await Scope(personId, token); if (branch is null) return NotFound();
            var snapshot = JsonSerializer.Serialize(r);
            await Exec("select pg_advisory_xact_lock(hashtextextended(cast(@person as text),27))", personId, branch.Value, _ => { }, token);
            if (await Replay("care_plan_review_rules", r.RequestId, personId, branch.Value, snapshot, token) is { } replay) return replay;
            var versions = await Rows("""
                select coalesce(max(version),0) version from care_plan_review_rules
                where service_user_id=@person and organization_id=@org and branch_id=@branch and event_kind=@kind
                """, personId, branch.Value, c => Add(c, "kind", r.EventKind), token);
            if (versions[0].GetProperty("version").GetInt32() != r.ExpectedVersion)
                return Conflict(new { message = "This rule changed. Reload before saving." });
            var reviewer = await Reviewer(personId, branch.Value, r.ReviewerId, token);
            if (reviewer is null) return BadRequest(new { message = "Select an active manager or administrator in this branch." });
            await Exec("""
                insert into care_plan_review_rules(id,service_user_id,organization_id,branch_id,event_kind,version,enabled,
                  reviewer_id,reviewer_name,due_hours,reason,created_by,request_snapshot)
                values(@id,@person,@org,@branch,@kind,@version,@enabled,@reviewer,@name,@hours,@reason,@actor,cast(@snapshot as jsonb))
                """, personId, branch.Value, c => {
                    Add(c, "id", r.RequestId); Add(c, "kind", r.EventKind); Add(c, "version", r.ExpectedVersion + 1); Add(c, "enabled", r.Enabled);
                    Add(c, "reviewer", r.ReviewerId); Add(c, "name", reviewer); Add(c, "hours", r.DueHours); Add(c, "reason", r.Reason.Trim()); Add(c, "snapshot", snapshot);
                }, token);
            await Audit("care_plan.review_rule_configured", r.RequestId, branch.Value, token);
            return Created($"/api/phase1/service-users/{personId}/plan-reviews/rules/{r.RequestId}", new { id = r.RequestId, version = r.ExpectedVersion + 1 });
        }, token);
    }

    [HttpPost("{itemId:guid}/events")]
    public Task<IActionResult> Record(Guid personId, Guid itemId, CarePlanReviewEventRequest r, CancellationToken token)
    {
        if (r.RequestId == Guid.Empty || r.ExpectedRevision < 0 || r.ReviewerId == Guid.Empty
            || r.Action is not ("Reassigned" or "NoChangeRequired" or "RevisedPlanActivated")
            || string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Length > 2000
            || (r.Action == "RevisedPlanActivated") != r.ResolvedPlanId.HasValue)
            return Task.FromResult<IActionResult>(BadRequest(new { message = "Select an action and reviewer, explain the decision, and link an activated plan only for a plan revision outcome." }));
        return Transaction(async () =>
        {
            var branch = await Scope(personId, token); if (branch is null) return NotFound();
            var items = await Rows("""
                select i.id from care_plan_review_items i where i.id=@item and i.service_user_id=@person
                  and i.organization_id=@org and i.branch_id=@branch for update
                """, personId, branch.Value, c => Add(c, "item", itemId), token);
            if (items.Count != 1) return NotFound();
            var snapshot = JsonSerializer.Serialize(new { itemId, request = r });
            if (await Replay("care_plan_review_events", r.RequestId, personId, branch.Value, snapshot, token) is { } replay) return replay;
            var events = await Rows("select revision,action from care_plan_review_events where item_id=@item order by revision desc limit 1",
                personId, branch.Value, c => Add(c, "item", itemId), token);
            if ((events.Count == 0 ? 0 : events[0].GetProperty("revision").GetInt32()) != r.ExpectedRevision
                || events.Count > 0 && events[0].GetProperty("action").GetString() != "Reassigned")
                return Conflict(new { message = "Review changed or was already resolved. Reload before saving." });
            // A manager resolving a review accepts accountability in their own name.
            if (r.Action != "Reassigned" && r.ReviewerId != user.UserId) return Forbid();
            var reviewer = await Reviewer(personId, branch.Value, r.ReviewerId, token);
            if (reviewer is null) return BadRequest(new { message = "Select an active manager or administrator in this branch." });
            await Exec("""
                insert into care_plan_review_events(id,item_id,revision,action,reviewer_id,reviewer_name,reason,resolved_plan_id,created_by,request_snapshot)
                values(@id,@item,@revision,@action,@reviewer,@name,@reason,@plan,@actor,cast(@snapshot as jsonb))
                """, personId, branch.Value, c => {
                    Add(c, "id", r.RequestId); Add(c, "item", itemId); Add(c, "revision", r.ExpectedRevision + 1); Add(c, "action", r.Action);
                    Add(c, "reviewer", r.ReviewerId); Add(c, "name", reviewer); Add(c, "reason", r.Reason.Trim());
                    Add(c, "plan", r.ResolvedPlanId, DbType.Guid); Add(c, "snapshot", snapshot);
                }, token);
            await Audit("care_plan.review_" + r.Action, r.RequestId, branch.Value, token);
            return Created($"/api/phase1/service-users/{personId}/plan-reviews/{itemId}/events/{r.RequestId}", new { id = r.RequestId, revision = r.ExpectedRevision + 1 });
        }, token);
    }

    private async Task<Guid?> Scope(Guid person, CancellationToken token)
    {
        if (user.UserId is null) return null;
        var row = await db.ServiceUsers.AsNoTracking().SingleOrDefaultAsync(p => p.Id == person && p.OrganizationId == tenant.OrganizationId
            && (tenant.IsOrganizationWide || p.BranchId == tenant.BranchId), token);
        return row is not null && await authorization.CanReadServiceUserAsync(person, token) ? row.BranchId : null;
    }
    private async Task<string?> Reviewer(Guid person, Guid branch, Guid reviewer, CancellationToken token)
    {
        var rows = await Rows("""
            select "UserName" name from "AppUsers" where "Id"=@reviewer and "OrganizationId"=@org and "IsActive"
              and "Role" in ('CareManager','Administrator') and ("BranchId"=@branch or ("BranchId" is null and "Role"='Administrator'))
            """, person, branch, c => Add(c, "reviewer", reviewer), token);
        return rows.Count == 1 ? rows[0].GetProperty("name").GetString() : null;
    }
    private async Task<IActionResult?> Replay(string table, Guid id, Guid person, Guid branch, string snapshot, CancellationToken token)
    {
        var from = table == "care_plan_review_rules" ? "care_plan_review_rules e" : "care_plan_review_events e join care_plan_review_items i on i.id=e.item_id";
        var scope = table == "care_plan_review_rules" ? "e" : "i";
        var rows = await Rows($"select e.request_snapshot=cast(@snapshot as jsonb) and e.created_by=@actor same from {from} where e.id=@id and {scope}.service_user_id=@person and {scope}.organization_id=@org and {scope}.branch_id=@branch",
            person, branch, c => { Add(c, "id", id); Add(c, "snapshot", snapshot); }, token);
        return rows.Count == 0 ? null : rows[0].GetProperty("same").GetBoolean() ? Ok(new { id, replayed = true }) : Conflict(new { message = "Request ID has different content or author." });
    }
    private async Task<IActionResult> Transaction(Func<Task<IActionResult>> action, CancellationToken token)
    {
        try { return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () => {
            await using var tx = await db.Database.BeginTransactionAsync(token); var result = await action();
            if (result is OkObjectResult or CreatedResult) await tx.CommitAsync(token); return result;
        }); }
        catch (Npgsql.PostgresException e) when (e.SqlState is "23505" or "23514" or "23503")
        { return Conflict(new { message = "Review state changed or the selected plan is not a qualifying activated revision. Reload before saving." }); }
    }
    private async Task<List<JsonElement>> Rows(string sql, Guid person, Guid branch, Action<DbCommand> bind, CancellationToken token)
    {
        await using var c = await Command($"select row_to_json(q)::text from ({sql})q", person, branch, token); bind(c);
        await using var r = await c.ExecuteReaderAsync(token); var result = new List<JsonElement>();
        while (await r.ReadAsync(token)) result.Add(JsonSerializer.Deserialize<JsonElement>(r.GetString(0))); return result;
    }
    private async Task Exec(string sql, Guid person, Guid branch, Action<DbCommand> bind, CancellationToken token)
    { await using var c = await Command(sql, person, branch, token); bind(c); await c.ExecuteNonQueryAsync(token); }
    private async Task<DbCommand> Command(string sql, Guid person, Guid branch, CancellationToken token)
    {
        var connection = db.Database.GetDbConnection(); if (connection.State != ConnectionState.Open) await connection.OpenAsync(token);
        var c = connection.CreateCommand(); c.Transaction = db.Database.CurrentTransaction?.GetDbTransaction(); c.CommandText = sql;
        Add(c, "org", tenant.OrganizationId); Add(c, "person", person); Add(c, "branch", branch); Add(c, "actor", user.UserId, DbType.Guid); return c;
    }
    private static void Add(DbCommand c, string name, object? value, DbType? type = null)
    { var p = c.CreateParameter(); p.ParameterName = name; if (type.HasValue) p.DbType = type.Value; p.Value = value ?? DBNull.Value; c.Parameters.Add(p); }
    private async Task Audit(string action, Guid id, Guid branch, CancellationToken token)
    { db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), action, user.UserName, "CarePlanReview", id, DateTimeOffset.UtcNow, tenant.OrganizationId, branch)); await db.SaveChangesAsync(token); }
}
public sealed record CarePlanReviewRuleRequest(Guid RequestId, int ExpectedVersion, string EventKind, bool Enabled, Guid ReviewerId, int DueHours, string Reason);
public sealed record CarePlanReviewEventRequest(Guid RequestId, int ExpectedRevision, string Action, Guid ReviewerId, string Reason, Guid? ResolvedPlanId = null);
