using System.Data.Common;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[ApiController]
[Authorize(Policy="Phase1User")]
[Route("api/phase1/visit-operations")]
public sealed class VisitOperationsController(CareDbContext db,ITenantContext tenant,ICurrentUserContext user):ControllerBase
{
    private static readonly string[] ExceptionTypes=["Late","Missed","Shortened","Cancelled","NoAccess","RefusedCare"];
    [HttpGet("visits/{visitId:guid}")]
    public async Task<IActionResult> Workspace(Guid visitId,CancellationToken token){var visit=await Visit(visitId,token);if(visit is null||!await CanAccess(visit,token))return NotFound();return Ok(new{exceptions=await Exceptions(visitId,token),handovers=await Handovers(visitId,token),scheduleHistory=await History(visitId,token)});}

    [HttpPost("visits/{visitId:guid}/exceptions")]
    public async Task<IActionResult> AddException(Guid visitId,CreateVisitExceptionRequest request,CancellationToken token)
    {
        var visit=await Visit(visitId,token);if(visit is null||!await CanAccess(visit,token))return NotFound();
        if(!ExceptionTypes.Contains(request.ExceptionType)||string.IsNullOrWhiteSpace(request.Reason)||string.IsNullOrWhiteSpace(request.ImmediateAction)||string.IsNullOrWhiteSpace(request.FollowUpOwner))return BadRequest(new{message="Exception type, reason, immediate action, and follow-up owner are required."});
        var id=Guid.NewGuid();var due=request.EscalationDueAt??DateTimeOffset.UtcNow.Add(request.Severity is "Critical"?TimeSpan.FromMinutes(15):request.Severity is "High"?TimeSpan.FromHours(1):TimeSpan.FromHours(4));
        await Execute("insert into visit_exceptions(id,visit_id,service_user_id,organization_id,branch_id,exception_type,severity,reason,immediate_action,notify_manager,follow_up_owner,escalation_due_at,status,created_by) values(@id,@visit,@person,@organization,@branch,@type,@severity,@reason,@action,@notify,@owner,@due,'Open',@actor)",c=>{Add(c,"id",id);Add(c,"visit",visit.Id);Add(c,"person",visit.ServiceUserId);Add(c,"type",request.ExceptionType);Add(c,"severity",request.Severity);Add(c,"reason",request.Reason.Trim());Add(c,"action",request.ImmediateAction.Trim());Add(c,"notify",request.NotifyManager);Add(c,"owner",request.FollowUpOwner.Trim());Add(c,"due",due);},token);
        await Event(id,"Created",request.ImmediateAction,token);Audit("visit_exception.created",id);await db.SaveChangesAsync(token);return Created($"/api/phase1/visit-operations/exceptions/{id}",new{id});
    }

    [HttpPatch("exceptions/{id:guid}")]
    [Authorize(Roles="CareCoordinator,CareManager,Administrator")]
    public async Task<IActionResult> Progress(Guid id,ProgressEscalationRequest request,CancellationToken token)
    {
        var action=request.Action.Trim();if(action is not ("Acknowledge" or "Resolve" or "Close"))return BadRequest(new{message="Action must be Acknowledge, Resolve, or Close."});
        var status=action=="Acknowledge"?"Acknowledged":action=="Resolve"?"Resolved":"Closed";var column=action=="Acknowledge"?"acknowledged_at":action=="Resolve"?"resolved_at":"closed_at";
        var changed=await Execute($"update visit_exceptions set status=@status,{column}=now(),updated_at=now() where id=@id and organization_id=@organization",c=>{Add(c,"id",id);Add(c,"status",status);},token);
        if(changed==0)return NotFound();await Event(id,action,request.Detail,token);Audit($"visit_exception.{status.ToLowerInvariant()}",id);await db.SaveChangesAsync(token);return NoContent();
    }

