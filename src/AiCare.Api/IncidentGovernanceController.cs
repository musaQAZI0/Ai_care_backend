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
[Authorize(Roles="CareCoordinator,CareManager,Administrator")]
[Route("api/phase1/incident-governance")]
public sealed class IncidentGovernanceController(CareDbContext db,ITenantContext tenant,ICurrentUserContext user):ControllerBase
{
 [HttpGet("incidents/{incidentId:guid}")]
 public async Task<IActionResult> Workspace(Guid incidentId,CancellationToken t){var incident=await Incident(incidentId,t);if(incident is null)return NotFound();return Ok(new{incident,investigation=await Investigation(incidentId,t),actions=await Actions(incidentId,t),history=await History(incidentId,t)});}

 [HttpPut("incidents/{incidentId:guid}/investigation")]
 public async Task<IActionResult> SaveInvestigation(Guid incidentId,InvestigationRequest request,CancellationToken t)
 {
  var incident=await Incident(incidentId,t);if(incident is null)return NotFound();
  if(string.IsNullOrWhiteSpace(request.Chronology)||string.IsNullOrWhiteSpace(request.EvidenceReviewed)||string.IsNullOrWhiteSpace(request.Findings)||string.IsNullOrWhiteSpace(request.RootCause)||string.IsNullOrWhiteSpace(request.Owner))return BadRequest(new{message="Chronology, evidence, findings, root cause and owner are required."});
  if(request.Complete&&string.IsNullOrWhiteSpace(request.LessonsLearned))return BadRequest(new{message="Lessons learned are required to complete an investigation."});
  if(request.Complete&&!user.IsCareManager&&!user.IsAdministrator)return Forbid();
  var id=Guid.NewGuid();var status=request.Complete?"Completed":"InProgress";
  await Execute("insert into incident_investigations(id,incident_id,organization_id,branch_id,status,chronology,evidence_reviewed,findings,root_cause,lessons_learned,owner,reviewed_by,completed_at) values(@id,@incident,@organization,@branch,@status,@chronology,@evidence,@findings,@root,@lessons,@owner,@reviewer,@completed) on conflict(incident_id) do update set status=@status,chronology=@chronology,evidence_reviewed=@evidence,findings=@findings,root_cause=@root,lessons_learned=@lessons,owner=@owner,reviewed_by=@reviewer,completed_at=@completed,updated_at=now()",c=>{Add(c,"id",id);Add(c,"incident",incidentId);Add(c,"status",status);Add(c,"chronology",request.Chronology.Trim());Add(c,"evidence",request.EvidenceReviewed.Trim());Add(c,"findings",request.Findings.Trim());Add(c,"root",request.RootCause.Trim());Add(c,"lessons",request.LessonsLearned?.Trim()??"");Add(c,"owner",request.Owner.Trim());Add(c,"reviewer",request.Complete?user.UserName:"");Add(c,"completed",request.Complete?DateTimeOffset.UtcNow:null);},t);
  await SetIncidentStatus(incident,request.Complete?"Investigation completed":"Under investigation",t);await Event(incidentId,request.Complete?"InvestigationCompleted":"InvestigationUpdated",request.Findings,t);Audit("incident.investigation_saved",incidentId);await db.SaveChangesAsync(t);return Ok(await Investigation(incidentId,t));
 }

