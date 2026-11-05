using System.Data;
using System.Data.Common;
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
public sealed class CareTasksController : ControllerBase
{
    private readonly CareDbContext _context;
    private readonly ITenantContext _tenant;
    private readonly ICurrentUserContext _currentUser;

    public CareTasksController(CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser)
    {
        _context = context;
        _tenant = tenant;
        _currentUser = currentUser;
    }

    [HttpGet("api/phase1/care-plans/{carePlanId:guid}/tasks")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public async Task<IActionResult> GetPlanTasks(Guid carePlanId, CancellationToken cancellationToken)
    {
        var plan = await FindPlan(carePlanId, cancellationToken);
        if (plan is null) return NotFound();
        return Ok(await QueryPlanTasks(carePlanId, cancellationToken));
    }

    [HttpPost("api/phase1/care-plans/{carePlanId:guid}/tasks")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public async Task<IActionResult> CreatePlanTask(Guid carePlanId, CreateCarePlanTaskRequest request, CancellationToken cancellationToken)
    {
        var plan = await FindPlan(carePlanId, cancellationToken);
        if (plan is null) return NotFound();
        if (string.IsNullOrWhiteSpace(request.Title)) return BadRequest(new { message = "Task title is required." });
        if (!string.Equals(plan.Status, "Draft", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = "Care-plan tasks can only be changed while the care plan version is Draft. Create a revision first." });

        var id = Guid.NewGuid();
        await Execute("""
            insert into care_plan_tasks
              (id,care_plan_id,service_user_id,organization_id,branch_id,title,category,instructions,is_required,frequency,status,created_at)
            values (@id,@plan,@person,@organization,@branch,@title,@category,@instructions,@required,@frequency,'Active',now())
            """, command =>
        {
            Add(command,"id",id); Add(command,"plan",plan.Id); Add(command,"person",plan.ServiceUserId);
            Add(command,"organization",plan.OrganizationId ?? _tenant.OrganizationId); Add(command,"branch",plan.BranchId ?? _tenant.BranchId ?? TenantDefaults.BranchId);
            Add(command,"title",request.Title.Trim()); Add(command,"category",string.IsNullOrWhiteSpace(request.Category)?"General":request.Category.Trim());
            Add(command,"instructions",request.Instructions?.Trim() ?? ""); Add(command,"required",request.IsRequired);
            Add(command,"frequency",string.IsNullOrWhiteSpace(request.Frequency)?"Every visit":request.Frequency.Trim());
        }, cancellationToken);
        Audit("care_plan_task.created","CarePlanTask",id,plan.OrganizationId,plan.BranchId);
        await _context.SaveChangesAsync(cancellationToken);
        return Created($"/api/phase1/care-plans/{carePlanId}/tasks/{id}", new { id });
    }

    [HttpGet("api/phase1/visits/{visitId:guid}/tasks")]
    public async Task<IActionResult> GetVisitTasks(Guid visitId, CancellationToken cancellationToken)
    {
        var visit = await FindVisit(visitId, cancellationToken);
        if (visit is null) return NotFound();
        if (!CanAccessVisit(visit)) return Forbid();

        await MaterializeVisitTasks(visit, cancellationToken);
        return Ok(await QueryVisitTasks(visit.Id, cancellationToken));
    }

    [HttpPost("api/phase1/visits/{visitId:guid}/tasks/reconcile")]
    [Authorize(Roles = "CareManager,Administrator")]
    public async Task<IActionResult> Reconcile(Guid visitId, ReconcileVisitTasksRequest request, CancellationToken token)
    {
        if (request.CarePlanId == Guid.Empty || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length is < 10 or > 2000)
            return BadRequest(new { message = "Select the current Active plan and enter a review reason of 10–2000 characters." });
        return await _context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await _context.Database.BeginTransactionAsync(token);
            await Execute("""
                select "Id" from "Visits" where "Id"=@visit and "OrganizationId"=@org and (@wide or "BranchId"=@branch) for update
                """, c => { Add(c,"visit",visitId); Add(c,"org",_tenant.OrganizationId); Add(c,"wide",_tenant.IsOrganizationWide); Add(c,"branch",_tenant.BranchId); }, token);
            var visit = await FindVisit(visitId, token);
            if (visit is null) return (IActionResult)NotFound();
            if (visit.Status is not (VisitStatus.Scheduled or VisitStatus.InProgress)) return (IActionResult)Conflict(new { message = "Only open visits can have their pending task plan reconciled." });
            var selected = await Query("""
                select v.care_plan_id from care_plan_versions v join "CarePlans" p on p."Id"=v.care_plan_id
                where v.care_plan_id=@plan and v.service_user_id=@person and v.organization_id=@org and v.branch_id=@branch
                  and v.status='Active' and p."Status"='Active' and p."ServiceUserId"=@person and p."OrganizationId"=@org and p."BranchId"=@branch
                for share of v,p
                """, c => { Add(c,"plan",request.CarePlanId); Add(c,"person",visit.ServiceUserId); Add(c,"org",visit.OrganizationId); Add(c,"branch",visit.BranchId); }, r => r.GetGuid(0), token);
            if (selected.Count != 1) return (IActionResult)Conflict(new { message = "The selected plan is no longer the visit's current Active plan. Reload before reviewing." });
            var blocked = (await QueryVisitTasks(visit.Id, token)).Where(item => item.Status == "RequiresReview").Select(item => item.Id).ToArray();
            if (blocked.Length == 0) return (IActionResult)Conflict(new { message = "This visit has no pending tasks requiring plan review." });
            var changed = await Execute("update visit_tasks set status='Retired' where visit_id=@visit and id=any(@ids) and status='Pending'", c => { Add(c,"visit",visit.Id); Add(c,"ids",blocked); }, token);
            if (changed != blocked.Length) return (IActionResult)Conflict(new { message = "Tasks changed during review. Reload the visit." });
            await MaterializeVisitTasks(visit, token, force: true);
            await Execute("""
                insert into visit_task_reconciliations(id,visit_id,care_plan_id,organization_id,branch_id,reason,retired_task_ids,performed_by,performed_at)
                values(@id,@visit,@plan,@org,@branch,@reason,@ids,@actor,now())
                """, c => { Add(c,"id",Guid.NewGuid()); Add(c,"visit",visit.Id); Add(c,"plan",request.CarePlanId); Add(c,"org",visit.OrganizationId); Add(c,"branch",visit.BranchId); Add(c,"reason",request.Reason.Trim()); Add(c,"ids",blocked); Add(c,"actor",_currentUser.UserName); }, token);
            Audit("visit_tasks.reconciled","Visit",visit.Id,visit.OrganizationId,visit.BranchId);
            await _context.SaveChangesAsync(token);
            var result = await QueryVisitTasks(visit.Id, token);
            await tx.CommitAsync(token);
            return (IActionResult)Ok(result);
        });
    }