    [HttpPost("visits/{visitId:guid}/handovers")]
    public async Task<IActionResult> AddHandover(Guid visitId,CreateHandoverRequest request,CancellationToken token)
    {
        var visit=await Visit(visitId,token);if(visit is null||!await CanAccess(visit,token))return NotFound();if(string.IsNullOrWhiteSpace(request.Summary)||string.IsNullOrWhiteSpace(request.OutstandingActions))return BadRequest(new{message="Summary and outstanding actions are required."});
        var id=Guid.NewGuid();await Execute("insert into visit_handovers(id,visit_id,service_user_id,organization_id,branch_id,summary,outstanding_actions,urgent,attachment_reference,created_by) values(@id,@visit,@person,@organization,@branch,@summary,@actions,@urgent,@attachment,@actor)",c=>{Add(c,"id",id);Add(c,"visit",visit.Id);Add(c,"person",visit.ServiceUserId);Add(c,"summary",request.Summary.Trim());Add(c,"actions",request.OutstandingActions.Trim());Add(c,"urgent",request.Urgent);Add(c,"attachment",request.AttachmentReference??"");},token);Audit("visit_handover.created",id);await db.SaveChangesAsync(token);return Created($"/api/phase1/visit-operations/handovers/{id}",new{id});
    }

    [HttpPost("handovers/{id:guid}/acknowledge")]
    public async Task<IActionResult> Acknowledge(Guid id,CancellationToken token){var changed=await Execute("insert into handover_acknowledgements(handover_id,user_id,organization_id) select id,@userId,@organization from visit_handovers where id=@id and organization_id=@organization on conflict do nothing",c=>{Add(c,"id",id);Add(c,"userId",user.UserId);},token);if(changed==0)return NoContent();Audit("visit_handover.acknowledged",id);await db.SaveChangesAsync(token);return NoContent();}

    [HttpGet("dashboard")]
    [Authorize(Roles="CareCoordinator,CareManager,Administrator")]
    public async Task<IActionResult> Dashboard(CancellationToken token)
    {
        await using var c=await Command("select (select count(*) from visit_exceptions where organization_id=@organization and status not in ('Resolved','Closed')),(select count(*) from visit_exceptions where organization_id=@organization and status not in ('Resolved','Closed') and escalation_due_at<now()),(select count(*) from visit_handovers h where h.organization_id=@organization and h.urgent and not exists(select 1 from handover_acknowledgements a where a.handover_id=h.id and a.user_id=@userId)),(select count(*) from schedule_change_history where organization_id=@organization and changed_at>now()-interval '7 days')",token);Add(c,"userId",user.UserId);await using var reader=await c.ExecuteReaderAsync(token);await reader.ReadAsync(token);return Ok(new{openExceptions=reader.GetInt64(0),overdueEscalations=reader.GetInt64(1),unacknowledgedUrgentHandovers=reader.GetInt64(2),scheduleChangesLast7Days=reader.GetInt64(3)});
    }

