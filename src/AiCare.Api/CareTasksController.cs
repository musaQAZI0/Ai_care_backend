using System.Data;
using System.Data.Common;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
        if (string.Equals(plan.Status, "Archived", StringComparison.OrdinalIgnoreCase)) return BadRequest(new { message = "Archived care plans cannot receive new tasks." });

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

    [HttpPost("api/phase1/visits/{visitId:guid}/tasks/{taskId:guid}/outcome")]
    public async Task<IActionResult> RecordOutcome(Guid visitId, Guid taskId, RecordVisitTaskOutcomeRequest request, CancellationToken cancellationToken)
    {
        var visit = await FindVisit(visitId, cancellationToken);
        if (visit is null) return NotFound();
        if (!CanCompleteVisit(visit)) return Forbid();
        var allowed = new[] { "Completed", "Partially completed", "Refused", "Not required", "Unable to complete" };
        if (!allowed.Contains(request.Outcome, StringComparer.OrdinalIgnoreCase)) return BadRequest(new { message = "Unsupported task outcome." });
        if (!request.Outcome.Equals("Completed", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(request.ExceptionReason))
            return BadRequest(new { message = "A reason is required when a task is not fully completed." });

        var affected = await Execute("""
            update visit_tasks set status='Completed', outcome=@outcome, exception_reason=@reason, completed_at=now()
            where id=@task and visit_id=@visit and organization_id=@organization
            """, command =>
        {
            Add(command,"task",taskId); Add(command,"visit",visit.Id); Add(command,"organization",visit.OrganizationId ?? _tenant.OrganizationId);
            Add(command,"outcome",request.Outcome.Trim()); Add(command,"reason",request.ExceptionReason?.Trim() ?? "");
        }, cancellationToken);
        if (affected == 0) return NotFound();

        Audit("visit_task.outcome_recorded","VisitTask",taskId,visit.OrganizationId,visit.BranchId);
        await _context.SaveChangesAsync(cancellationToken);
        return Ok((await QueryVisitTasks(visit.Id, cancellationToken)).Single(item => item.Id == taskId));
    }

    private async Task MaterializeVisitTasks(Visit visit, CancellationToken cancellationToken)
    {
        var existing = await Scalar("select count(*) from visit_tasks where visit_id=@visit and organization_id=@organization", command =>
        {
            Add(command,"visit",visit.Id); Add(command,"organization",visit.OrganizationId ?? _tenant.OrganizationId);
        }, cancellationToken);
        if (existing > 0) return;

        var plan = await _context.CarePlans
            .Where(item => item.ServiceUserId == visit.ServiceUserId && (item.Status == "Active" || item.Status == "Approved"))
            .OrderByDescending(item => item.Status == "Active")
            .ThenByDescending(item => item.ReviewDueAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (plan is null) return;

        await Execute("""
            insert into visit_tasks
              (id,visit_id,care_plan_task_id,service_user_id,care_worker_id,organization_id,branch_id,title,category,instructions,is_required,status,outcome,exception_reason,completed_at,created_at)
            select gen_random_uuid(), @visit, task.id, @person, @worker, @organization, @branch,
                   task.title, task.category, task.instructions, task.is_required, 'Pending', '', '', null, now()
            from care_plan_tasks task
            where task.care_plan_id=@plan and task.organization_id=@organization and task.status='Active'
            on conflict (visit_id, care_plan_task_id) where care_plan_task_id is not null do nothing
            """, command =>
        {
            Add(command,"visit",visit.Id); Add(command,"person",visit.ServiceUserId); Add(command,"worker",visit.CareWorkerId);
            Add(command,"organization",visit.OrganizationId ?? _tenant.OrganizationId); Add(command,"branch",visit.BranchId ?? _tenant.BranchId ?? TenantDefaults.BranchId); Add(command,"plan",plan.Id);
        }, cancellationToken);
    }

    private async Task<CarePlan?> FindPlan(Guid id, CancellationToken cancellationToken)
    {
        var plan = await _context.CarePlans.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return plan is not null && _tenant.CanAccess(plan.OrganizationId, plan.BranchId) ? plan : null;
    }

    private async Task<Visit?> FindVisit(Guid id, CancellationToken cancellationToken)
    {
        var visit = await _context.Visits.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
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
        "select id,visit_id,care_plan_task_id,service_user_id,care_worker_id,title,category,instructions,is_required,status,outcome,exception_reason,completed_at from visit_tasks where visit_id=@visit and organization_id=@organization order by created_at",
        command => { Add(command,"visit",visitId); Add(command,"organization",_tenant.OrganizationId); },
        reader => new VisitTaskResponse(reader.GetGuid(0),reader.GetGuid(1),reader.IsDBNull(2)?null:reader.GetGuid(2),reader.GetGuid(3),reader.GetGuid(4),reader.GetString(5),reader.GetString(6),reader.GetString(7),reader.GetBoolean(8),reader.GetString(9),reader.GetString(10),reader.GetString(11),reader.IsDBNull(12)?null:reader.GetDateTime(12)), cancellationToken);

    private async Task<List<T>> Query<T>(string sql, Action<DbCommand> bind, Func<DbDataReader,T> map, CancellationToken cancellationToken)
    {
        var result = new List<T>(); var connection = _context.Database.GetDbConnection(); var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try { await using var command=connection.CreateCommand(); command.CommandText=sql; bind(command); await using var reader=await command.ExecuteReaderAsync(cancellationToken); while(await reader.ReadAsync(cancellationToken)) result.Add(map(reader)); return result; }
        finally { if(opened) await connection.CloseAsync(); }
    }

    private async Task<int> Execute(string sql, Action<DbCommand> bind, CancellationToken cancellationToken)
    {
        var connection=_context.Database.GetDbConnection(); var opened=connection.State!=ConnectionState.Open; if(opened) await connection.OpenAsync(cancellationToken);
        try { await using var command=connection.CreateCommand(); command.CommandText=sql; bind(command); return await command.ExecuteNonQueryAsync(cancellationToken); }
        finally { if(opened) await connection.CloseAsync(); }
    }

    private async Task<int> Scalar(string sql, Action<DbCommand> bind, CancellationToken cancellationToken)
    {
        var connection=_context.Database.GetDbConnection(); var opened=connection.State!=ConnectionState.Open; if(opened) await connection.OpenAsync(cancellationToken);
        try { await using var command=connection.CreateCommand(); command.CommandText=sql; bind(command); return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)); }
        finally { if(opened) await connection.CloseAsync(); }
    }

    private static void Add(DbCommand command,string name,object? value) { var parameter=command.CreateParameter(); parameter.ParameterName=name; parameter.Value=value??DBNull.Value; command.Parameters.Add(parameter); }
    private void Audit(string action,string entityType,Guid id,Guid? organizationId,Guid? branchId) => _context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),action,_currentUser.UserName,entityType,id,DateTimeOffset.UtcNow,organizationId,branchId));
}

public sealed record CreateCarePlanTaskRequest(string Title,string? Category,string? Instructions,bool IsRequired=true,string? Frequency="Every visit");
public sealed record RecordVisitTaskOutcomeRequest(string Outcome,string? ExceptionReason);
public sealed record CarePlanTaskResponse(Guid Id,Guid CarePlanId,Guid ServiceUserId,string Title,string Category,string Instructions,bool IsRequired,string Frequency,string Status);
public sealed record VisitTaskResponse(Guid Id,Guid VisitId,Guid? CarePlanTaskId,Guid ServiceUserId,Guid CareWorkerId,string Title,string Category,string Instructions,bool IsRequired,string Status,string Outcome,string ExceptionReason,DateTimeOffset? CompletedAt);