 [HttpPost("incidents/{incidentId:guid}/actions")]
 public async Task<IActionResult> AddAction(Guid incidentId,CapaRequest request,CancellationToken t){if(await Incident(incidentId,t) is null)return NotFound();if(request.ActionType is not("Corrective" or "Preventive")||string.IsNullOrWhiteSpace(request.Detail)||string.IsNullOrWhiteSpace(request.Owner)||request.DueAt<=DateTimeOffset.UtcNow)return BadRequest(new{message="Corrective/Preventive type, detail, owner and a future due date are required."});var id=Guid.NewGuid();await Execute("insert into incident_capa_actions(id,incident_id,organization_id,branch_id,action_type,detail,owner,due_at) values(@id,@incident,@organization,@branch,@type,@detail,@owner,@due)",c=>{Add(c,"id",id);Add(c,"incident",incidentId);Add(c,"type",request.ActionType);Add(c,"detail",request.Detail.Trim());Add(c,"owner",request.Owner.Trim());Add(c,"due",request.DueAt);},t);await Event(incidentId,"CapaAdded",request.Detail,t);Audit("incident.capa_added",incidentId);await db.SaveChangesAsync(t);return Created($"/api/phase1/incident-governance/incidents/{incidentId}/actions/{id}",new{id});}

 [HttpPost("incidents/{incidentId:guid}/actions/{actionId:guid}/complete")]
 public async Task<IActionResult> CompleteAction(Guid incidentId,Guid actionId,CompleteCapaRequest request,CancellationToken t){if(await Incident(incidentId,t) is null)return NotFound();if(string.IsNullOrWhiteSpace(request.CompletionEvidence))return BadRequest(new{message="Completion evidence is required."});var changed=await Execute("update incident_capa_actions set status='Completed',completion_evidence=@evidence,completed_by=@actor,completed_at=now() where id=@id and incident_id=@incident and organization_id=@organization and status='Open'",c=>{Add(c,"evidence",request.CompletionEvidence.Trim());Add(c,"actor",user.UserName);Add(c,"id",actionId);Add(c,"incident",incidentId);},t);if(changed==0)return NotFound();await Event(incidentId,"CapaCompleted",request.CompletionEvidence,t);Audit("incident.capa_completed",incidentId);await db.SaveChangesAsync(t);return NoContent();}

 [HttpPost("incidents/{incidentId:guid}/close")]
 public async Task<IActionResult> Close(Guid incidentId,CloseIncidentRequest request,CancellationToken t){if(!user.IsCareManager&&!user.IsAdministrator)return Forbid();var incident=await Incident(incidentId,t);if(incident is null)return NotFound();if(string.IsNullOrWhiteSpace(request.ClosureSummary))return BadRequest(new{message="Closure summary is required."});var investigation=await Investigation(incidentId,t);if(investigation is null||investigation.Status!="Completed")return Conflict(new{message="A completed investigation is required before closure."});if(await Scalar("select count(*) from incident_capa_actions where incident_id=@incident and organization_id=@organization and status='Open'",incidentId,t)>0)return Conflict(new{message="All corrective and preventive actions must be completed before closure."});await SetIncidentStatus(incident,"Closed",t);await Event(incidentId,"Closed",request.ClosureSummary.Trim(),t);Audit("incident.closed",incidentId);await db.SaveChangesAsync(t);return NoContent();}

