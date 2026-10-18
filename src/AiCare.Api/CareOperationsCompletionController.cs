using System.Data;
using System.Data.Common;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[ApiController, Authorize(Policy="Phase1User")]
[Route("api/phase1/care-operations")]
public sealed class CareOperationsCompletionController(CareDbContext db,ITenantContext tenant,ICurrentUserContext user):ControllerBase
{
 [HttpGet("map-points"),Authorize(Roles="CareCoordinator,CareManager,Administrator")]
 public Task<List<Dictionary<string,object?>>> MapPoints([FromQuery]string? subjectType,CancellationToken t)=>Rows("""select subject_type,subject_id,case when subject_type='CareWorker' then coalesce(w."FullName",'') else coalesce(s."FullName",'') end name,address_text,latitude,longitude,postcode,provider source from subject_geocoded_locations g left join "CareWorkers" w on g.subject_type='CareWorker' and w."Id"=g.subject_id and w."OrganizationId"=g.organization_id left join "ServiceUsers" s on g.subject_type='ServiceUser' and s."Id"=g.subject_id and s."OrganizationId"=g.organization_id where g.organization_id=@organization and (@wide or g.branch_id=@branch) and (@type='' or g.subject_type=@type) order by name""",c=>Add(c,"type",subjectType??""),t);

 [HttpPut("map-points/{subjectType}/{subjectId:guid}"),Authorize(Roles="CareManager,Administrator")]
 public async Task<IActionResult> SaveMapPoint(string subjectType,Guid subjectId,MapPointRequest request,CancellationToken t)
 {
  if(subjectType is not("CareWorker" or "ServiceUser")||request.Latitude is <-90 or >90||request.Longitude is <-180 or >180||string.IsNullOrWhiteSpace(request.Address)||string.IsNullOrWhiteSpace(request.Provider))return BadRequest(new{message="A valid subject, address, provider and coordinates are required."});
  var subjectBranch=subjectType=="CareWorker"
   ?await db.CareWorkers.Where(x=>x.Id==subjectId&&x.OrganizationId==tenant.OrganizationId).Select(x=>(Guid?)x.BranchId).SingleOrDefaultAsync(t)
   :await db.ServiceUsers.Where(x=>x.Id==subjectId&&x.OrganizationId==tenant.OrganizationId).Select(x=>(Guid?)x.BranchId).SingleOrDefaultAsync(t);
  if(subjectBranch is null||(!tenant.IsOrganizationWide&&subjectBranch!=tenant.BranchId))return NotFound();
  await Exec("""insert into subject_geocoded_locations(id,organization_id,branch_id,subject_type,subject_id,address_text,postcode,latitude,longitude,provider,confidence,geocoded_at,verified_by,verified_at) values(@id,@organization,@subjectBranch,@type,@subject,@address,@postcode,@lat,@lng,@provider,@confidence,now(),@actor,now()) on conflict(organization_id,subject_type,subject_id) do update set branch_id=@subjectBranch,address_text=@address,postcode=@postcode,latitude=@lat,longitude=@lng,provider=@provider,confidence=@confidence,geocoded_at=now(),verified_by=@actor,verified_at=now()""",c=>{Add(c,"id",Guid.NewGuid());Add(c,"subjectBranch",subjectBranch);Add(c,"type",subjectType);Add(c,"subject",subjectId);Add(c,"address",request.Address.Trim());Add(c,"postcode",request.Postcode?.Trim()??"");Add(c,"lat",request.Latitude);Add(c,"lng",request.Longitude);Add(c,"provider",request.Provider.Trim());Add(c,"confidence",request.Confidence);},t);
  await Audit("location.geocode_verified","GeocodedLocation",subjectId,t);return NoContent();
 }

 [HttpGet("live-sharing"),Authorize(Roles="CareWorker")]
 public async Task<IActionResult> LiveSharing(CancellationToken t)
 {
  if(user.CareWorkerId is not Guid worker)return Forbid();
  var rows=await Rows("select visit_id,ends_at from worker_live_sharing where organization_id=@organization and branch_id=@branch and care_worker_id=@worker and stopped_at is null and ends_at>now()",c=>Add(c,"worker",worker),t);
  return Ok(rows.FirstOrDefault());
 }

