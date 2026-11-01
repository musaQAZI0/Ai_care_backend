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

[ApiController,Authorize(Roles="Administrator,BackOffice,CareManager")]
[Route("api/phase1/finance/billing")]
public sealed class BillingEngineController(CareDbContext db,ITenantContext tenant,ICurrentUserContext user):ControllerBase
{
 Guid Branch=>tenant.BranchId??TenantDefaults.BranchId;bool Manage=>user.IsAdministrator||user.IsBackOffice;
 [HttpGet("visit-contexts")]public async Task<IActionResult> VisitContexts(CancellationToken token)=>Ok(await Query("select visit_id,staff_level,location_code,reference from finance_visit_billing_context where organization_id=@organization and (@wide or branch_id=@branch)",_=>{},r=>new{visitId=r.GetGuid(0),staffLevel=r.GetString(1),locationCode=r.GetString(2),reference=r.GetString(3)},token));
 [HttpPut("visits/{visit:guid}/context")]public Task<IActionResult> SetContext(Guid visit,VisitBillingContextRequest r,CancellationToken token)=>Guard(async()=>
 {
  if(user.UserId is null)return Unauthorized();if(string.IsNullOrWhiteSpace(r.StaffLevel)||string.IsNullOrWhiteSpace(r.LocationCode)||string.IsNullOrWhiteSpace(r.Reference))return BadRequest(new{message="Explicit billing staff level, location code and source reference required; these are not inferred from job titles or addresses."});
  var source=await db.Visits.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==visit&&x.OrganizationId==tenant.OrganizationId&&(tenant.IsOrganizationWide||x.BranchId==Branch),token);if(source is null)return NotFound();
  var current=await Query("select staff_level,location_code,reference from finance_visit_billing_context where visit_id=@visit and organization_id=@organization",c=>Add(c,"visit",visit),x=>new{Staff=x.GetString(0),Location=x.GetString(1),Reference=x.GetString(2)},token);if(current.Count==1&&current[0].Staff==r.StaffLevel.Trim()&&current[0].Location==r.LocationCode.Trim()&&current[0].Reference==r.Reference.Trim())return Ok(new{visitId=visit,replayed=true});
  await Exec("insert into finance_visit_billing_context(visit_id,organization_id,branch_id,staff_level,location_code,reference,created_by) values(@visit,@organization,@target,@staff,@location,@reference,@creator) on conflict(visit_id) do update set staff_level=excluded.staff_level,location_code=excluded.location_code,reference=excluded.reference,created_by=excluded.created_by,created_at=now()",c=>{Add(c,"visit",visit);Add(c,"target",source.BranchId);Add(c,"staff",r.StaffLevel.Trim());Add(c,"location",r.LocationCode.Trim());Add(c,"reference",r.Reference.Trim());Add(c,"creator",user.UserId.Value);},token);Audit("BILLING_CONTEXT_RECORDED","Visit",visit);await db.SaveChangesAsync(token);return Ok(new{visitId=visit});
 },token);
 [HttpPost("generate")]
 public Task<IActionResult> Generate(GenerateBillableEventsRequest request,CancellationToken token)
 {
  if(!Manage)return Task.FromResult<IActionResult>(Forbid());
  if(user.UserId is null)return Task.FromResult<IActionResult>(Unauthorized());
  if(request.ServiceFrom is not null || request.ServiceTo is not null)
  {
   if(request.ServiceFrom is not DateOnly from || request.ServiceTo is not DateOnly to)return Task.FromResult<IActionResult>(BadRequest(new{message="Both UK service dates are required."}));
   try{var bounds=FinanceServicePeriod.FromInclusiveDates(from,to);request=request with{PeriodStart=bounds.Start,PeriodEnd=bounds.End};}
   catch(ArgumentException e){return Task.FromResult<IActionResult>(BadRequest(new{message=e.Message}));}
  }
  if(request.PeriodStart>=request.PeriodEnd)return Task.FromResult<IActionResult>(BadRequest(new{message="Period start must be before period end."}));
  return Guard(async()=>
  {
   var visits=await db.Visits.FromSqlInterpolated($"select * from \"Visits\" where \"OrganizationId\"={tenant.OrganizationId} and ({tenant.IsOrganizationWide} or \"BranchId\"={Branch}) and \"StartsAt\">={request.PeriodStart} and \"StartsAt\"<{request.PeriodEnd} order by \"StartsAt\",\"Id\" for update").AsNoTracking().ToListAsync(token);
   var generated=0;var skipped=0;var exceptions=0;
   foreach(var visit in visits)
   {
    if(await Scalar("select count(*) from finance_billable_events where organization_id=@organization and visit_id=@visit and status<>'Rejected'",c=>Add(c,"visit",visit.Id),token)>0){skipped++;continue;}
    var day=AppointmentWorkflow.UkDate(visit.StartsAt);
    var contracts=await Query("""
      select c.id,c.funding_arrangement_id,c.funder_id,c.rate_version_id,c.currency,c.billing_basis,c.cancellation_policy,c.rounding_mode,c.version,c.service_type,c.cap_mode,c.cap_amount
      from finance_contract_versions c join funding_arrangements a on a.id=c.funding_arrangement_id and a.organization_id=c.organization_id and a.branch_id=c.branch_id and a.service_user_id=c.person_id and a.funder_id=c.funder_id
      join finance_funders f on f.id=c.funder_id and f.organization_id=c.organization_id and (f.branch_id is null or f.branch_id=c.branch_id) and f.active and f.currency=c.currency
      where c.organization_id=@organization and c.branch_id=@visitBranch and c.person_id=@person and c.status='Approved'
        and @day between c.authorised_from and c.authorised_to and a.status='Active'
      for update of a
      """,c=>{Add(c,"visitBranch",visit.BranchId);Add(c,"person",visit.ServiceUserId);Add(c,"day",day);},r=>new Contract(r.GetGuid(0),r.GetGuid(1),r.GetGuid(2),r.GetGuid(3),r.GetString(4).Trim(),r.GetString(5),r.GetString(6),r.GetString(7),r.GetInt32(8),r.GetString(9),r.GetString(10),r.IsDBNull(11)?null:r.GetDecimal(11)),token);
    if(contracts.Count!=1){exceptions+=await Exception(visit,contracts.Count==0?"MISSING_APPROVED_CONTRACT":"CONFLICTING_CONTRACTS","Exactly one approved effective contract is required; split funding requires an approved allocation workflow.",token);continue;}
    var contract=contracts[0];
    if(visit.Status==VisitStatus.Cancelled){exceptions+=await Exception(visit,contract.Cancellation=="NonChargeable"?"CANCELLATION_NON_CHARGEABLE":"CANCELLATION_REVIEW",contract.Cancellation=="NonChargeable"?"Excluded by the approved non-chargeable cancellation policy.":"Cancellation needs an authorised decision. No automatic cancellation charge was posted.",token);continue;}
    if(visit.Status!=VisitStatus.Completed||visit.DurationMinutes<=0||!string.Equals(visit.VisitType,contract.Service,StringComparison.OrdinalIgnoreCase)){exceptions+=await Exception(visit,"VISIT_NOT_ELIGIBLE","Completed visit and contract service type must match.",token);continue;}
    var additionalWorkers=await Scalar("select count(*) from visit_care_worker_assignments where visit_id=@visit and care_worker_id<>@primary",c=>{Add(c,"visit",visit.Id);Add(c,"primary",visit.CareWorkerId);},token);
    if(additionalWorkers>0&&await Scalar("select count(*) from finance_multi_worker_policies where contract_version_id=@contract and approved_by is not null and billing_unit='PerVisit'",c=>Add(c,"contract",contract.Id),token)!=1){exceptions+=await Exception(visit,"MULTI_WORKER_POLICY_REQUIRED","Approve an explicit per-visit crew billing policy. Staff counts are never multiplied into the client charge automatically; per-worker pricing requires a different supported contract workflow.",token);continue;}
    var attendance=await Query("""
      select t.id,t.revision,t.actual_minutes from timesheet_entries t join "Visits" v on v."Id"=t.visit_id
      where t.organization_id=@organization and t.branch_id=@visitBranch and t.visit_id=@visit and t.care_worker_id=v."CareWorkerId"
        and t.status='Approved' and t.approved_by is not null and t.submitted_by is not null and t.approved_by<>t.submitted_by
        and t.planned_minutes=v."DurationMinutes" and v."CheckedOutAt">v."CheckedInAt" and t.actual_minutes>0
        and abs(t.actual_minutes-extract(epoch from(v."CheckedOutAt"-v."CheckedInAt"))/60)<0.000001
      for share of t
      """,c=>{Add(c,"visitBranch",visit.BranchId);Add(c,"visit",visit.Id);},r=>new Attendance(r.GetGuid(0),r.GetInt32(1),r.GetDecimal(2)),token);
    if(attendance.Count!=1){exceptions+=await Exception(visit,"UNAPPROVED_OR_STALE_ATTENDANCE","Approve current attendance with a different reviewer before billing; payable time is not used as actual care time.",token);continue;}
    var source=attendance[0];var minutes=contract.Basis=="Actual"?source.Actual:visit.DurationMinutes;
    var rateStart=contract.Basis=="Actual"?visit.CheckedInAt!.Value:visit.StartsAt;
    if(AppointmentWorkflow.UkDate(rateStart)!=day){exceptions+=await Exception(visit,"SERVICE_DATE_REVIEW","Actual attendance moved to a different service date; an explicit contract/attendance correction is required.",token);continue;}
    var holiday=await Scalar("select count(*) from admin_bank_holidays where organization_id=@organization and holiday_date=@date and (branch_id is null or branch_id=@visitBranch)",c=>{Add(c,"date",day);Add(c,"visitBranch",visit.BranchId);},token)>0;
    var rates=await Query("""
      select v.id,r.id,r.rule_type,r.day_type,r.unit,r.unit_rate,r.minimum_quantity,r.priority,r.time_from,r.time_to,r.staff_level,r.location_code,r.minimum_minutes,r.maximum_minutes
      from finance_rate_versions v join finance_rate_rules r on r.rate_version_id=v.id and r.organization_id=v.organization_id
      join finance_rate_cards card on card.id=v.rate_card_id and card.organization_id=v.organization_id
      where v.id=@version and v.organization_id=@organization and v.status='Active' and card.active and card.currency=@currency
        and (card.branch_id is null or card.branch_id=@visitBranch) and (v.branch_id is null or v.branch_id=@visitBranch)
        and @date>=v.effective_from and (v.effective_to is null or @date<=v.effective_to)
      """,c=>{Add(c,"version",contract.RateVersion);Add(c,"currency",contract.Currency);Add(c,"visitBranch",visit.BranchId);Add(c,"date",day);},r=>new BillingRate(r.GetGuid(0),r.GetGuid(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetDecimal(5),r.GetDecimal(6),r.GetInt32(7),r.IsDBNull(8)?null:r.GetFieldValue<TimeOnly>(8),r.IsDBNull(9)?null:r.GetFieldValue<TimeOnly>(9),r.IsDBNull(10)?null:r.GetString(10),r.IsDBNull(11)?null:r.GetString(11),r.IsDBNull(12)?null:r.GetDecimal(12),r.IsDBNull(13)?null:r.GetDecimal(13)),token);
    var dimensions=await Query("select staff_level,location_code from finance_visit_billing_context where visit_id=@visit and organization_id=@organization and branch_id=@visitBranch",c=>{Add(c,"visit",visit.Id);Add(c,"visitBranch",visit.BranchId);},r=>new{Staff=r.GetString(0),Location=r.GetString(1)},token);BillingRate rate;decimal quantity,amount;
    try{rate=FinanceRateSelection.Select(rates,rateStart,minutes,holiday,dimensions.FirstOrDefault()?.Staff,dimensions.FirstOrDefault()?.Location);(quantity,amount)=FinanceRateSelection.Calculate(rate,minutes,contract.Rounding);}
    catch(InvalidOperationException e){var parts=e.Message.Split(':',2);exceptions+=await Exception(visit,parts[0],parts.Length==2?parts[1].Trim():e.Message,token);continue;}
    var consumed=await Query("select coalesce(sum(final_amount),0) from finance_billable_events where funding_arrangement_id=@funding and status<>'Rejected'",c=>Add(c,"funding",contract.Funding),r=>r.GetDecimal(0),token);
    if(contract.Cap is decimal cap && consumed[0]+amount>cap){exceptions+=await Exception(visit,"AUTHORISATION_CAP_EXCEEDED","The approved lifetime funding authorisation cannot cover this service. Credits do not automatically restore authorisation.",token);continue;}
    await Exec("""
      insert into finance_billable_events(id,organization_id,branch_id,service_user_id,visit_id,funder_id,funding_arrangement_id,rate_version_id,rate_rule_id,
        service_date,quantity,unit,unit_rate,gross_amount,final_amount,currency,calculation_explanation,status,created_by,
        contract_version_id,timesheet_entry_id,source_revision,source_snapshot,billing_minutes,created_by_user_id)
      values(@id,@organization,@visitBranch,@person,@visit,@funder,@funding,@version,@rule,@date,@quantity,@unit,@rate,@amount,@amount,@currency,@explanation,'Validated',@actor,
        @contract,@attendance,@revision,(select finance_visit_source(v) from "Visits" v where v."Id"=@visit),@minutes,@actorId)
      """,c=>{Add(c,"id",Guid.NewGuid());Add(c,"visitBranch",visit.BranchId);Add(c,"person",visit.ServiceUserId);Add(c,"visit",visit.Id);Add(c,"funder",contract.Funder);Add(c,"funding",contract.Funding);Add(c,"version",rate.VersionId);Add(c,"rule",rate.RuleId);Add(c,"date",day);Add(c,"quantity",quantity);Add(c,"unit",rate.Unit);Add(c,"rate",rate.UnitRate);Add(c,"amount",amount);Add(c,"currency",contract.Currency);Add(c,"explanation",$"{contract.Basis} {minutes:0.########} minutes; {quantity:0.########} {rate.Unit.ToLowerInvariant()} at {rate.UnitRate}; contract version {contract.Version}; {rate.DayType} priority {rate.Priority}; billing staff {dimensions.FirstOrDefault()?.Staff ?? "unspecified"}, location {dimensions.FirstOrDefault()?.Location ?? "unspecified"}; duration band [{rate.MinimumMinutes}, {rate.MaximumMinutes}); final amount rounded to 2 decimals ({contract.Rounding}).");Add(c,"contract",contract.Id);Add(c,"attendance",source.Id);Add(c,"revision",source.Revision);Add(c,"minutes",minutes);},token);generated++;
   }
   Audit("BILLABLE_EVENTS_GENERATED","BillableEvent",null);await db.SaveChangesAsync(token);return Ok(new{examined=visits.Count,generated,skipped,exceptions});
  },token);
 }
 [HttpGet("events")]
 public async Task<IActionResult> Events([FromQuery]string? status,CancellationToken token)=>Ok(await Query("""select e.id,e.visit_id,e.service_user_id,e.service_date,e.quantity,e.unit,e.unit_rate,e.gross_amount,e.adjustment_amount,e.final_amount,e.currency,e.calculation_explanation,e.status,f.name,e.contract_version_id,e.timesheet_entry_id,e.source_revision,e.billing_minutes,finance_source_current(e.id) from finance_billable_events e join finance_funders f on f.id=e.funder_id and f.organization_id=e.organization_id where e.organization_id=@organization and (@wide or e.branch_id=@branch) and (coalesce(@status,'')='' or e.status=@status) order by e.service_date desc,e.created_at desc""",c=>Add(c,"status",status??""),r=>new{id=r.GetGuid(0),visitId=r.GetGuid(1),serviceUserId=r.GetGuid(2),serviceDate=r.GetFieldValue<DateOnly>(3),quantity=r.GetDecimal(4),unit=r.GetString(5),unitRate=r.GetDecimal(6),grossAmount=r.GetDecimal(7),adjustmentAmount=r.GetDecimal(8),finalAmount=r.GetDecimal(9),currency=r.GetString(10).Trim(),calculationExplanation=r.GetString(11),status=r.GetString(12),funderName=r.GetString(13),contractVersionId=r.IsDBNull(14)?null:(Guid?)r.GetGuid(14),attendanceId=r.IsDBNull(15)?null:(Guid?)r.GetGuid(15),sourceRevision=r.IsDBNull(16)?null:(int?)r.GetInt32(16),billingMinutes=r.IsDBNull(17)?null:(decimal?)r.GetDecimal(17),sourceCurrent=r.GetBoolean(18)},token));
 [HttpGet("exceptions")]
 public async Task<IActionResult> Exceptions(CancellationToken token)=>Ok(await Query("select id,visit_id,service_user_id,exception_code,message,status,created_at from finance_billing_exceptions where organization_id=@organization and (@wide or branch_id=@branch) order by status,created_at desc",_=>{},r=>new{id=r.GetGuid(0),visitId=r.GetGuid(1),serviceUserId=r.GetGuid(2),code=r.GetString(3),message=r.GetString(4),status=r.GetString(5),createdAt=r.GetFieldValue<DateTimeOffset>(6)},token));
 [HttpPost("events/{id:guid}/approve")]
 public Task<IActionResult> Approve(Guid id,CancellationToken token)=>Guard(async()=>
 {
  if(!Manage)return Forbid();
  var changed=await Exec("update finance_billable_events set status='Approved',approved_at=now(),approved_by=@actor,approved_by_user_id=@actorId,revision=revision+1 where id=@id and organization_id=@organization and (@wide or branch_id=@branch) and status='Validated' and created_by_user_id<>@actorId and finance_source_current(id)",c=>Add(c,"id",id),token);
  if(changed==0)return Conflict(new{message="An independent reviewer and current approved contract/attendance are required. Reject stale validated events and regenerate after correcting the source."});
  Audit("BILLABLE_EVENT_APPROVED","BillableEvent",id);await db.SaveChangesAsync(token);return NoContent();
 },token);
 [HttpPost("events/{id:guid}/adjust")]
 public Task<IActionResult> Adjust(Guid id,AdjustBillableEventRequest request,CancellationToken token)=>Guard(async()=>
 {
  if(!Manage)return Forbid();if(request.AdjustmentAmount==0||decimal.Round(request.AdjustmentAmount,2)!=request.AdjustmentAmount||string.IsNullOrWhiteSpace(request.Reason))return BadRequest(new{message="An exact-penny adjustment and reason are required."});
  var changed=await Exec("update finance_billable_events set adjustment_amount=@amount,final_amount=gross_amount+@amount,adjustment_reason=@reason,created_by=@actor,created_by_user_id=@actorId,revision=revision+1 where id=@id and organization_id=@organization and (@wide or branch_id=@branch) and status in ('Pending','Validated') and gross_amount+@amount>=0",c=>{Add(c,"id",id);Add(c,"amount",request.AdjustmentAmount);Add(c,"reason",request.Reason.Trim());},token);
  if(changed==0)return Conflict(new{message="Event cannot be adjusted in its current state or total becomes negative."});Audit("BILLABLE_EVENT_ADJUSTED","BillableEvent",id);await db.SaveChangesAsync(token);return NoContent();
 },token);
 [HttpPost("events/{id:guid}/reject")]
 public Task<IActionResult> Reject(Guid id,BillingDecisionRequest request,CancellationToken token)=>Guard(async()=>
 {
  if(!Manage)return Forbid();if(string.IsNullOrWhiteSpace(request.Reason))return BadRequest(new{message="A rejection reason is required."});
  var changed=await Exec("update finance_billable_events set status='Rejected',adjustment_reason=@reason,revision=revision+1 where id=@id and organization_id=@organization and (@wide or branch_id=@branch) and status in ('Pending','Validated')",c=>{Add(c,"id",id);Add(c,"reason",request.Reason.Trim());},token);
  if(changed==0)return Conflict(new{message="Event cannot be rejected in its current state."});Audit("BILLABLE_EVENT_REJECTED","BillableEvent",id);await db.SaveChangesAsync(token);return NoContent();
 },token);
 [HttpPost("exceptions/{id:guid}/resolve")]
 public async Task<IActionResult> ResolveException(Guid id,BillingDecisionRequest request,CancellationToken token){if(!Manage)return Forbid();if(string.IsNullOrWhiteSpace(request.Reason))return BadRequest(new{message="A resolution is required."});var changed=await Exec("update finance_billing_exceptions set status='Resolved',resolved_at=now(),resolved_by=@actor,resolution=@reason where id=@id and organization_id=@organization and (@wide or branch_id=@branch) and status='Open'",c=>{Add(c,"id",id);Add(c,"reason",request.Reason.Trim());},token);if(changed==0)return Conflict(new{message="Only open exceptions can be resolved."});Audit("BILLING_EXCEPTION_RESOLVED","BillingException",id);await db.SaveChangesAsync(token);return NoContent();}
 async Task<IActionResult> Guard(Func<Task<IActionResult>> action,CancellationToken token)
 {
  try{return await db.Database.CreateExecutionStrategy().ExecuteAsync(async()=>{await using var tx=await db.Database.BeginTransactionAsync(token);await Exec("select pg_advisory_xact_lock(hashtextextended(@key,0))",c=>Add(c,"key",$"finance-billing:{tenant.OrganizationId}"),token);var result=await action();await tx.CommitAsync(token);return result;});}
  catch(Npgsql.PostgresException e) when(e.SqlState is "23514" or "23505" or "23P01"){return Conflict(new{message="Billing source, authorisation cap or version changed. Reload and review the current source before retrying."});}
 }
 async Task<int> Exception(Visit visit,string code,string message,CancellationToken token)=>await Exec("insert into finance_billing_exceptions(id,organization_id,branch_id,visit_id,service_user_id,exception_code,message) values(@id,@organization,@visitBranch,@visit,@person,@code,@message) on conflict do nothing",c=>{Add(c,"id",Guid.NewGuid());Add(c,"visitBranch",visit.BranchId??Branch);Add(c,"visit",visit.Id);Add(c,"person",visit.ServiceUserId);Add(c,"code",code);Add(c,"message",message);},token);
 async Task<int> Scalar(string sql,Action<DbCommand> bind,CancellationToken t){await using var c=await Command(sql,t);bind(c);return Convert.ToInt32(await c.ExecuteScalarAsync(t));}async Task<int> Exec(string sql,Action<DbCommand> bind,CancellationToken t){await using var c=await Command(sql,t);bind(c);return await c.ExecuteNonQueryAsync(t);}async Task<List<T>> Query<T>(string sql,Action<DbCommand> bind,Func<DbDataReader,T> map,CancellationToken t){var a=new List<T>();await using var c=await Command(sql,t);bind(c);await using var r=await c.ExecuteReaderAsync(t);while(await r.ReadAsync(t))a.Add(map(r));return a;}async Task<DbCommand> Command(string sql,CancellationToken t){var cn=db.Database.GetDbConnection();if(cn.State!=ConnectionState.Open)await cn.OpenAsync(t);var c=cn.CreateCommand();c.Transaction=db.Database.CurrentTransaction?.GetDbTransaction();c.CommandText=sql;Add(c,"organization",tenant.OrganizationId);Add(c,"branch",Branch);Add(c,"wide",tenant.IsOrganizationWide);Add(c,"actor",user.UserName);Add(c,"actorId",user.UserId);return c;}static void Add(DbCommand c,string n,object? v){if(c.Parameters.Contains(n))return;var p=c.CreateParameter();p.ParameterName=n;p.Value=v??DBNull.Value;c.Parameters.Add(p);}void Audit(string action,string entity,Guid? id)=>db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),action,user.UserName,entity,id,DateTimeOffset.UtcNow,tenant.OrganizationId,Branch));
 sealed record Contract(Guid Id,Guid Funding,Guid Funder,Guid RateVersion,string Currency,string Basis,string Cancellation,string Rounding,int Version,string Service,string CapMode,decimal? Cap);
 sealed record Attendance(Guid Id,int Revision,decimal Actual);
}
public sealed record VisitBillingContextRequest(string StaffLevel,string LocationCode,string Reference);
public sealed record GenerateBillableEventsRequest(DateTimeOffset PeriodStart=default,DateTimeOffset PeriodEnd=default,DateOnly? ServiceFrom=null,DateOnly? ServiceTo=null);
public sealed record AdjustBillableEventRequest(decimal AdjustmentAmount,string Reason);
public sealed record BillingDecisionRequest(string Reason);
