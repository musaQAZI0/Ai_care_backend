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
[Route("api/phase1/care-operations")]
public sealed class CareOperationsController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user) : ControllerBase
{
    [HttpGet("roster-availability")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator,BackOffice")]
    public async Task<IActionResult> RosterAvailability([FromQuery] DateOnly day, CancellationToken token)
    {
        if (day == default) return BadRequest(new { message = "Select a roster day." });
        var start = AppointmentWorkflow.UkStart(day);
        var end = AppointmentWorkflow.UkStart(day.AddDays(1));
        var rules = await Rows("""
            select care_worker_id,start_time,end_time,is_available from worker_availability_rules
            where organization_id=@organization and (@organizationWide or branch_id=@branch)
              and day_of_week=@weekday and effective_from<=@day and (effective_to is null or effective_to>=@day)
            order by care_worker_id,start_time
            """, command => { Add(command,"weekday",(int)day.DayOfWeek); Add(command,"day",day); }, token);
        var absences = await Rows("""
            select care_worker_id,starts_at,ends_at,status from worker_absences
            where organization_id=@organization and (@organizationWide or branch_id=@branch)
              and starts_at<@end and ends_at>@start and status='Approved'
            order by care_worker_id,starts_at
            """, command => { Add(command,"start",start); Add(command,"end",end); }, token);
        var employment = await Rows("""
            select care_worker_id,job_title from worker_employment_profiles
            where organization_id=@organization and (@organizationWide or branch_id=@branch)
            order by care_worker_id
            """, _ => { }, token);
        return Ok(new { rules, absences, employment });
    }

    [HttpGet("diary-participants")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator,BackOffice")]
    public async Task<IActionResult> DiaryParticipants(CancellationToken token) => Ok(await db.AppUsers.AsNoTracking()
        .Where(x => x.IsActive && x.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || x.BranchId == tenant.BranchId)
            && (x.Role == UserRole.CareCoordinator || x.Role == UserRole.CareManager || x.Role == UserRole.Administrator || x.Role == UserRole.BackOffice))
        .OrderBy(x => x.UserName).Select(x => new { x.Id, Name = x.UserName, Role = x.Role.ToString() }).ToListAsync(token));

    [HttpGet("diary")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator,BackOffice")]
    public async Task<IActionResult> Diary([FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] string? category, [FromQuery] string? status, [FromQuery] string? priority, [FromQuery] Guid? serviceUserId, [FromQuery] Guid? careWorkerId, [FromQuery] string? createdBy, [FromQuery] string? sortBy, CancellationToken token)
    {
        var start = from ?? DateTimeOffset.UtcNow.AddDays(-30); var end = to ?? DateTimeOffset.UtcNow.AddDays(30);
        if (end <= start || end - start > TimeSpan.FromDays(366)) return BadRequest(new { message = "Choose a valid diary period of no more than one year." });
        return Ok(await Rows("""
            select d.id,d.category,d.priority,d.status,d.subject,d.detail,d.event_at,d.service_user_id,d.care_worker_id,d.owner_id,
                   coalesce(s."FullName",'') as service_user_name,coalesce(w."FullName",'') as care_worker_name,
                   coalesce(owner_user."UserName",'') as owner_name,
                   coalesce((select array_agg(recipient."UserName"::text) from operational_diary_recipients dr
                     join "AppUsers" recipient on recipient."Id"=dr.recipient_user_id and recipient."OrganizationId"=d.organization_id
                     where dr.diary_entry_id=d.id and dr.organization_id=d.organization_id),array[]::text[]) as recipient_names,
                   d.created_by,d.created_at,d.updated_by,d.updated_at
            from operational_diary_entries d
            left join "ServiceUsers" s on s."Id"=d.service_user_id and s."OrganizationId"=d.organization_id
            left join "CareWorkers" w on w."Id"=d.care_worker_id and w."OrganizationId"=d.organization_id
            left join "AppUsers" owner_user on owner_user."Id"=d.owner_id and owner_user."OrganizationId"=d.organization_id
            where d.organization_id=@organization and (@organizationWide or d.branch_id=@branch)
              and d.event_at>=@from and d.event_at<=@to and (@category='' or d.category=@category) and (@status='' or d.status=@status)
              and (@priority='' or d.priority=@priority) and (@person is null or d.service_user_id=@person)
              and (@worker is null or d.care_worker_id=@worker) and (@creator='' or d.created_by=@creator)
              and (@manager or not exists(select 1 from care_operation_options o where o.organization_id=d.organization_id and o.kind='DiaryCategory' and o.value=d.category and o.visibility='ManagersOnly'))
            order by case when @sort='event' then d.event_at end desc,
                     case when @sort='priority' then case d.priority when 'Critical' then 4 when 'High' then 3 when 'Medium' then 2 else 1 end end desc,
                     coalesce(d.updated_at,d.created_at) desc
            """, command => { Add(command,"from",start); Add(command,"to",end); Add(command,"category",category?.Trim()??""); Add(command,"status",status?.Trim()??""); Add(command,"priority",priority?.Trim()??""); Add(command,"person",serviceUserId); Add(command,"worker",careWorkerId); Add(command,"creator",createdBy?.Trim()??""); Add(command,"sort",sortBy is "event" or "priority" ? sortBy : "modified"); Add(command,"manager",User.IsInRole("Administrator")||User.IsInRole("CareManager")); }, token));
    }

    [HttpPost("diary")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public async Task<IActionResult> AddDiary(DiaryEntryRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.Category) || string.IsNullOrWhiteSpace(request.Subject) || string.IsNullOrWhiteSpace(request.Detail)) return BadRequest(new { message = "Category, subject and detail are required." });
        if (!await DiaryOptionAllowed(request.Category,token)) return BadRequest(new { message = "Select an active diary category available to your role." });
        if (request.Priority is not ("Routine" or "Medium" or "High" or "Critical")) return BadRequest(new { message = "Select a valid priority." });
        if (request.ServiceUserId is Guid person && !await db.ServiceUsers.AnyAsync(x => x.Id == person && x.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || x.BranchId == tenant.BranchId), token)) return BadRequest(new { message = "The selected client is not available in this branch." });
        if (request.CareWorkerId is Guid worker && !await db.CareWorkers.AnyAsync(x => x.Id == worker && x.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || x.BranchId == tenant.BranchId), token)) return BadRequest(new { message = "The selected worker is not available in this branch." });
        if (request.DueAt is not null && request.DueAt < request.EventAt) return BadRequest(new { message = "The diary due date must follow the event date." });
        var participantIds = (request.RecipientIds ?? []).Append(request.OwnerId ?? Guid.Empty).Where(id => id != Guid.Empty).Distinct().ToArray();
        if (participantIds.Length > 20) return BadRequest(new { message = "Select no more than twenty diary participants." });
        var visibleParticipants = await db.AppUsers.CountAsync(x => participantIds.Contains(x.Id) && x.IsActive && x.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || x.BranchId == tenant.BranchId)
            && (x.Role == UserRole.CareCoordinator || x.Role == UserRole.CareManager || x.Role == UserRole.Administrator || x.Role == UserRole.BackOffice), token);
        if (visibleParticipants != participantIds.Length) return BadRequest(new { message = "One or more diary participants are unavailable in this branch." });
        var id = Guid.NewGuid();
        await Exec("insert into operational_diary_entries(id,organization_id,branch_id,service_user_id,care_worker_id,category,priority,status,subject,detail,event_at,due_at,owner_id,created_by) values(@id,@organization,@branch,@person,@worker,@category,@priority,'Open',@subject,@detail,@event,@due,@owner,@actor)", command => { Add(command,"id",id); Add(command,"person",request.ServiceUserId); Add(command,"worker",request.CareWorkerId); Add(command,"category",request.Category.Trim()); Add(command,"priority",request.Priority); Add(command,"subject",request.Subject.Trim()); Add(command,"detail",request.Detail.Trim()); Add(command,"event",request.EventAt); Add(command,"due",request.DueAt); Add(command,"owner",request.OwnerId); }, token);
        foreach (var recipientId in (request.RecipientIds ?? []).Distinct())
            await Exec("insert into operational_diary_recipients(diary_entry_id,recipient_user_id,organization_id) values(@id,@recipient,@organization)", command => { Add(command,"id",id); Add(command,"recipient",recipientId); }, token);
        await Exec("insert into operational_diary_history(id,diary_entry_id,organization_id,action,to_status,reason,actor) values(@history,@id,@organization,'Created','Open','',@actor)", command => { Add(command,"history",Guid.NewGuid()); Add(command,"id",id); }, token);
        await Audit("operations.diary_created", "OperationalDiaryEntry", id, token);
        return Created($"/api/phase1/care-operations/diary/{id}", new { id });
    }

    [HttpPost("diary/{id:guid}/transition")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public async Task<IActionResult> TransitionDiary(Guid id, DiaryTransitionRequest request, CancellationToken token)
    {
        if (request.Status is not ("Open" or "InProgress" or "Resolved" or "Cancelled") || string.IsNullOrWhiteSpace(request.Reason)) return BadRequest(new { message = "A valid status and reason are required." });
        var current = await Rows("select status from operational_diary_entries d where id=@id and organization_id=@organization and (@organizationWide or branch_id=@branch) and (@manager or not exists(select 1 from care_operation_options o where o.organization_id=d.organization_id and o.kind='DiaryCategory' and o.value=d.category and o.visibility='ManagersOnly'))", command => { Add(command,"id",id); Add(command,"manager",User.IsInRole("Administrator")||User.IsInRole("CareManager")); }, token);
        if (current.Count == 0) return NotFound();
        var changed = await Exec("update operational_diary_entries d set status=@status,updated_by=@actor,updated_at=now() where id=@id and organization_id=@organization and (@organizationWide or branch_id=@branch) and (@manager or not exists(select 1 from care_operation_options o where o.organization_id=d.organization_id and o.kind='DiaryCategory' and o.value=d.category and o.visibility='ManagersOnly'))", command => { Add(command,"id",id); Add(command,"status",request.Status); Add(command,"manager",User.IsInRole("Administrator")||User.IsInRole("CareManager")); }, token);
        if (changed == 0) return NotFound();
        await Exec("insert into operational_diary_history(id,diary_entry_id,organization_id,action,from_status,to_status,reason,actor) values(@history,@id,@organization,'Status changed',@previous,@status,@reason,@actor)", command => { Add(command,"history",Guid.NewGuid()); Add(command,"id",id); Add(command,"previous",current[0]["status"]); Add(command,"status",request.Status); Add(command,"reason",request.Reason.Trim()); }, token);
        await Audit("operations.diary_transitioned", "OperationalDiaryEntry", id, token); return NoContent();
    }

    [HttpGet("training-calendar")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator,BackOffice")]
    public async Task<IActionResult> TrainingCalendar([FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken token)
    {
        var start=from??DateTimeOffset.UtcNow.AddMonths(-1);var end=to??DateTimeOffset.UtcNow.AddMonths(6);
        if(end<=start||end-start>TimeSpan.FromDays(1095))return BadRequest(new{message="Choose a valid training period of no more than three years."});
        return Ok(await Rows("""
            select b.id,b.course_id,c.code,c.title,c.category,c.provider,b.care_worker_id,w."FullName" as care_worker_name,b.scheduled_at,b.status,b.confirmation_status,b.completed_at,b.expires_at,c.is_mandatory,b.booked_by,b.created_at,b.updated_by,b.updated_at
            from worker_training_bookings b join training_catalogue_courses c on c.id=b.course_id join "CareWorkers" w on w."Id"=b.care_worker_id
            where b.organization_id=@organization and (@organizationWide or b.branch_id=@branch) and b.scheduled_at>=@from and b.scheduled_at<=@to
            order by b.scheduled_at,c.title
            """, command => { Add(command,"from",start);Add(command,"to",end); }, token));
    }

    [HttpGet("monitoring")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator,BackOffice")]
    public async Task<IActionResult> Monitoring([FromQuery] DateTimeOffset? from,[FromQuery] DateTimeOffset? to,[FromQuery] Guid? careWorkerId,[FromQuery] Guid? serviceUserId,[FromQuery] string? status,CancellationToken token)
    {
        var start=from??DateTimeOffset.UtcNow.Date;var end=to??start.AddDays(7);
        if(end<=start||end-start>TimeSpan.FromDays(62))return BadRequest(new{message="Choose a monitoring period of no more than 62 days."});
        return Ok(await Rows("""
            select v."Id",v."StartsAt",v."DurationMinutes",v."CheckedInAt",v."CheckedOutAt",v."Status",v."VisitType",
                   v."ServiceUserId",s."FullName" as service_user_name,v."CareWorkerId",w."FullName" as care_worker_name,
                   extract(epoch from (v."CheckedInAt"-v."StartsAt"))/60.0 as arrival_variance_minutes,
                   case when v."CheckedInAt" is null or v."CheckedOutAt" is null then null else extract(epoch from (v."CheckedOutAt"-v."CheckedInAt"))/60.0 end as actual_duration_minutes,
                   coalesce(o.care_provided,''),coalesce(o.duration_reason,''),coalesce(o.exception_reason,'')
            from "Visits" v join "ServiceUsers" s on s."Id"=v."ServiceUserId" left join "CareWorkers" w on w."Id"=v."CareWorkerId"
            left join visit_monitoring_outcomes o on o.visit_id=v."Id" and o.organization_id=v."OrganizationId"
            where v."OrganizationId"=@organization and (@organizationWide or v."BranchId"=@branch)
              and v."StartsAt">=@from and v."StartsAt"<=@to and (@worker is null or v."CareWorkerId"=@worker)
              and (@person is null or v."ServiceUserId"=@person) and (@status='' or v."Status"::text=@status)
            order by v."StartsAt"
            """, command => {Add(command,"from",start);Add(command,"to",end);Add(command,"worker",careWorkerId);Add(command,"person",serviceUserId);Add(command,"status",status??"");}, token));
    }

    [HttpPut("monitoring/{visitId:guid}/outcome")]
    [Authorize(Roles = "CareWorker,CareCoordinator,CareManager,Administrator")]
    public async Task<IActionResult> MonitoringOutcome(Guid visitId, VisitMonitoringOutcomeRequest request, CancellationToken token)
    {
        var visit=await db.Visits.SingleOrDefaultAsync(x=>x.Id==visitId&&x.OrganizationId==tenant.OrganizationId&&(tenant.IsOrganizationWide||x.BranchId==tenant.BranchId),token);
        if(visit is null)return NotFound();if(user.IsCareWorker&&user.CareWorkerId!=visit.CareWorkerId)return Forbid();
        if(string.IsNullOrWhiteSpace(request.CareProvided))return BadRequest(new{message="Care provided is required."});
        if(!await OptionActive("CareProvided",request.CareProvided,token))return BadRequest(new{message="Select an active care provided option."});
        if(!string.IsNullOrWhiteSpace(request.DurationReason)&&!await OptionActive("DurationReason",request.DurationReason,token))return BadRequest(new{message="Select an active duration reason."});
        if(visit.CheckedInAt is not null&&visit.CheckedOutAt is not null&&Math.Abs((visit.CheckedOutAt.Value-visit.CheckedInAt.Value).TotalMinutes-visit.DurationMinutes)>=10&&string.IsNullOrWhiteSpace(request.DurationReason))return BadRequest(new{message="A duration reason is required when actual delivery differs from planned time by ten minutes or more."});
        await Exec("""insert into visit_monitoring_outcomes(visit_id,organization_id,branch_id,care_provided,duration_reason,exception_reason,recorded_by) values(@visit,@organization,@branch,@care,@duration,@exception,@actor) on conflict(visit_id) do update set care_provided=@care,duration_reason=@duration,exception_reason=@exception,recorded_by=@actor,updated_at=now()""",command=>{Add(command,"visit",visitId);Add(command,"care",request.CareProvided.Trim());Add(command,"duration",request.DurationReason?.Trim()??"");Add(command,"exception",request.ExceptionReason?.Trim()??"");},token);
        await Audit("visit.monitoring_outcome_recorded","Visit",visitId,token);return NoContent();
    }

    [HttpPut("location-consent/{subjectType}/{subjectId:guid}")]
    [Authorize(Roles = "CareManager,Administrator")]
    public async Task<IActionResult> LocationConsent(string subjectType,Guid subjectId,LocationConsentRequest request,CancellationToken token)
    {
        if(subjectType is not("CareWorker" or "ServiceUser")||request.SharingMode is not("Exact" or "Approximate" or "Hidden")||string.IsNullOrWhiteSpace(request.Purpose)||request.EndsAt is null||request.EndsAt<=request.StartsAt)return BadRequest(new{message="Subject, sharing mode, purpose and valid consent period are required."});
        var subjectBranch=subjectType=="CareWorker"
            ?await db.CareWorkers.Where(x=>x.Id==subjectId&&x.OrganizationId==tenant.OrganizationId).Select(x=>(Guid?)x.BranchId).SingleOrDefaultAsync(token)
            :await db.ServiceUsers.Where(x=>x.Id==subjectId&&x.OrganizationId==tenant.OrganizationId).Select(x=>(Guid?)x.BranchId).SingleOrDefaultAsync(token);
        if(subjectBranch is null||(!tenant.IsOrganizationWide&&subjectBranch!=tenant.BranchId))return NotFound();
        await Exec("update location_sharing_consents set active=false where organization_id=@organization and subject_type=@type and subject_id=@subject and active=true",c=>{Add(c,"type",subjectType);Add(c,"subject",subjectId);},token);
        var id=Guid.NewGuid();await Exec("insert into location_sharing_consents(id,organization_id,branch_id,subject_type,subject_id,sharing_mode,purpose,starts_at,ends_at,active,approved_by) values(@id,@organization,@subjectBranch,@type,@subject,@mode,@purpose,@starts,@ends,true,@actor)",c=>{Add(c,"id",id);Add(c,"subjectBranch",subjectBranch);Add(c,"type",subjectType);Add(c,"subject",subjectId);Add(c,"mode",request.SharingMode);Add(c,"purpose",request.Purpose.Trim());Add(c,"starts",request.StartsAt);Add(c,"ends",request.EndsAt);},token);await Audit("location.consent_updated","LocationSharingConsent",id,token);return Ok(new{id});
    }

    [HttpGet("live-map")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public async Task<IActionResult> LiveMap(CancellationToken token) => Ok(await Rows("""
        with latest as (select distinct on (l.care_worker_id) l.care_worker_id,l.service_user_id,l.visit_id,l.latitude,l.longitude,l.accuracy_meters,l.captured_at from visit_location_events l where l.organization_id=@organization and (@organizationWide or l.branch_id=@branch) and l.event_type='LiveUpdate' and l.captured_at>=now()-interval '15 minutes' order by l.care_worker_id,l.captured_at desc)
        select x.care_worker_id,w."FullName" as care_worker_name,x.service_user_id,s."FullName" as service_user_name,x.visit_id,
          case when c.sharing_mode='Exact' then x.latitude else round(x.latitude,2) end as latitude,
          case when c.sharing_mode='Exact' then x.longitude else round(x.longitude,2) end as longitude,
          x.accuracy_meters,x.captured_at,c.sharing_mode,c.purpose
        from latest x join "CareWorkers" w on w."Id"=x.care_worker_id join "ServiceUsers" s on s."Id"=x.service_user_id
        join "Visits" v on v."Id"=x.visit_id and v."OrganizationId"=@organization and v."Status" in ('Scheduled','InProgress')
        join worker_live_sharing sh on sh.organization_id=@organization and sh.care_worker_id=x.care_worker_id and sh.visit_id=x.visit_id and sh.stopped_at is null and sh.started_at<=x.captured_at and sh.ends_at>now()
        join location_sharing_consents c on c.organization_id=@organization and c.branch_id=sh.branch_id and c.subject_type='CareWorker' and c.subject_id=x.care_worker_id and c.active=true and c.sharing_mode<>'Hidden' and c.starts_at<=now() and c.ends_at>now()
        order by x.captured_at desc
        """,_=>{},token));

    private async Task<bool> OptionActive(string kind,string value,CancellationToken token){var rows=await Rows("select id from care_operation_options where organization_id=@organization and kind=@kind and value=@value and active=true limit 1",command=>{Add(command,"kind",kind);Add(command,"value",value.Trim());},token);return rows.Count>0;}
    private async Task<bool> DiaryOptionAllowed(string value,CancellationToken token){var rows=await Rows("select id from care_operation_options where organization_id=@organization and kind='DiaryCategory' and value=@value and active=true and (visibility='AllStaff' or @manager) limit 1",command=>{Add(command,"value",value.Trim());Add(command,"manager",User.IsInRole("Administrator")||User.IsInRole("CareManager"));},token);return rows.Count>0;}
    private async Task<List<Dictionary<string,object?>>> Rows(string sql,Action<DbCommand> bind,CancellationToken token){var list=new List<Dictionary<string,object?>>();await using var command=await Command(sql,token);bind(command);await using var reader=await command.ExecuteReaderAsync(token);while(await reader.ReadAsync(token)){var row=new Dictionary<string,object?>();for(var i=0;i<reader.FieldCount;i++)row[Camel(reader.GetName(i))]=reader.IsDBNull(i)?null:reader.GetValue(i);list.Add(row);}return list;}
    private async Task<int> Exec(string sql,Action<DbCommand> bind,CancellationToken token){await using var command=await Command(sql,token);bind(command);return await command.ExecuteNonQueryAsync(token);}
    private async Task<DbCommand> Command(string sql,CancellationToken token){var connection=db.Database.GetDbConnection();if(connection.State!=ConnectionState.Open)await connection.OpenAsync(token);var command=connection.CreateCommand();command.CommandText=sql;Add(command,"organization",tenant.OrganizationId);Add(command,"branch",tenant.BranchId??TenantDefaults.BranchId);Add(command,"organizationWide",tenant.IsOrganizationWide);Add(command,"actor",user.UserName);return command;}
    private async Task Audit(string action,string entity,Guid id,CancellationToken token){db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),action,user.UserName,entity,id,DateTimeOffset.UtcNow,tenant.OrganizationId,tenant.BranchId??TenantDefaults.BranchId));await db.SaveChangesAsync(token);}
    private static void Add(DbCommand command,string name,object? value){if(command.Parameters.Contains(name))return;var parameter=command.CreateParameter();parameter.ParameterName=name;parameter.Value=value??DBNull.Value;command.Parameters.Add(parameter);}
    private static string Camel(string value){var parts=value.Split('_');return parts[0]+string.Concat(parts.Skip(1).Select(x=>char.ToUpperInvariant(x[0])+x[1..]));}
}

public sealed record DiaryEntryRequest(string Category,string Priority,string Subject,string Detail,DateTimeOffset EventAt,Guid? ServiceUserId,Guid? CareWorkerId,DateTimeOffset? DueAt = null,Guid? OwnerId = null,Guid[]? RecipientIds = null);
public sealed record DiaryTransitionRequest(string Status,string Reason);
public sealed record VisitMonitoringOutcomeRequest(string CareProvided,string? DurationReason,string? ExceptionReason);
public sealed record LocationConsentRequest(string SharingMode,string Purpose,DateTimeOffset StartsAt,DateTimeOffset? EndsAt);