 [HttpPost("live-sharing/{visitId:guid}/start"),Authorize(Roles="CareWorker")]
 public async Task<IActionResult> StartLiveSharing(Guid visitId,CancellationToken t)
 {
  if(user.CareWorkerId is not Guid worker)return Forbid();
  var visit=await db.Visits.SingleOrDefaultAsync(x=>x.Id==visitId&&x.CareWorkerId==worker&&x.OrganizationId==tenant.OrganizationId&&x.BranchId==(tenant.BranchId??TenantDefaults.BranchId),t);
  if(visit is null)return NotFound();
  var now=DateTimeOffset.UtcNow;
  var visitEnd=visit.StartsAt.AddMinutes(visit.DurationMinutes+15);
  if(visit.Status is not (VisitStatus.Scheduled or VisitStatus.InProgress)||now<visit.StartsAt.AddMinutes(-30)||now>=visitEnd)return BadRequest(new{message="Live sharing is available only around an assigned active visit."});
  var consents=await Rows("select ends_at from location_sharing_consents where organization_id=@organization and branch_id=@branch and subject_type='CareWorker' and subject_id=@worker and active=true and sharing_mode<>'Hidden' and starts_at<=now() and ends_at>now()",c=>Add(c,"worker",worker),t);
  if(consents.Count==0)return Forbid();
  var consentEnd=consents[0]["endsAt"] is DateTime dateTime ? new DateTimeOffset(DateTime.SpecifyKind(dateTime,DateTimeKind.Utc)) : (DateTimeOffset)consents[0]["endsAt"]!;
  var endsAt=visitEnd<consentEnd?visitEnd:consentEnd;
  await Exec("update worker_live_sharing set stopped_at=now() where organization_id=@organization and care_worker_id=@worker and stopped_at is null",c=>Add(c,"worker",worker),t);
  var id=Guid.NewGuid();
  await Exec("insert into worker_live_sharing(id,organization_id,branch_id,care_worker_id,visit_id,ends_at) values(@id,@organization,@branch,@worker,@visit,@ends)",c=>{Add(c,"id",id);Add(c,"worker",worker);Add(c,"visit",visitId);Add(c,"ends",endsAt);},t);
  await Audit("location.worker_sharing_started","WorkerLiveSharing",id,t);
  return Ok(new{visitId,endsAt});
 }

 [HttpPost("live-sharing/stop"),Authorize(Roles="CareWorker")]
 public async Task<IActionResult> StopLiveSharing(CancellationToken t)
 {
  if(user.CareWorkerId is not Guid worker)return Forbid();
  await Exec("update worker_live_sharing set stopped_at=now() where organization_id=@organization and branch_id=@branch and care_worker_id=@worker and stopped_at is null",c=>Add(c,"worker",worker),t);
  await Audit("location.worker_sharing_stopped","CareWorker",worker,t);
  return NoContent();
 }