    [HttpPost("api/phase1/visits/{visitId:guid}/tasks/{taskId:guid}/outcome")]
    [TransactionalAction]
    public async Task<IActionResult> RecordOutcome(Guid visitId, Guid taskId, RecordVisitTaskOutcomeRequest request, CancellationToken cancellationToken)
    {
        var visit = await FindVisit(visitId, cancellationToken);
        if (visit is null) return NotFound();
        if (!CanCompleteVisit(visit)) return Forbid();
        await Execute("""select "Id" from "Visits" where "Id"=@visit and "OrganizationId"=@org for update""",
            c => { Add(c,"visit",visit.Id); Add(c,"org",visit.OrganizationId); }, cancellationToken);
        visit = await FindVisit(visitId, cancellationToken);
        if (visit is null || !CanCompleteVisit(visit)) return NotFound();
        var allowed = new[] { "Completed", "Partially completed", "Refused", "Not required", "Unable to complete" };
        if (!allowed.Contains(request.Outcome, StringComparer.OrdinalIgnoreCase)) return BadRequest(new { message = "Unsupported task outcome." });
        if (!request.Outcome.Equals("Completed", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(request.ExceptionReason))
            return BadRequest(new { message = "A reason is required when a task is not fully completed." });

        // Activation/supersession also locks the lifecycle row. Hold shared locks
        // through outcome/audit commit, then recheck Active state in the UPDATE.
        await Query("""
            select version.care_plan_id from visit_tasks task
            join care_plan_tasks source on source.id=task.care_plan_task_id
            join care_plan_versions version on version.care_plan_id=source.care_plan_id
            join "CarePlans" plan on plan."Id"=version.care_plan_id
            where task.id=@task and task.visit_id=@visit and task.organization_id=@org
              and source.service_user_id=task.service_user_id and source.organization_id=task.organization_id and source.branch_id=task.branch_id
              and version.service_user_id=task.service_user_id and version.organization_id=task.organization_id and version.branch_id=task.branch_id
              and plan."ServiceUserId"=task.service_user_id and plan."OrganizationId"=task.organization_id and plan."BranchId"=task.branch_id
            for share of version,plan
            """, c => { Add(c,"task",taskId); Add(c,"visit",visit.Id); Add(c,"org",visit.OrganizationId); }, r => r.GetGuid(0), cancellationToken);
        var affected = await Execute("""
            update visit_tasks set status='Completed', outcome=@outcome, exception_reason=@reason, completed_at=now()
            where id=@task and visit_id=@visit and organization_id=@organization
              and status not in ('Completed','Retired')
              and (care_plan_task_id is null or exists (
                select 1 from care_plan_tasks source
                join "CarePlans" plan on plan."Id"=source.care_plan_id
                join care_plan_versions version on version.care_plan_id=plan."Id"
                where source.id=visit_tasks.care_plan_task_id and source.status='Active'
                  and source.service_user_id=visit_tasks.service_user_id and source.organization_id=visit_tasks.organization_id and source.branch_id=visit_tasks.branch_id
                  and plan."ServiceUserId"=visit_tasks.service_user_id and plan."OrganizationId"=visit_tasks.organization_id and plan."BranchId"=visit_tasks.branch_id and plan."Status"='Active'
                  and version.service_user_id=visit_tasks.service_user_id and version.organization_id=visit_tasks.organization_id and version.branch_id=visit_tasks.branch_id and version.status='Active'))
            """, command =>
        {
            Add(command,"task",taskId); Add(command,"visit",visit.Id); Add(command,"organization",visit.OrganizationId ?? _tenant.OrganizationId);
            Add(command,"outcome",request.Outcome.Trim()); Add(command,"reason",request.ExceptionReason?.Trim() ?? "");
        }, cancellationToken);
        if (affected == 0)
        {
            var task = (await QueryVisitTasks(visit.Id, cancellationToken)).SingleOrDefault(item => item.Id == taskId);
            return task is null ? NotFound() : Conflict(new { message = "This task is already recorded or its plan requires review. Reload the visit and contact your manager." });
        }

        if (!request.Outcome.Equals("Completed", StringComparison.OrdinalIgnoreCase) && !request.Outcome.Equals("Not required", StringComparison.OrdinalIgnoreCase))
        {
            var exceptionId=Guid.NewGuid();
            await Execute("insert into visit_exceptions(id,visit_id,service_user_id,organization_id,branch_id,exception_type,severity,reason,immediate_action,notify_manager,follow_up_owner,escalation_due_at,status,created_by) values(@id,@visit,@person,@organization,@branch,'TaskException','High',@reason,'Review care delivery exception',true,'Care coordinator',now()+interval '1 hour','Open',@actor)", command=>{Add(command,"id",exceptionId);Add(command,"visit",visit.Id);Add(command,"person",visit.ServiceUserId);Add(command,"organization",visit.OrganizationId??_tenant.OrganizationId);Add(command,"branch",visit.BranchId??_tenant.BranchId??TenantDefaults.BranchId);Add(command,"reason",$"{request.Outcome}: {request.ExceptionReason}");Add(command,"actor",_currentUser.UserName);},cancellationToken);
            Audit("visit_task.exception_escalated","VisitException",exceptionId,visit.OrganizationId,visit.BranchId);
        }

        Audit("visit_task.outcome_recorded","VisitTask",taskId,visit.OrganizationId,visit.BranchId);
        await _context.SaveChangesAsync(cancellationToken);
        return Ok((await QueryVisitTasks(visit.Id, cancellationToken)).Single(item => item.Id == taskId));
    }

    private async Task MaterializeVisitTasks(Visit visit, CancellationToken cancellationToken, bool force = false)
    {
        var existing = await Scalar("select count(*) from visit_tasks where visit_id=@visit and organization_id=@organization", command =>
        {
            Add(command,"visit",visit.Id); Add(command,"organization",visit.OrganizationId ?? _tenant.OrganizationId);
        }, cancellationToken);
        if (existing > 0 && !force) return;

        await Execute("""
            insert into visit_tasks
              (id,visit_id,care_plan_task_id,service_user_id,care_worker_id,organization_id,branch_id,title,category,instructions,is_required,status,outcome,exception_reason,completed_at,created_at)
            select gen_random_uuid(), @visit, task.id, @person, @worker, @organization, @branch,
                   task.title, task.category, task.instructions, task.is_required, 'Pending', '', '', null, now()
            from care_plan_tasks task
            join "CarePlans" plan on plan."Id"=task.care_plan_id
            join care_plan_versions version on version.care_plan_id=plan."Id"
            where plan."ServiceUserId"=@person and plan."OrganizationId"=@organization and plan."BranchId"=@branch
              and plan."Status"='Active' and version.status='Active'
              and version.service_user_id=@person and version.organization_id=@organization and version.branch_id=@branch
              and task.service_user_id=@person and task.organization_id=@organization and task.branch_id=@branch
              and task.status='Active'
            on conflict (visit_id, care_plan_task_id) where care_plan_task_id is not null do nothing
            """, command =>
        {
            Add(command,"visit",visit.Id); Add(command,"person",visit.ServiceUserId); Add(command,"worker",visit.CareWorkerId);
            Add(command,"organization",visit.OrganizationId ?? _tenant.OrganizationId); Add(command,"branch",visit.BranchId ?? _tenant.BranchId ?? TenantDefaults.BranchId);
        }, cancellationToken);
    }

    private async Task<CarePlan?> FindPlan(Guid id, CancellationToken cancellationToken)
    {
        var plan = await _context.CarePlans.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return plan is not null && _tenant.CanAccess(plan.OrganizationId, plan.BranchId) ? plan : null;
    }

    private async Task<Visit?> FindVisit(Guid id, CancellationToken cancellationToken)
    {
        var visit = await _context.Visits.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return visit is not null && _tenant.CanAccess(visit.OrganizationId, visit.BranchId) ? visit : null;
    }

    private bool CanAccessVisit(Visit visit)
    {
        if (_currentUser.IsAdministrator || _currentUser.IsCareManager || _currentUser.IsCareCoordinator) return true;
        return _currentUser.IsCareWorker && _currentUser.CareWorkerId == visit.CareWorkerId;
    }

    private bool CanCompleteVisit(Visit visit) => CanAccessVisit(visit) && (!_currentUser.IsCareWorker || _currentUser.CareWorkerId == visit.CareWorkerId);

    private async Task<List<CarePlanTaskResponse>> QueryPlanTasks(Guid planId, CancellationToken cancellationToken) => await Query(
        "select id,care_plan_id,service_user_id,title,category,instructions,is_required,frequency,status from care_plan_tasks where care_plan_id=@plan and organization_id=@organization order by created_at",
        command => { Add(command,"plan",planId); Add(command,"organization",_tenant.OrganizationId); },
        reader => new CarePlanTaskResponse(reader.GetGuid(0),reader.GetGuid(1),reader.GetGuid(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetBoolean(6),reader.GetString(7),reader.GetString(8)), cancellationToken);

    private async Task<List<VisitTaskResponse>> QueryVisitTasks(Guid visitId, CancellationToken cancellationToken) => await Query(
        """
        select task.id,task.visit_id,task.care_plan_task_id,task.service_user_id,task.care_worker_id,task.title,task.category,
               task.instructions,task.is_required,task.status,task.outcome,task.exception_reason,task.completed_at,
               (task.care_plan_task_id is null or exists (
                 select 1 from care_plan_tasks source
                 join "CarePlans" plan on plan."Id"=source.care_plan_id
                 join care_plan_versions version on version.care_plan_id=plan."Id"
                 where source.id=task.care_plan_task_id and source.status='Active'
                   and source.service_user_id=task.service_user_id and source.organization_id=task.organization_id and source.branch_id=task.branch_id
                   and plan."ServiceUserId"=task.service_user_id and plan."OrganizationId"=task.organization_id and plan."BranchId"=task.branch_id and plan."Status"='Active'
                   and version.service_user_id=task.service_user_id and version.organization_id=task.organization_id and version.branch_id=task.branch_id and version.status='Active')) source_current
        from visit_tasks task where task.visit_id=@visit and task.organization_id=@organization order by task.created_at
        """,
        command => { Add(command,"visit",visitId); Add(command,"organization",_tenant.OrganizationId); },
        reader =>
        {
            var retired = reader.GetString(9) == "Retired";
            var review = reader.GetString(9) != "Completed" && !retired && !reader.GetBoolean(13);
            return new VisitTaskResponse(reader.GetGuid(0),reader.GetGuid(1),reader.IsDBNull(2)?null:reader.GetGuid(2),reader.GetGuid(3),reader.GetGuid(4),
                retired ? "Previous plan task — retired by manager review" : review ? "Previous plan task — manager review required" : reader.GetString(5),reader.GetString(6),review || retired ? "" : reader.GetString(7),reader.GetBoolean(8),
                review ? "RequiresReview" : reader.GetString(9),reader.GetString(10),reader.GetString(11),reader.IsDBNull(12)?null:reader.GetDateTime(12));
        }, cancellationToken);

    private async Task<List<T>> Query<T>(string sql, Action<DbCommand> bind, Func<DbDataReader,T> map, CancellationToken cancellationToken)
    {
        var result = new List<T>(); var connection = _context.Database.GetDbConnection(); var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try { await using var command=connection.CreateCommand(); command.Transaction=_context.Database.CurrentTransaction?.GetDbTransaction(); command.CommandText=sql; bind(command); await using var reader=await command.ExecuteReaderAsync(cancellationToken); while(await reader.ReadAsync(cancellationToken)) result.Add(map(reader)); return result; }
        finally { if(opened) await connection.CloseAsync(); }
    }

    private async Task<int> Execute(string sql, Action<DbCommand> bind, CancellationToken cancellationToken)
    {
        var connection=_context.Database.GetDbConnection(); var opened=connection.State!=ConnectionState.Open; if(opened) await connection.OpenAsync(cancellationToken);
        try { await using var command=connection.CreateCommand(); command.Transaction=_context.Database.CurrentTransaction?.GetDbTransaction(); command.CommandText=sql; bind(command); return await command.ExecuteNonQueryAsync(cancellationToken); }
        finally { if(opened) await connection.CloseAsync(); }
    }

    private async Task<int> Scalar(string sql, Action<DbCommand> bind, CancellationToken cancellationToken)
    {
        var connection=_context.Database.GetDbConnection(); var opened=connection.State!=ConnectionState.Open; if(opened) await connection.OpenAsync(cancellationToken);
        try { await using var command=connection.CreateCommand(); command.Transaction=_context.Database.CurrentTransaction?.GetDbTransaction(); command.CommandText=sql; bind(command); return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)); }
        finally { if(opened) await connection.CloseAsync(); }
    }

    private static void Add(DbCommand command,string name,object? value) { var parameter=command.CreateParameter(); parameter.ParameterName=name; parameter.Value=value??DBNull.Value; command.Parameters.Add(parameter); }
    private void Audit(string action,string entityType,Guid id,Guid? organizationId,Guid? branchId) => _context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),action,_currentUser.UserName,entityType,id,DateTimeOffset.UtcNow,organizationId,branchId));
}

public sealed record CreateCarePlanTaskRequest(string Title,string? Category,string? Instructions,bool IsRequired=true,string? Frequency="Every visit");
public sealed record ReconcileVisitTasksRequest(Guid CarePlanId, string Reason);
public sealed record RecordVisitTaskOutcomeRequest(string Outcome,string? ExceptionReason);
public sealed record CarePlanTaskResponse(Guid Id,Guid CarePlanId,Guid ServiceUserId,string Title,string Category,string Instructions,bool IsRequired,string Frequency,string Status);
public sealed record VisitTaskResponse(Guid Id,Guid VisitId,Guid? CarePlanTaskId,Guid ServiceUserId,Guid CareWorkerId,string Title,string Category,string Instructions,bool IsRequired,string Status,string Outcome,string ExceptionReason,DateTimeOffset? CompletedAt);