    private async Task<Visit?> Visit(Guid id,CancellationToken t)=>await db.Visits.SingleOrDefaultAsync(v=>v.Id==id&&v.OrganizationId==tenant.OrganizationId,t);
    private async Task<bool> CanAccess(Visit visit,CancellationToken t){if(user.IsAdministrator||user.IsCareManager||user.IsCareCoordinator)return true;if(!user.IsCareWorker||user.CareWorkerId is null)return false;if(visit.CareWorkerId==user.CareWorkerId)return true;await using var c=await Command("select exists(select 1 from visit_care_worker_assignments where visit_id=@visit and care_worker_id=@worker and organization_id=@organization)",t);Add(c,"visit",visit.Id);Add(c,"worker",user.CareWorkerId.Value);return Convert.ToBoolean(await c.ExecuteScalarAsync(t));}
    private async Task<List<VisitExceptionResponse>> Exceptions(Guid visit,CancellationToken t)=>await Query<VisitExceptionResponse>("select id,exception_type,severity,reason,immediate_action,notify_manager,follow_up_owner,escalation_due_at,status,created_by,created_at from visit_exceptions where visit_id=@visit and organization_id=@organization order by created_at desc",visit,r=>new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetBoolean(5),r.GetString(6),r.GetFieldValue<DateTimeOffset>(7),r.GetString(8),r.GetString(9),r.GetFieldValue<DateTimeOffset>(10)),t);
    private async Task<List<HandoverResponse>> Handovers(Guid visit,CancellationToken t)=>await Query<HandoverResponse>("select h.id,h.summary,h.outstanding_actions,h.urgent,h.attachment_reference,h.created_by,h.created_at,exists(select 1 from handover_acknowledgements a where a.handover_id=h.id and a.user_id=@userId) from visit_handovers h where h.visit_id=@visit and h.organization_id=@organization order by h.created_at desc",visit,r=>new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetBoolean(3),r.GetString(4),r.GetString(5),r.GetFieldValue<DateTimeOffset>(6),r.GetBoolean(7)),t,true);
    private async Task<List<ScheduleChangeResponse>> History(Guid visit,CancellationToken t)=>await Query<ScheduleChangeResponse>("select id,change_type,old_values_json,new_values_json,reason,changed_by,changed_at from schedule_change_history where visit_id=@visit and organization_id=@organization order by changed_at desc",visit,r=>new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetFieldValue<DateTimeOffset>(6)),t);
    private async Task<List<T>> Query<T>(string sql,Guid visit,Func<DbDataReader,T> map,CancellationToken t,bool addUser=false){var rows=new List<T>();await using var c=await Command(sql,t);Add(c,"visit",visit);if(addUser)Add(c,"userId",user.UserId);await using var r=await c.ExecuteReaderAsync(t);while(await r.ReadAsync(t))rows.Add(map(r));return rows;}
    private Task<int> Event(Guid id,string action,string detail,CancellationToken t)=>Execute("insert into visit_escalation_events(id,visit_exception_id,organization_id,action,detail,actor) values(@event,@id,@organization,@action,@detail,@actor)",c=>{Add(c,"event",Guid.NewGuid());Add(c,"id",id);Add(c,"action",action);Add(c,"detail",detail??"");},t);
    private void Audit(string action,Guid id)=>db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),action,user.UserName,"VisitOperations",id,DateTimeOffset.UtcNow,tenant.OrganizationId,tenant.BranchId??TenantDefaults.BranchId));
    private async Task<int> Execute(string sql,Action<DbCommand> bind,CancellationToken t){await using var c=await Command(sql,t);bind(c);return await c.ExecuteNonQueryAsync(t);}
    private async Task<DbCommand> Command(string sql,CancellationToken t){var connection=db.Database.GetDbConnection();if(connection.State!=System.Data.ConnectionState.Open)await connection.OpenAsync(t);var c=connection.CreateCommand();c.CommandText=sql;Add(c,"organization",tenant.OrganizationId);Add(c,"branch",tenant.BranchId??TenantDefaults.BranchId);Add(c,"actor",user.UserName);return c;}
    private static void Add(DbCommand c,string n,object? v){if(c.Parameters.Contains(n))return;var p=c.CreateParameter();p.ParameterName=n;p.Value=v??DBNull.Value;c.Parameters.Add(p);}
}

public sealed record CreateVisitExceptionRequest(string ExceptionType,string Severity,string Reason,string ImmediateAction,bool NotifyManager,string FollowUpOwner,DateTimeOffset? EscalationDueAt);
public sealed record ProgressEscalationRequest(string Action,string Detail);
public sealed record CreateHandoverRequest(string Summary,string OutstandingActions,bool Urgent,string? AttachmentReference);
public sealed record VisitExceptionResponse(Guid Id,string ExceptionType,string Severity,string Reason,string ImmediateAction,bool NotifyManager,string FollowUpOwner,DateTimeOffset EscalationDueAt,string Status,string CreatedBy,DateTimeOffset CreatedAt);
public sealed record HandoverResponse(Guid Id,string Summary,string OutstandingActions,bool Urgent,string AttachmentReference,string CreatedBy,DateTimeOffset CreatedAt,bool AcknowledgedByCurrentUser);
public sealed record ScheduleChangeResponse(Guid Id,string ChangeType,string OldValuesJson,string NewValuesJson,string Reason,string ChangedBy,DateTimeOffset ChangedAt);