 [HttpPost("live-location"),Authorize(Roles="CareWorker")]
 public async Task<IActionResult> LiveLocation(LiveLocationRequest request,CancellationToken t)
 {
  if(user.CareWorkerId is not Guid worker)return Forbid();
  if(request.Latitude is <-90 or >90||request.Longitude is <-180 or >180||request.AccuracyMeters is <0 or >10000||request.CapturedAt<DateTimeOffset.UtcNow.AddMinutes(-5)||request.CapturedAt>DateTimeOffset.UtcNow.AddMinutes(1))return BadRequest(new{message="A current valid location is required."});
  var visit=await db.Visits.SingleOrDefaultAsync(x=>x.Id==request.VisitId&&x.CareWorkerId==worker&&x.OrganizationId==tenant.OrganizationId&&x.BranchId==(tenant.BranchId??TenantDefaults.BranchId),t);
  if(visit is null)return NotFound();
  if(visit.Status is not (VisitStatus.Scheduled or VisitStatus.InProgress))return Forbid();
  var permitted=await Scalar("""
   select count(*) from worker_live_sharing s join location_sharing_consents c on c.organization_id=s.organization_id and c.branch_id=s.branch_id and c.subject_type='CareWorker' and c.subject_id=s.care_worker_id
   where s.organization_id=@organization and s.branch_id=@branch and s.care_worker_id=@worker and s.visit_id=@visit and s.stopped_at is null and s.ends_at>now() and s.started_at<=@captured
     and c.active=true and c.sharing_mode<>'Hidden' and c.starts_at<=now() and c.ends_at>now()
   """,c=>{Add(c,"worker",worker);Add(c,"visit",request.VisitId);Add(c,"captured",request.CapturedAt);},t);
  if(Convert.ToInt64(permitted)==0)return Forbid();
  await Exec("insert into visit_location_events(id,visit_id,service_user_id,care_worker_id,organization_id,branch_id,event_type,latitude,longitude,accuracy_meters,captured_at,source,notes) values(@id,@visit,@person,@worker,@organization,@branch,'LiveUpdate',@lat,@lng,@accuracy,@captured,'Device','Consented live update')",c=>{Add(c,"id",Guid.NewGuid());Add(c,"visit",visit.Id);Add(c,"person",visit.ServiceUserId);Add(c,"worker",worker);Add(c,"lat",request.Latitude);Add(c,"lng",request.Longitude);Add(c,"accuracy",request.AccuracyMeters);Add(c,"captured",request.CapturedAt);},t);
  return Accepted();
 }

 [HttpGet("diary/{id:guid}"),Authorize(Roles="CareCoordinator,CareManager,Administrator,BackOffice")]
 public async Task<IActionResult> DiaryDetail(Guid id,CancellationToken t){var entries=await Rows("select d.id,d.category,d.priority,d.status,d.subject,d.detail,d.event_at,d.due_at,d.owner_id,coalesce(u.\"UserName\",'') owner_name,d.created_by,d.created_at from operational_diary_entries d left join \"AppUsers\" u on u.\"Id\"=d.owner_id and u.\"OrganizationId\"=d.organization_id where d.id=@id and d.organization_id=@organization and (@wide or d.branch_id=@branch)",c=>Add(c,"id",id),t);if(entries.Count==0)return NotFound();entries[0]["recipientIds"]=(await Rows("select recipient_user_id from operational_diary_recipients where diary_entry_id=@id and organization_id=@organization",c=>Add(c,"id",id),t)).Select(row=>row["recipientUserId"]).ToArray();entries[0]["comments"]=await Rows("select id,body,created_by,created_at from operational_diary_comments where diary_entry_id=@id and organization_id=@organization order by created_at",c=>Add(c,"id",id),t);entries[0]["history"]=await Rows("select id,action,from_status,to_status,reason,actor,created_at from operational_diary_history where diary_entry_id=@id and organization_id=@organization order by created_at",c=>Add(c,"id",id),t);return Ok(entries[0]);}

 [HttpPost("diary/{id:guid}/comments"),Authorize(Roles="CareCoordinator,CareManager,Administrator")]
 public async Task<IActionResult> Comment(Guid id,DiaryCommentRequest request,CancellationToken t){if(string.IsNullOrWhiteSpace(request.Body)||request.Body.Trim().Length>2000)return BadRequest(new{message="A comment of up to 2,000 characters is required."});var changed=await Exec("insert into operational_diary_comments(id,diary_entry_id,organization_id,body,created_by) select @comment,id,@organization,@body,@actor from operational_diary_entries where id=@id and organization_id=@organization and (@wide or branch_id=@branch)",c=>{Add(c,"comment",Guid.NewGuid());Add(c,"id",id);Add(c,"body",request.Body.Trim());},t);if(changed==0)return NotFound();await Exec("insert into operational_diary_history(id,diary_entry_id,organization_id,action,reason,actor) values(@history,@id,@organization,'Comment added','',@actor)",c=>{Add(c,"history",Guid.NewGuid());Add(c,"id",id);},t);await Audit("operations.diary_commented","OperationalDiaryEntry",id,t);return NoContent();}