 private async Task<Incident?> Incident(Guid id,CancellationToken t){var row=await db.Incidents.SingleOrDefaultAsync(x=>x.Id==id&&x.OrganizationId==tenant.OrganizationId,t);return row is null||!tenant.CanAccess(row.OrganizationId,row.BranchId)?null:row;}
 private async Task SetIncidentStatus(Incident incident,string status,CancellationToken t){db.Entry(incident).CurrentValues.SetValues(incident with{Status=status});await db.SaveChangesAsync(t);}
 private async Task<InvestigationResponse?> Investigation(Guid id,CancellationToken t){var rows=await Query("select id,status,chronology,evidence_reviewed,findings,root_cause,lessons_learned,owner,reviewed_by,started_at,completed_at,updated_at from incident_investigations where incident_id=@incident and organization_id=@organization",id,r=>new InvestigationResponse(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7),r.GetString(8),Date(r,9)!.Value,Date(r,10),Date(r,11)!.Value),t);return rows.SingleOrDefault();}
 private Task<List<CapaResponse>> Actions(Guid id,CancellationToken t)=>Query("select id,action_type,detail,owner,due_at,status,completion_evidence,completed_by,completed_at,created_at from incident_capa_actions where incident_id=@incident and organization_id=@organization order by created_at",id,r=>new CapaResponse(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetString(3),Date(r,4)!.Value,r.GetString(5),r.GetString(6),r.GetString(7),Date(r,8),Date(r,9)!.Value),t);
 private Task<List<IncidentEventResponse>> History(Guid id,CancellationToken t)=>Query("select id,event_type,detail,actor,occurred_at from incident_governance_events where incident_id=@incident and organization_id=@organization order by occurred_at",id,r=>new IncidentEventResponse(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetString(3),Date(r,4)!.Value),t);
 private Task Event(Guid id,string type,string detail,CancellationToken t)=>Execute("insert into incident_governance_events(id,incident_id,organization_id,branch_id,event_type,detail,actor) values(@id,@incident,@organization,@branch,@type,@detail,@actor)",c=>{Add(c,"id",Guid.NewGuid());Add(c,"incident",id);Add(c,"type",type);Add(c,"detail",detail);Add(c,"actor",user.UserName);},t);
 private void Audit(string action,Guid id)=>db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),action,user.UserName,"Incident",id,DateTimeOffset.UtcNow,tenant.OrganizationId,tenant.BranchId??TenantDefaults.BranchId));
 private async Task<long> Scalar(string sql,Guid id,CancellationToken t){await using var c=await Command(sql,t);Add(c,"incident",id);return Convert.ToInt64(await c.ExecuteScalarAsync(t));}
 private async Task<List<T>> Query<T>(string sql,Guid id,Func<DbDataReader,T> map,CancellationToken t){var rows=new List<T>();await using var c=await Command(sql,t);Add(c,"incident",id);await using var r=await c.ExecuteReaderAsync(t);while(await r.ReadAsync(t))rows.Add(map(r));return rows;}
 private async Task<int> Execute(string sql,Action<DbCommand> bind,CancellationToken t){await using var c=await Command(sql,t);bind(c);return await c.ExecuteNonQueryAsync(t);}
 private async Task<DbCommand> Command(string sql,CancellationToken t){var connection=db.Database.GetDbConnection();if(connection.State!=ConnectionState.Open)await connection.OpenAsync(t);var c=connection.CreateCommand();c.CommandText=sql;Add(c,"organization",tenant.OrganizationId);Add(c,"branch",tenant.BranchId??TenantDefaults.BranchId);return c;}
 private static DateTimeOffset? Date(DbDataReader r,int i)=>r.IsDBNull(i)?null:r.GetFieldValue<DateTimeOffset>(i);
 private static void Add(DbCommand c,string n,object? v){if(c.Parameters.Contains(n))return;var p=c.CreateParameter();p.ParameterName=n;p.Value=v??DBNull.Value;c.Parameters.Add(p);}
}
public sealed record InvestigationRequest(string Chronology,string EvidenceReviewed,string Findings,string RootCause,string? LessonsLearned,string Owner,bool Complete);
public sealed record CapaRequest(string ActionType,string Detail,string Owner,DateTimeOffset DueAt);
public sealed record CompleteCapaRequest(string CompletionEvidence);
public sealed record CloseIncidentRequest(string ClosureSummary);
public sealed record InvestigationResponse(Guid Id,string Status,string Chronology,string EvidenceReviewed,string Findings,string RootCause,string LessonsLearned,string Owner,string ReviewedBy,DateTimeOffset StartedAt,DateTimeOffset? CompletedAt,DateTimeOffset UpdatedAt);
public sealed record CapaResponse(Guid Id,string ActionType,string Detail,string Owner,DateTimeOffset DueAt,string Status,string CompletionEvidence,string CompletedBy,DateTimeOffset? CompletedAt,DateTimeOffset CreatedAt);
public sealed record IncidentEventResponse(Guid Id,string EventType,string Detail,string Actor,DateTimeOffset OccurredAt);
