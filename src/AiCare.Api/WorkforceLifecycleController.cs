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
[Route("api/phase1/workforce-lifecycle/care-workers/{workerId:guid}")]
public sealed class WorkforceLifecycleController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Workspace(Guid workerId, CancellationToken token)
    {
        if (!CanRead(workerId) || !await WorkerExists(workerId, token)) return NotFound();
        var employment = await One("select recruitment_status,employment_status,contract_type,job_title,contracted_weekly_minutes,start_date,end_date,line_manager,status_reason,approved_by,approved_at,updated_at from worker_employment_profiles where care_worker_id=@worker and organization_id=@organization and branch_id=@branch", workerId, token);
        var supervisions = await Many("select id,record_type,scheduled_at,completed_at,supervisor,discussion,outcome,actions,evidence_reference,review_due,status from worker_supervision_records where care_worker_id=@worker and organization_id=@organization and branch_id=@branch order by scheduled_at desc", workerId, token);
        var absences = await Many("select a.id,a.absence_type,a.starts_at,a.ends_at,a.status,a.notes,a.cover_status,a.covered_by,a.cover_notes,r.id as return_to_work_id,r.meeting_at,r.fit_to_return,r.adjustments,r.restrictions,r.evidence_reference,r.review_due,r.manager,r.completed_at from worker_absences a left join worker_return_to_work_reviews r on r.absence_id=a.id where a.care_worker_id=@worker and a.organization_id=@organization and a.branch_id=@branch order by a.starts_at desc", workerId, token);
        var events = await Many("select id,event_type,entity_type,entity_id,actor,detail,occurred_at from workforce_lifecycle_events where care_worker_id=@worker and organization_id=@organization and branch_id=@branch order by occurred_at desc", workerId, token);
        return Ok(new { careWorkerId = workerId, employment, supervisions, absences, events, alerts = BuildAlerts(supervisions, absences) });
    }

    [HttpPut("employment")]
    [Authorize(Roles = "CareManager,Administrator")]
    public async Task<IActionResult> Employment(Guid workerId, EmploymentRequest request, CancellationToken token)
    {
        if (!await WorkerExists(workerId, token)) return NotFound();
        var statuses = new[] { "PreEmployment", "Employed", "Suspended", "LeaveOfAbsence", "Leaver" };
        if (!statuses.Contains(request.EmploymentStatus) || request.ContractedWeeklyMinutes < 0) return BadRequest(new { message = "Employment status or contracted minutes are invalid." });
        if (request.EmploymentStatus == "Employed" && (request.StartDate is null || string.IsNullOrWhiteSpace(request.JobTitle) || string.IsNullOrWhiteSpace(request.ContractType))) return BadRequest(new { message = "Employed workers require a start date, job title, and contract type." });
        if (request.EmploymentStatus is "Suspended" or "Leaver" && string.IsNullOrWhiteSpace(request.StatusReason)) return BadRequest(new { message = "A status reason is required." });
        if (request.EmploymentStatus == "Leaver" && request.EndDate is null) return BadRequest(new { message = "A leaver date is required." });
        await Exec("""insert into worker_employment_profiles(care_worker_id,organization_id,branch_id,recruitment_status,employment_status,contract_type,job_title,contracted_weekly_minutes,start_date,end_date,line_manager,status_reason,approved_by,approved_at,updated_at) values(@worker,@organization,@branch,@recruitment,@status,@contract,@job,@minutes,@start,@end,@manager,@reason,@actor,now(),now()) on conflict(care_worker_id) do update set recruitment_status=@recruitment,employment_status=@status,contract_type=@contract,job_title=@job,contracted_weekly_minutes=@minutes,start_date=@start,end_date=@end,line_manager=@manager,status_reason=@reason,approved_by=@actor,approved_at=now(),updated_at=now()""", workerId, c => { Add(c,"recruitment",request.RecruitmentStatus); Add(c,"status",request.EmploymentStatus); Add(c,"contract",request.ContractType); Add(c,"job",request.JobTitle); Add(c,"minutes",request.ContractedWeeklyMinutes); Add(c,"start",request.StartDate); Add(c,"end",request.EndDate); Add(c,"manager",request.LineManager); Add(c,"reason",request.StatusReason); }, token);
        await Event(workerId,"employment.updated","WorkerEmployment",workerId,$"{request.RecruitmentStatus} / {request.EmploymentStatus}",token);
        return Ok(new { message = "Employment lifecycle updated." });
    }

    [HttpPost("supervisions")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public async Task<IActionResult> Schedule(Guid workerId, SupervisionRequest request, CancellationToken token)
    {
        if (!await WorkerExists(workerId, token)) return NotFound();
        if (request.RecordType is not ("Supervision" or "Appraisal") || request.ScheduledAt == default) return BadRequest(new { message = "A valid supervision or appraisal schedule is required." });
        var id=Guid.NewGuid(); await Exec("insert into worker_supervision_records(id,care_worker_id,organization_id,branch_id,record_type,scheduled_at,supervisor,status) values(@id,@worker,@organization,@branch,@type,@scheduled,@supervisor,'Scheduled')",workerId,c=>{Add(c,"id",id);Add(c,"type",request.RecordType);Add(c,"scheduled",request.ScheduledAt);Add(c,"supervisor",request.Supervisor);},token);
        await Event(workerId,"supervision.scheduled","WorkerSupervision",id,request.RecordType,token); return Created($"/api/phase1/workforce-lifecycle/care-workers/{workerId}/supervisions/{id}",new{id});
    }

    [HttpPost("supervisions/{id:guid}/complete")]
    [Authorize(Roles = "CareManager,Administrator")]
    public async Task<IActionResult> CompleteSupervision(Guid workerId,Guid id,CompleteSupervisionRequest request,CancellationToken token)
    {
        if (!await WorkerExists(workerId,token)) return NotFound();
        if (string.IsNullOrWhiteSpace(request.Outcome)||string.IsNullOrWhiteSpace(request.EvidenceReference)) return BadRequest(new{message="Outcome and completion evidence are required."});
        var changed=await Exec("update worker_supervision_records set status='Completed',completed_at=now(),discussion=@discussion,outcome=@outcome,actions=@actions,evidence_reference=@evidence,review_due=@due where id=@id and care_worker_id=@worker and organization_id=@organization and branch_id=@branch and status='Scheduled'",workerId,c=>{Add(c,"id",id);Add(c,"discussion",request.Discussion);Add(c,"outcome",request.Outcome);Add(c,"actions",request.Actions);Add(c,"evidence",request.EvidenceReference);Add(c,"due",request.ReviewDue);},token);
        if(changed==0)return Conflict(new{message="Only a scheduled record can be completed."}); await Event(workerId,"supervision.completed","WorkerSupervision",id,request.Outcome,token);return Ok(new{message="Supervision record completed."});
    }

    [HttpPut("absences/{absenceId:guid}/cover")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public async Task<IActionResult> Cover(Guid workerId,Guid absenceId,CoverRequest request,CancellationToken token)
    {
        if(request.CoverStatus is not ("Unassigned" or "Requested" or "Covered" or "NotRequired"))return BadRequest(new{message="Cover status is invalid."});
        if(request.CoverStatus=="Covered"&&request.CoveredBy is null)return BadRequest(new{message="A covering worker is required."});
        if(request.CoveredBy==workerId)return BadRequest(new{message="The absent worker cannot cover their own absence."});
        if(request.CoveredBy is not null&&!await WorkerExists(request.CoveredBy.Value,token))return BadRequest(new{message="Covering worker is outside the accessible branch."});
        var changed=await Exec("update worker_absences set cover_status=@status,covered_by=@covered,cover_notes=@notes where id=@id and care_worker_id=@worker and organization_id=@organization and branch_id=@branch",workerId,c=>{Add(c,"id",absenceId);Add(c,"status",request.CoverStatus);Add(c,"covered",request.CoveredBy);Add(c,"notes",request.Notes);},token);
        if(changed==0)return NotFound();await Event(workerId,"absence.cover_updated","WorkerAbsence",absenceId,request.CoverStatus,token);return Ok(new{message="Absence cover updated."});
    }

    [HttpPost("absences/{absenceId:guid}/return-to-work")]
    [Authorize(Roles = "CareManager,Administrator")]
    public async Task<IActionResult> ReturnToWork(Guid workerId,Guid absenceId,ReturnToWorkRequest request,CancellationToken token)
    {
        if(!await WorkerExists(workerId,token))return NotFound();
        if(request.MeetingAt>DateTimeOffset.UtcNow.AddMinutes(5)||string.IsNullOrWhiteSpace(request.EvidenceReference))return BadRequest(new{message="A completed meeting and evidence reference are required."});
        await using var check=await Command("select absence_type,ends_at from worker_absences where id=@id and care_worker_id=@worker and organization_id=@organization and branch_id=@branch",workerId,token);Add(check,"id",absenceId);await using var reader=await check.ExecuteReaderAsync(token);if(!await reader.ReadAsync(token))return NotFound();var type=reader.GetString(0);var ends=reader.GetFieldValue<DateTimeOffset>(1);await reader.DisposeAsync();
        if(type!="Sickness")return BadRequest(new{message="Return-to-work reviews apply only to sickness absence."});if(ends>DateTimeOffset.UtcNow)return Conflict(new{message="The sickness absence must have ended before review completion."});if(!request.FitToReturn&&string.IsNullOrWhiteSpace(request.Restrictions))return BadRequest(new{message="Restrictions are required when the worker is not fit for unrestricted return."});
        var id=Guid.NewGuid();try{await Exec("insert into worker_return_to_work_reviews(id,absence_id,care_worker_id,organization_id,branch_id,meeting_at,fit_to_return,adjustments,restrictions,evidence_reference,review_due,manager,status,completed_at) values(@id,@absence,@worker,@organization,@branch,@meeting,@fit,@adjustments,@restrictions,@evidence,@due,@actor,'Completed',now())",workerId,c=>{Add(c,"id",id);Add(c,"absence",absenceId);Add(c,"meeting",request.MeetingAt);Add(c,"fit",request.FitToReturn);Add(c,"adjustments",request.Adjustments);Add(c,"restrictions",request.Restrictions);Add(c,"evidence",request.EvidenceReference);Add(c,"due",request.ReviewDue);},token);}catch(DbException){return Conflict(new{message="A return-to-work review already exists for this absence."});}
        await Event(workerId,"return_to_work.completed","ReturnToWorkReview",id,request.FitToReturn?"Fit to return":"Restrictions recorded",token);return Created($"/api/phase1/workforce-lifecycle/care-workers/{workerId}/absences/{absenceId}/return-to-work/{id}",new{id});
    }

    private bool CanRead(Guid workerId)=>user.IsBackOffice||user.IsAdministrator||user.IsCareManager||user.IsCareCoordinator||(user.IsCareWorker&&user.CareWorkerId==workerId);
    private async Task<bool> WorkerExists(Guid id,CancellationToken t)=>await db.CareWorkers.AnyAsync(x=>x.Id==id&&x.OrganizationId==tenant.OrganizationId&&(tenant.IsOrganizationWide||x.BranchId==tenant.BranchId),t);
    private async Task Event(Guid worker,string type,string entity,Guid? entityId,string detail,CancellationToken t){var id=Guid.NewGuid();await Exec("insert into workforce_lifecycle_events(id,care_worker_id,organization_id,branch_id,event_type,entity_type,entity_id,actor,detail) values(@id,@worker,@organization,@branch,@type,@entity,@entityId,@actor,@detail)",worker,c=>{Add(c,"id",id);Add(c,"type",type);Add(c,"entity",entity);Add(c,"entityId",entityId);Add(c,"detail",detail);},t);db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),type,user.UserName,entity,entityId,DateTimeOffset.UtcNow,tenant.OrganizationId,tenant.BranchId??TenantDefaults.BranchId));await db.SaveChangesAsync(t);}
    private async Task<Dictionary<string,object?>> One(string sql,Guid worker,CancellationToken t){var rows=await Many(sql,worker,t);return rows.FirstOrDefault()??[];}
    private async Task<List<Dictionary<string,object?>>> Many(string sql,Guid worker,CancellationToken t){var result=new List<Dictionary<string,object?>>();await using var c=await Command(sql,worker,t);await using var r=await c.ExecuteReaderAsync(t);while(await r.ReadAsync(t)){var row=new Dictionary<string,object?>();for(var i=0;i<r.FieldCount;i++)row[ToCamel(r.GetName(i))]=r.IsDBNull(i)?null:r.GetValue(i);result.Add(row);}return result;}
    private static object[] BuildAlerts(List<Dictionary<string,object?>> supervisions,List<Dictionary<string,object?>> absences){var today=DateTime.UtcNow.Date;return supervisions.Where(x=>x["status"]?.ToString()=="Completed"&&x["reviewDue"] is DateTime due&&due.Date<=today.AddDays(30)).Select(x=>(object)new{type="ReviewDue",message=$"{x["recordType"]} review is due {((DateTime)x["reviewDue"]!).ToShortDateString()}"}).Concat(absences.Where(x=>x["absenceType"]?.ToString()=="Sickness"&&x["endsAt"] is DateTimeOffset end&&end<DateTimeOffset.UtcNow&&x["returnToWorkId"] is null).Select(x=>(object)new{type="ReturnToWorkDue",message="Completed sickness absence requires a return-to-work review."})).ToArray();}
    private async Task<int> Exec(string sql,Guid worker,Action<DbCommand> bind,CancellationToken t){await using var c=await Command(sql,worker,t);bind(c);return await c.ExecuteNonQueryAsync(t);}
    private async Task<DbCommand> Command(string sql,Guid worker,CancellationToken t){var connection=db.Database.GetDbConnection();if(connection.State!=ConnectionState.Open)await connection.OpenAsync(t);var c=connection.CreateCommand();c.CommandText=sql;Add(c,"worker",worker);Add(c,"organization",tenant.OrganizationId);Add(c,"branch",tenant.BranchId??TenantDefaults.BranchId);Add(c,"actor",user.UserName);return c;}
    private static void Add(DbCommand c,string name,object? value){if(c.Parameters.Contains(name))return;var p=c.CreateParameter();p.ParameterName=name;p.Value=value??DBNull.Value;c.Parameters.Add(p);}private static string ToCamel(string value){var parts=value.Split('_');return parts[0]+string.Concat(parts.Skip(1).Select(x=>char.ToUpperInvariant(x[0])+x[1..]));}
}

public sealed record EmploymentRequest(string RecruitmentStatus,string EmploymentStatus,string ContractType,string JobTitle,int ContractedWeeklyMinutes,DateOnly? StartDate,DateOnly? EndDate,string LineManager,string StatusReason);
public sealed record SupervisionRequest(string RecordType,DateTimeOffset ScheduledAt,string Supervisor);
public sealed record CompleteSupervisionRequest(string Discussion,string Outcome,string Actions,string EvidenceReference,DateOnly? ReviewDue);
public sealed record CoverRequest(string CoverStatus,Guid? CoveredBy,string Notes);
public sealed record ReturnToWorkRequest(DateTimeOffset MeetingAt,bool FitToReturn,string Adjustments,string Restrictions,string EvidenceReference,DateOnly? ReviewDue);