 [HttpGet("training-courses"),Authorize(Roles="CareCoordinator,CareManager,Administrator,BackOffice")]
 public Task<List<Dictionary<string,object?>>> Courses([FromQuery]bool includeInactive,CancellationToken t)=>Rows("select id,branch_id,code,title,category,provider,validity_months,is_mandatory,active,description,required_job_titles from training_catalogue_courses where organization_id=@organization and (branch_id is null or @wide or branch_id=@branch) and (active=true or (@inactive and @manager)) order by active desc,is_mandatory desc,title",c=>{Add(c,"inactive",includeInactive);Add(c,"manager",User.IsInRole("CareManager")||User.IsInRole("Administrator"));},t);

 [HttpGet("training-job-titles"),Authorize(Roles="CareManager,Administrator")]
 public async Task<string[]> JobTitles(CancellationToken t)=>(await Rows("select title from (select distinct trim(job_title) title from worker_employment_profiles where organization_id=@organization and (@wide or branch_id=@branch) and trim(job_title)<>'' and not exists(select 1 from care_operation_options where organization_id=@organization and kind='JobTitle' and active=true) union select value title from care_operation_options where organization_id=@organization and kind='JobTitle' and active=true) titles order by title",_=>{},t)).Select(x=>(string)x["title"]!).ToArray();

 [HttpGet("options"),Authorize(Roles="CareWorker,CareCoordinator,CareManager,Administrator,BackOffice")]
 public Task<List<Dictionary<string,object?>>> Options(CancellationToken t)=>Rows("select id,kind,value,active from care_operation_options where organization_id=@organization and (active or @manager) order by kind,value",c=>Add(c,"manager",User.IsInRole("Administrator")),t);

 [HttpPost("options"),Authorize(Roles="Administrator")]
 public async Task<IActionResult> AddOption(CareOperationOptionRequest r,CancellationToken t){if(!tenant.IsOrganizationWide)return Forbid();if(!ValidOption(r))return BadRequest(new{message="Choose a valid option type and a value up to 120 characters."});var id=Guid.NewGuid();var changed=await Exec("insert into care_operation_options(id,organization_id,kind,value,created_by) values(@id,@organization,@kind,@value,@actor) on conflict(organization_id,kind,value) do nothing",c=>{Add(c,"id",id);Add(c,"kind",r.Kind);Add(c,"value",r.Value.Trim());},t);if(changed==0)return Conflict(new{message="This option already exists. Reactivate it if needed."});await Audit("operations.option_created","CareOperationOption",id,t);return Created("",new{id});}

 [HttpPut("options/{id:guid}"),Authorize(Roles="Administrator")]
 public async Task<IActionResult> UpdateOption(Guid id,CareOperationOptionRequest r,CancellationToken t)
 {
  if(!tenant.IsOrganizationWide)return Forbid();
  if(!ValidOption(r))return BadRequest(new{message="Choose a valid option type and a value up to 120 characters."});
  var existing=await Rows("select kind from care_operation_options where id=@id and organization_id=@organization",c=>Add(c,"id",id),t);
  if(existing.Count==0)return NotFound();
  if(!string.Equals(existing[0]["kind"]?.ToString(),r.Kind,StringComparison.Ordinal))
   return Conflict(new{message="An option type cannot be changed. Create a new option instead."});
  var duplicate=await Scalar("select exists(select 1 from care_operation_options where organization_id=@organization and kind=@kind and value=@value and id<>@id)",c=>{Add(c,"id",id);Add(c,"kind",r.Kind);Add(c,"value",r.Value.Trim());},t);
  if(duplicate is true)return Conflict(new{message="This option already exists. Reactivate it if needed."});
  var changed=await Exec("update care_operation_options set value=@value,active=@active,updated_by=@actor,updated_at=now() where id=@id and organization_id=@organization and kind=@kind",c=>{Add(c,"id",id);Add(c,"kind",r.Kind);Add(c,"value",r.Value.Trim());Add(c,"active",r.Active);},t);
  if(changed==0)return Conflict(new{message="The option changed while you were editing it. Reload and try again."});
  await Audit("operations.option_updated","CareOperationOption",id,t);
  return NoContent();
 }

