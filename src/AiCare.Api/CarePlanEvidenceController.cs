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

[ApiController, Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
[Route("api/phase1/care-plans/{planId:guid}/evidence")]
[RequestSizeLimit(32 * 1024)]
public sealed class CarePlanEvidenceController(CareDbContext db, ITenantContext tenant,
    ICurrentUserContext user, IContextualAuthorization authorization) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid planId, CancellationToken token)
    {
        var plan = await Plan(planId, false, token); if (plan is null) return NotFound();
        var links = await Rows("""
            select e.*,w.id is not null is_withdrawn,w.reason withdrawal_reason,w.created_at withdrawn_at,w.created_by_name withdrawn_by_name
            from care_plan_evidence e left join care_plan_evidence_withdrawals w on w.evidence_id=e.id
            where e.care_plan_id=@plan and e.organization_id=@org order by e.created_at,e.id
            """,
            c => Add(c, "plan", planId), token);
        var sources = await Rows("""
            select 'Assessment' kind,id,assessment_type title,version from governed_assessments
              where service_user_id=@person and organization_id=@org and branch_id=@branch and status='Current' and signed_at is not null
            union all
            select 'Risk',id,category,version from governed_risk_assessments
              where service_user_id=@person and organization_id=@org and branch_id=@branch and status='Current' and signed_at is not null
            """, c => { Add(c, "person", plan.Person); Add(c, "branch", plan.Branch); }, token);
        return Ok(new { planStatus = plan.Status, revision = plan.Revision, links, sources });
    }

    [HttpPost]
    public async Task<IActionResult> Create(Guid planId, CarePlanEvidenceRequest request, CancellationToken token)
    {
        if (user.UserId is null) return Unauthorized();
        if (request.RequestId == Guid.Empty || request.SourceId == Guid.Empty || request.ExpectedPlanRevision < 1
            || request.SourceKind is not ("Assessment" or "Risk") || string.IsNullOrWhiteSpace(request.Section)
            || request.Section.Length > 200 || string.IsNullOrWhiteSpace(request.Rationale) || request.Rationale.Length > 2000)
            return BadRequest(new { message = "Select signed evidence, a plan section and a rationale (up to 2,000 characters)." });
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(token);
                var plan = await Plan(planId, true, token); if (plan is null) return NotFound();
                var snapshot = JsonSerializer.Serialize(request);
                var replay = await Rows("""
                    select request_snapshot=cast(@snapshot as jsonb) and created_by=@actor same from care_plan_evidence
                    where id=@id and care_plan_id=@plan and organization_id=@org
                    """, c => { Add(c, "snapshot", snapshot); Add(c, "id", request.RequestId); Add(c, "plan", planId); }, token);
                if (replay.Count > 0) return replay[0].GetProperty("same").GetBoolean()
                    ? Ok(new { id = request.RequestId, replayed = true }) : Conflict(new { message = "Request ID already has different content or author." });
                if (plan.Status != "Draft" || plan.Revision != request.ExpectedPlanRevision)
                    return Conflict(new { message = "Evidence changes require the current draft revision. Reload before saving." });
                await using var c = await Command("""
                    insert into care_plan_evidence(id,care_plan_id,organization_id,branch_id,service_user_id,source_kind,source_id,
                      source_snapshot,section,rationale,created_by,request_snapshot)
                    values(@id,@plan,@org,@branch,@person,@kind,@source,'{}'::jsonb,@section,@rationale,@actor,cast(@snapshot as jsonb));
                    update care_plan_versions set revision=revision+1,updated_at=now() where care_plan_id=@plan and organization_id=@org;
                    """, token);
                Add(c, "id", request.RequestId); Add(c, "plan", planId); Add(c, "branch", plan.Branch); Add(c, "person", plan.Person);
                Add(c, "kind", request.SourceKind); Add(c, "source", request.SourceId); Add(c, "section", request.Section.Trim());
                Add(c, "rationale", request.Rationale.Trim()); Add(c, "snapshot", snapshot);
                await c.ExecuteNonQueryAsync(token);
                db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "care_plan.evidence_linked", user.UserName, "CarePlanEvidence",
                    request.RequestId, DateTimeOffset.UtcNow, tenant.OrganizationId, plan.Branch));
                await db.SaveChangesAsync(token); await tx.CommitAsync(token);
                return Created($"/api/phase1/care-plans/{planId}/evidence/{request.RequestId}", new { id = request.RequestId, revision = plan.Revision + 1 });
            });
        }
        catch (Npgsql.PostgresException e) when (e.SqlState is "23505" or "23514")
        { return Conflict(new { message = "Evidence or plan state changed, or this source is already linked to that section. Reload before saving." }); }
    }

    [HttpPost("{evidenceId:guid}/withdrawals")]
    public async Task<IActionResult> Withdraw(Guid planId, Guid evidenceId, EvidenceWithdrawalRequest request, CancellationToken token)
    {
        if (user.UserId is null) return Unauthorized();
        if (request.RequestId == Guid.Empty || request.ExpectedPlanRevision < 1 || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 2000)
            return BadRequest(new { message = "The current draft revision and a withdrawal reason of up to 2,000 characters are required." });
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () => {
                await using var tx = await db.Database.BeginTransactionAsync(token);
                var plan = await Plan(planId, true, token); if (plan is null) return NotFound();
                var snapshot = JsonSerializer.Serialize(new { evidenceId, request });
                var replay = await Rows("""
                    select request_snapshot=cast(@snapshot as jsonb) and created_by=@actor same from care_plan_evidence_withdrawals
                    where id=@id and care_plan_id=@plan and organization_id=@org
                    """, c => { Add(c, "snapshot", snapshot); Add(c, "id", request.RequestId); Add(c, "plan", planId); }, token);
                if (replay.Count > 0) return replay[0].GetProperty("same").GetBoolean() ? Ok(new { id = request.RequestId, replayed = true })
                    : Conflict(new { message = "Request ID already has different content or author." });
                if (plan.Status != "Draft" || plan.Revision != request.ExpectedPlanRevision)
                    return Conflict(new { message = "Withdraw evidence only from the current draft revision. Reload before saving." });
                var links = await Rows("select id from care_plan_evidence where id=@evidence and care_plan_id=@plan and organization_id=@org",
                    c => { Add(c, "evidence", evidenceId); Add(c, "plan", planId); }, token);
                if (links.Count != 1) return NotFound();
                await using var c = await Command("""
                    insert into care_plan_evidence_withdrawals(id,evidence_id,care_plan_id,organization_id,branch_id,reason,created_by,created_by_name,request_snapshot)
                    values(@id,@evidence,@plan,@org,@branch,@reason,@actor,@name,cast(@snapshot as jsonb));
                    update care_plan_versions set revision=revision+1,updated_at=now() where care_plan_id=@plan and organization_id=@org;
                    """, token);
                Add(c, "id", request.RequestId); Add(c, "evidence", evidenceId); Add(c, "plan", planId); Add(c, "branch", plan.Branch);
                Add(c, "reason", request.Reason.Trim()); Add(c, "name", user.UserName); Add(c, "snapshot", snapshot); await c.ExecuteNonQueryAsync(token);
                db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "care_plan.evidence_withdrawn", user.UserName, "CarePlanEvidenceWithdrawal",
                    request.RequestId, DateTimeOffset.UtcNow, tenant.OrganizationId, plan.Branch));
                await db.SaveChangesAsync(token); await tx.CommitAsync(token);
                return Created($"/api/phase1/care-plans/{planId}/evidence/{evidenceId}/withdrawals/{request.RequestId}", new { id = request.RequestId, revision = plan.Revision + 1 });
            });
        }
        catch (Npgsql.PostgresException e) when (e.SqlState is "23505" or "23514")
        { return Conflict(new { message = "This evidence was withdrawn or the plan changed. Reload before saving." }); }
    }

    private async Task<PlanScope?> Plan(Guid id, bool locking, CancellationToken token)
    {
        var plan = await db.CarePlans.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && p.OrganizationId == tenant.OrganizationId
            && (tenant.IsOrganizationWide || p.BranchId == tenant.BranchId), token);
        if (plan is null || !await authorization.CanReadServiceUserAsync(plan.ServiceUserId, token)) return null;
        await using var c = await Command("select status,revision from care_plan_versions where care_plan_id=@plan and organization_id=@org" + (locking ? " for update" : ""), token);
        Add(c, "plan", id); await using var r = await c.ExecuteReaderAsync(token);
        return await r.ReadAsync(token) ? new(plan.ServiceUserId, plan.BranchId ?? Guid.Empty, r.GetString(0), r.GetInt64(1)) : null;
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
public sealed record CarePlanEvidenceRequest(Guid RequestId, long ExpectedPlanRevision, string SourceKind, Guid SourceId, string Section, string Rationale);
public sealed record EvidenceWithdrawalRequest(Guid RequestId, long ExpectedPlanRevision, string Reason);
