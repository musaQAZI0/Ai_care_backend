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
[Route("api/phase1/care-plans/{planId:guid}/needs")]
[RequestSizeLimit(64 * 1024)]
public sealed class StructuredCareNeedsController(CareDbContext db, ITenantContext tenant,
    ICurrentUserContext user, IContextualAuthorization authorization) : ControllerBase
{
    private bool Manage => user.HasAnyRole(UserRole.Administrator, UserRole.CareManager, UserRole.CareCoordinator);
    [HttpGet]
    public async Task<IActionResult> Get(Guid planId, CancellationToken token)
    {
        var plan = await Plan(planId, false, token); if (plan is null) return NotFound();
        var needs = await Rows("""
            select n.*,not exists(select 1 from care_plan_needs newer where newer.supersedes_need_id=n.id) is_current,
              exists(select 1 from "AppUsers" u where u."Id"=n.owner_user_id and u."OrganizationId"=n.organization_id and u."IsActive"
                and u."Role" in ('Administrator','CareManager','CareCoordinator','CareWorker')
                and (u."BranchId"=n.branch_id or (u."BranchId" is null and u."Role"='Administrator'))) owner_is_current
            from care_plan_needs n where care_plan_id=@plan and organization_id=@org
              and (@manage or not exists(select 1 from care_plan_needs newer where newer.supersedes_need_id=n.id)) order by created_at,id
            """, c => { Add(c, "plan", planId); Add(c, "manage", Manage); }, token);
        var owners = Manage ? await Rows("""
            select "Id" id,"UserName" name from "AppUsers" where "OrganizationId"=@org and "IsActive"
              and "Role" in ('Administrator','CareManager','CareCoordinator','CareWorker')
              and ("BranchId"=@branch or ("BranchId" is null and "Role"='Administrator')) order by "UserName"
            """, c => Add(c, "branch", plan.Branch), token) : new List<JsonElement>();
        return Ok(new { planStatus = plan.Status, revision = plan.Revision, needs, owners });
    }

    [HttpPost, Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public async Task<IActionResult> Create(Guid planId, StructuredCareNeedRequest r, CancellationToken token)
    {
        if (user.UserId is null) return Unauthorized();
        static bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
        if (r.RequestId == Guid.Empty || r.OwnerUserId == Guid.Empty || r.ExpectedPlanRevision < 1 || !Text(r.Section, 200)
            || !Text(r.IdentifiedNeed, 4000) || !Text(r.LevelOfNeed, 200) || !Text(r.PlannedOutcomes, 4000)
            || !Text(r.Actions, 4000) || !Text(r.ChangeReason, 2000) || r.Status is not ("Active" or "NoLongerRequired"))
            return BadRequest(new { message = "Complete the need, level, section, outcomes, actions, owner, status and change reason within the field limits." });
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () => {
                await using var tx = await db.Database.BeginTransactionAsync(token);
                var plan = await Plan(planId, true, token); if (plan is null) return NotFound();
                var snapshot = JsonSerializer.Serialize(r);
                var replay = await Rows("""
                    select request_snapshot=cast(@snapshot as jsonb) and created_by=@actor same from care_plan_needs
                    where id=@id and care_plan_id=@plan and organization_id=@org
                    """, c => { Add(c, "id", r.RequestId); Add(c, "plan", planId); Add(c, "snapshot", snapshot); }, token);
                if (replay.Count > 0) return replay[0].GetProperty("same").GetBoolean() ? Ok(new { id = r.RequestId, replayed = true })
                    : Conflict(new { message = "Request ID already has different content or author." });
                if (plan.Status != "Draft" || plan.Revision != r.ExpectedPlanRevision)
                    return Conflict(new { message = "Need changes require the current draft revision. Reload before saving." });
                var owners = await Rows("""
                    select "UserName" name from "AppUsers" where "Id"=@owner and "OrganizationId"=@org and "IsActive"
                      and "Role" in ('Administrator','CareManager','CareCoordinator','CareWorker')
                      and ("BranchId"=@branch or ("BranchId" is null and "Role"='Administrator'))
                    """, c => { Add(c, "owner", r.OwnerUserId); Add(c, "branch", plan.Branch); }, token);
                if (owners.Count != 1) return BadRequest(new { message = "Select an active care-team owner in this branch." });
                var root = r.RequestId;
                if (r.SupersedesNeedId is Guid previous)
                {
                    var originals = await Rows("""
                        select root_need_id from care_plan_needs n where id=@id and care_plan_id=@plan and organization_id=@org
                          and not exists(select 1 from care_plan_needs newer where newer.supersedes_need_id=n.id)
                        """, c => { Add(c, "id", previous); Add(c, "plan", planId); }, token);
                    if (originals.Count != 1) return Conflict(new { message = "Correct the latest need definition in this draft." });
                    root = originals[0].GetProperty("root_need_id").GetGuid();
                }
                await using var c = await Command("""
                    insert into care_plan_needs(id,root_need_id,care_plan_id,service_user_id,organization_id,branch_id,supersedes_need_id,
                      section,identified_need,level_of_need,planned_outcomes,actions,owner_user_id,owner_name,status,change_reason,created_by,request_snapshot)
                    values(@id,@root,@plan,@person,@org,@branch,@previous,@section,@need,@level,@outcomes,@actions,@owner,@name,@status,@reason,@actor,cast(@snapshot as jsonb));
                    update care_plan_versions set revision=revision+1,updated_at=now() where care_plan_id=@plan and organization_id=@org;
                    """, token);
                Add(c, "id", r.RequestId); Add(c, "root", root); Add(c, "plan", planId); Add(c, "person", plan.Person); Add(c, "branch", plan.Branch);
                Add(c, "previous", r.SupersedesNeedId, DbType.Guid); Add(c, "section", r.Section.Trim()); Add(c, "need", r.IdentifiedNeed.Trim());
                Add(c, "level", r.LevelOfNeed.Trim()); Add(c, "outcomes", r.PlannedOutcomes.Trim()); Add(c, "actions", r.Actions.Trim());
                Add(c, "owner", r.OwnerUserId); Add(c, "name", owners[0].GetProperty("name").GetString()); Add(c, "status", r.Status);
                Add(c, "reason", r.ChangeReason.Trim()); Add(c, "snapshot", snapshot); await c.ExecuteNonQueryAsync(token);
                db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "care_plan.need_recorded", user.UserName, "CarePlanNeed", r.RequestId, DateTimeOffset.UtcNow, tenant.OrganizationId, plan.Branch));
                await db.SaveChangesAsync(token); await tx.CommitAsync(token);
                return Created($"/api/phase1/care-plans/{planId}/needs/{r.RequestId}", new { id = r.RequestId, revision = plan.Revision + 1 });
            });
        }
        catch (Npgsql.PostgresException e) when (e.SqlState is "23505" or "23514")
        { return Conflict(new { message = "The need, owner or plan changed. Reload before saving." }); }
    }
    private async Task<PlanScope?> Plan(Guid id, bool locking, CancellationToken token)
    {
        var p = await db.CarePlans.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && p.OrganizationId == tenant.OrganizationId
            && (tenant.IsOrganizationWide || p.BranchId == tenant.BranchId), token);
        if (p is null || !await authorization.CanReadServiceUserAsync(p.ServiceUserId, token)) return null;
        await using var c = await Command("select status,revision from care_plan_versions where care_plan_id=@plan and organization_id=@org" + (locking ? " for update" : ""), token);
        Add(c, "plan", id); await using var r = await c.ExecuteReaderAsync(token);
        if (!await r.ReadAsync(token)) return null; var status = r.GetString(0);
        return !Manage && status != "Active" ? null : new(p.ServiceUserId, p.BranchId ?? Guid.Empty, status, r.GetInt64(1));
    }
    private async Task<List<JsonElement>> Rows(string sql, Action<DbCommand> bind, CancellationToken token)
    {
        await using var c = await Command($"select row_to_json(q)::text from ({sql})q", token); bind(c);
        await using var r = await c.ExecuteReaderAsync(token); var result = new List<JsonElement>();
        while (await r.ReadAsync(token)) result.Add(JsonSerializer.Deserialize<JsonElement>(r.GetString(0))); return result;
    }
    private async Task<DbCommand> Command(string sql, CancellationToken token)
    {
        var connection = db.Database.GetDbConnection(); if (connection.State != ConnectionState.Open) await connection.OpenAsync(token);
        var c = connection.CreateCommand(); c.Transaction = db.Database.CurrentTransaction?.GetDbTransaction(); c.CommandText = sql;
        Add(c, "org", tenant.OrganizationId); Add(c, "actor", user.UserId, DbType.Guid); return c;
    }
    private static void Add(DbCommand c, string name, object? value, DbType? type = null)
    { var p = c.CreateParameter(); p.ParameterName = name; if (type.HasValue) p.DbType = type.Value; p.Value = value ?? DBNull.Value; c.Parameters.Add(p); }
    private sealed record PlanScope(Guid Person, Guid Branch, string Status, long Revision);
}
public sealed record StructuredCareNeedRequest(Guid RequestId, long ExpectedPlanRevision, string Section, string IdentifiedNeed,
    string LevelOfNeed, string PlannedOutcomes, string Actions, Guid OwnerUserId, string Status, string ChangeReason, Guid? SupersedesNeedId = null);