 [HttpPost("training-courses"),Authorize(Roles="CareManager,Administrator")]
 public async Task<IActionResult> Course(TrainingCourseRequest r,CancellationToken t){if(!ValidCourse(r))return BadRequest(new{message="Code, title and positive validity are required; job titles must be unique."});if(!await CourseTitlesAllowed(r,null,t))return BadRequest(new{message="Choose active job titles configured in Administration."});var id=Guid.NewGuid();await Exec("insert into training_catalogue_courses(id,organization_id,branch_id,code,title,category,provider,validity_months,is_mandatory,active,description,required_job_titles) values(@id,@organization,@branch,@code,@title,@category,@provider,@months,@mandatory,true,@description,@titles)",c=>BindCourse(c,r,id),t);await Audit("training.course_created","TrainingCourse",id,t);return Created("",new{id});}

 [HttpPut("training-courses/{id:guid}"),Authorize(Roles="CareManager,Administrator")]
 public async Task<IActionResult> UpdateCourse(Guid id,TrainingCourseRequest r,CancellationToken t){if(!ValidCourse(r))return BadRequest(new{message="Code, title and positive validity are required; job titles must be unique."});if(!await CourseTitlesAllowed(r,id,t))return BadRequest(new{message="Choose active job titles configured in Administration."});var changed=await Exec("update training_catalogue_courses set code=@code,title=@title,category=@category,provider=@provider,validity_months=@months,is_mandatory=@mandatory,description=@description,required_job_titles=@titles where id=@id and organization_id=@organization and (@wide or branch_id=@branch)",c=>BindCourse(c,r,id),t);if(changed==0)return NotFound();await Audit("training.course_updated","TrainingCourse",id,t);return NoContent();}

 [HttpPut("training-courses/{id:guid}/active"),Authorize(Roles="CareManager,Administrator")]
 public async Task<IActionResult> SetCourseActive(Guid id,TrainingCourseActiveRequest r,CancellationToken t){var changed=await Exec("update training_catalogue_courses set active=@active where id=@id and organization_id=@organization and (@wide or branch_id=@branch) and active<>@active",c=>{Add(c,"id",id);Add(c,"active",r.Active);},t);if(changed==0)return NotFound();await Audit(r.Active?"training.course_activated":"training.course_deactivated","TrainingCourse",id,t);return NoContent();}

 static bool ValidCourse(TrainingCourseRequest r)=>!string.IsNullOrWhiteSpace(r.Code)&&!string.IsNullOrWhiteSpace(r.Title)&&r.ValidityMonths is not <=0&&(r.RequiredJobTitles??[]).All(x=>!string.IsNullOrWhiteSpace(x))&&(r.RequiredJobTitles??[]).Select(x=>x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count()==(r.RequiredJobTitles??[]).Length;
 async Task<bool> CourseTitlesAllowed(TrainingCourseRequest request,Guid? courseId,CancellationToken token)
 {
  var active=(await Rows("select value from care_operation_options where organization_id=@organization and kind='JobTitle' and active=true",_=>{},token)).Select(row=>row["value"]?.ToString()).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
  if(active.Count==0)return true;
  if(courseId is Guid id)
  {
   var existing=await Rows("select required_job_titles from training_catalogue_courses where id=@id and organization_id=@organization and (@wide or branch_id=@branch)",c=>Add(c,"id",id),token);
   if(existing.FirstOrDefault()?.GetValueOrDefault("requiredJobTitles") is string[] previous)
    active.UnionWith(previous);
  }
  return (request.RequiredJobTitles??[]).All(title=>active.Contains(title.Trim()));
 }
 static bool ValidOption(CareOperationOptionRequest r)=>(r.Kind is "DiaryCategory" or "CareProvided" or "DurationReason" or "CancellationReason" or "JobTitle" or "WorkerLeavingReason" or "ClientStatus" or "ClientLeavingReason")&&!string.IsNullOrWhiteSpace(r.Value)&&r.Value.Trim().Length<=120&&!(r.Kind=="ClientStatus"&&r.Value.Trim().Equals("Archived",StringComparison.OrdinalIgnoreCase));
 static void BindCourse(DbCommand c,TrainingCourseRequest r,Guid id){Add(c,"id",id);Add(c,"code",r.Code.Trim());Add(c,"title",r.Title.Trim());Add(c,"category",r.Category?.Trim()??"");Add(c,"provider",r.Provider?.Trim()??"");Add(c,"months",r.ValidityMonths);Add(c,"mandatory",r.IsMandatory);Add(c,"description",r.Description?.Trim()??"");Add(c,"titles",(r.RequiredJobTitles??[]).Select(x=>x.Trim()).ToArray());}

 [HttpPost("training-bookings"),Authorize(Roles="CareCoordinator,CareManager,Administrator")]
 public async Task<IActionResult> Book(TrainingBookingRequest r,CancellationToken t){if(r.ScheduledAt<=DateTimeOffset.UtcNow)return BadRequest(new{message="Training must be scheduled in the future."});var id=Guid.NewGuid();var changed=await Exec("insert into worker_training_bookings(id,course_id,care_worker_id,organization_id,branch_id,scheduled_at,status,booked_by) select @id,c.id,w.\"Id\",@organization,w.\"BranchId\",@scheduled,'Booked',@actor from training_catalogue_courses c join \"CareWorkers\" w on w.\"Id\"=@worker and w.\"OrganizationId\"=@organization and (@wide or w.\"BranchId\"=@branch) where c.id=@course and c.organization_id=@organization and (c.branch_id is null or c.branch_id=w.\"BranchId\") and c.active=true",c=>{Add(c,"id",id);Add(c,"course",r.CourseId);Add(c,"worker",r.CareWorkerId);Add(c,"scheduled",r.ScheduledAt);},t);if(changed==0)return BadRequest(new{message="Course or worker is unavailable in this branch."});await Audit("training.booking_created","TrainingBooking",id,t);return Created("",new{id});}

 [HttpPut("training-bookings/{id:guid}/attendance"),Authorize(Roles="CareCoordinator,CareManager,Administrator")]
 public async Task<IActionResult> Attendance(Guid id,TrainingAttendanceRequest r,CancellationToken t){if(r.Status is not("Attended" or "NoShow" or "Cancelled")||r.ConfirmationStatus is not("Pending" or "Confirmed" or "Declined"))return BadRequest(new{message="Valid attendance and confirmation states are required."});var changed=await Exec("""update worker_training_bookings b set status=@status,confirmation_status=@confirmation,confirmed_at=case when @confirmation='Confirmed' then now() else b.confirmed_at end,attendance_notes=@notes,completed_at=case when @status='Attended' then now() else b.completed_at end,expires_at=case when @status='Attended' and c.validity_months is not null then now()+make_interval(months=>c.validity_months) else b.expires_at end from training_catalogue_courses c where b.course_id=c.id and b.id=@id and b.organization_id=@organization and (@wide or b.branch_id=@branch)""",c=>{Add(c,"id",id);Add(c,"status",r.Status);Add(c,"confirmation",r.ConfirmationStatus);Add(c,"notes",r.Notes??"");},t);if(changed==0)return NotFound();await Audit("training.attendance_updated","TrainingBooking",id,t);return NoContent();}

 [HttpGet("training-compliance"),Authorize(Roles="CareCoordinator,CareManager,Administrator,BackOffice")]
 public Task<List<Dictionary<string,object?>>> Compliance(CancellationToken t)=>Rows("""
   select w."Id" care_worker_id,w."FullName" care_worker_name,
     count(c.id) filter(where c.is_mandatory) required,
     count(c.id) filter(where c.is_mandatory and b.status in ('Attended','Completed') and (b.expires_at is null or b.expires_at>now())) valid,
     count(c.id) filter(where c.is_mandatory and b.expires_at between now() and now()+interval '30 days') expiring,
     count(c.id) filter(where c.is_mandatory and b.expires_at<=now()) expired,
     count(c.id) filter(where c.is_mandatory)=count(c.id) filter(where c.is_mandatory and b.status in ('Attended','Completed') and (b.expires_at is null or b.expires_at>now())) compliant
   from "CareWorkers" w
   left join training_catalogue_courses c on c.organization_id=w."OrganizationId" and c.active=true and c.is_mandatory and (c.branch_id is null or c.branch_id=w."BranchId") and (cardinality(c.required_job_titles)=0 or exists (select 1 from worker_employment_profiles ep where ep.care_worker_id=w."Id" and ep.organization_id=w."OrganizationId" and trim(ep.job_title)=any(c.required_job_titles)))
   left join lateral (
     select status,expires_at from worker_training_bookings
     where organization_id=w."OrganizationId" and care_worker_id=w."Id" and course_id=c.id
     order by case when status in ('Attended','Completed') then 0 else 1 end, completed_at desc nulls last,scheduled_at desc limit 1
   ) b on true
   where w."OrganizationId"=@organization and (@wide or w."BranchId"=@branch)
   group by w."Id",w."FullName" order by w."FullName"
   """,_=>{},t);

 async Task<List<Dictionary<string,object?>>> Rows(string sql,Action<DbCommand> bind,CancellationToken t){var list=new List<Dictionary<string,object?>>();await using var c=await Command(sql,t);bind(c);await using var r=await c.ExecuteReaderAsync(t);while(await r.ReadAsync(t)){var row=new Dictionary<string,object?>();for(var i=0;i<r.FieldCount;i++)row[Camel(r.GetName(i))]=r.IsDBNull(i)?null:r.GetValue(i);list.Add(row);}return list;}async Task<int> Exec(string sql,Action<DbCommand> bind,CancellationToken t){await using var c=await Command(sql,t);bind(c);return await c.ExecuteNonQueryAsync(t);}async Task<object?> Scalar(string sql,Action<DbCommand> bind,CancellationToken t){await using var c=await Command(sql,t);bind(c);return await c.ExecuteScalarAsync(t);}async Task<DbCommand> Command(string sql,CancellationToken t){var cn=db.Database.GetDbConnection();if(cn.State!=ConnectionState.Open)await cn.OpenAsync(t);var c=cn.CreateCommand();c.CommandText=sql;Add(c,"organization",tenant.OrganizationId);Add(c,"branch",tenant.BranchId??TenantDefaults.BranchId);Add(c,"wide",tenant.IsOrganizationWide);Add(c,"actor",user.UserName);return c;}async Task Audit(string action,string entity,Guid id,CancellationToken t){db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),action,user.UserName,entity,id,DateTimeOffset.UtcNow,tenant.OrganizationId,tenant.BranchId??TenantDefaults.BranchId));await db.SaveChangesAsync(t);}static void Add(DbCommand c,string n,object? v){if(c.Parameters.Contains(n))return;var p=c.CreateParameter();p.ParameterName=n;p.Value=v??DBNull.Value;c.Parameters.Add(p);}static string Camel(string s){var p=s.Split('_');return p[0]+string.Concat(p.Skip(1).Select(x=>char.ToUpperInvariant(x[0])+x[1..]));}
}
public sealed record MapPointRequest(string Address,string? Postcode,decimal Latitude,decimal Longitude,string Provider,decimal? Confidence);
public sealed record LiveLocationRequest(Guid VisitId,decimal Latitude,decimal Longitude,decimal? AccuracyMeters,DateTimeOffset CapturedAt);
public sealed record DiaryCommentRequest(string Body);
public sealed record TrainingCourseRequest(string Code,string Title,string Category,string Provider,int? ValidityMonths,bool IsMandatory,string Description,string[]? RequiredJobTitles=null);
public sealed record TrainingCourseActiveRequest(bool Active);
public sealed record CareOperationOptionRequest(string Kind,string Value,bool Active=true);
public sealed record TrainingBookingRequest(Guid CourseId,Guid CareWorkerId,DateTimeOffset ScheduledAt);
public sealed record TrainingAttendanceRequest(string Status,string ConfirmationStatus,string? Notes);
