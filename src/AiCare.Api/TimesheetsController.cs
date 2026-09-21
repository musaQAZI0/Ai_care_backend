using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
namespace AiCare.Api;

[ApiController, Authorize(Policy="Phase1User")]
[Route("api/phase1/timesheets")]
public sealed class TimesheetsController(CareDbContext db,ITenantContext tenant,ICurrentUserContext user):ControllerBase
{
 private bool Manage=>user.IsAdministrator||user.IsCareManager;
 private bool Finance=>user.IsAdministrator||user.IsBackOffice;
 private bool Staff=>Manage||Finance||user.IsCareCoordinator;
 [HttpGet]
 public async Task<IActionResult> Summary(CancellationToken t)
 {
  if(!Staff&&!user.IsCareWorker)return Forbid();
  return Ok(await Rows("select care_worker_id as \"careWorkerId\",count(*) as visits,count(*) as \"completedVisits\",sum(planned_minutes) as \"scheduledMinutes\",sum(payable_minutes)/60 as \"payableHours\",sum(mileage) as mileage,status from timesheet_entries where organization_id=@org and (@wide or branch_id=@branch) and (@staff or care_worker_id=@worker) group by care_worker_id,status",c=>{Add(c,"staff",Staff);Add(c,"worker",user.CareWorkerId??Guid.Empty);},t));
 }
 [HttpGet("periods")]
 public async Task<IActionResult> Periods(CancellationToken t)
 {
  if(!Staff&&!user.IsCareWorker)return Forbid();
  return Ok(await Rows("select * from timesheet_periods where organization_id=@org and (@wide or branch_id=@branch) order by period_start desc",_=>{},t));
 }
 [HttpPost("periods"),Authorize(Roles="CareManager,Administrator,BackOffice")]
 public Task<IActionResult> CreatePeriod(CreatePayPeriod r,CancellationToken t)=>Tx(async()=>{
  if(user.UserId is null)return Unauthorized();
  if(r.PeriodStart>=r.PeriodEnd||r.HourlyRate is null or <0||r.TravelRate is null or <0||r.MileageRate is null or <0)return BadRequest(new{message="Valid dates and explicit nonnegative hourly, travel and mileage rates are required."});
  if(!tenant.IsOrganizationWide&&r.BranchId!=tenant.BranchId)return NotFound();
  if(!await db.CareWorkers.AnyAsync(x=>x.OrganizationId==tenant.OrganizationId&&x.BranchId==r.BranchId,t))return NotFound();
  if(await Count("select count(*) from timesheet_periods where organization_id=@org and branch_id=@target and period_start<@end and period_end>@start",c=>{Add(c,"target",r.BranchId);Add(c,"start",r.PeriodStart);Add(c,"end",r.PeriodEnd);},t)>0)return Conflict(new{message="A pay period overlaps these dates in this branch."});
  var id=Guid.NewGuid();
  await Exec("insert into timesheet_periods(id,organization_id,branch_id,period_start,period_end,hourly_rate,travel_rate,mileage_rate,created_by) values(@id,@org,@target,@start,@end,@rate,@travel,@mileage,@actor)",c=>{Add(c,"id",id);Add(c,"target",r.BranchId);Add(c,"start",r.PeriodStart);Add(c,"end",r.PeriodEnd);Add(c,"rate",r.HourlyRate);Add(c,"travel",r.TravelRate);Add(c,"mileage",r.MileageRate);},t);
  await Audit("timesheet.period_created",id,r.BranchId,t);return Created($"/api/phase1/timesheets/periods/{id}",new{id});
 },t);
 [HttpGet("periods/{id:guid}")]
 public async Task<IActionResult> Detail(Guid id,CancellationToken t)
 {
  if(!Staff&&!user.IsCareWorker)return Forbid();var period=await Period(id,t);if(period is null)return NotFound();
  var entries=await Rows("select e.*,w.\"FullName\" as worker_name,v.\"StartsAt\" as visit_starts_at from timesheet_entries e join \"CareWorkers\" w on w.\"Id\"=e.care_worker_id join \"Visits\" v on v.\"Id\"=e.visit_id where e.period_id=@id and e.organization_id=@org and (@staff or e.care_worker_id=@worker) order by v.\"StartsAt\",e.id",c=>{Add(c,"id",id);Add(c,"staff",Staff);Add(c,"worker",user.CareWorkerId??Guid.Empty);},t);
  return Ok(new{period,entries});
 }
 [HttpPost("periods/{id:guid}/import"),Authorize(Roles="CareCoordinator,CareManager,Administrator,BackOffice")]
 public Task<IActionResult> Import(Guid id,CancellationToken t)=>Tx(async()=>{
  var p=await Period(id,t);if(p is null)return NotFound();if(p.Value.GetProperty("status").GetString()!="Open")return Conflict(new{message="Period is locked."});
  var count=await Exec("""
   insert into timesheet_entries(id,period_id,organization_id,branch_id,visit_id,care_worker_id,planned_minutes,actual_minutes,payable_minutes,hourly_rate,reason,changed_by)
   select gen_random_uuid(),p.id,p.organization_id,p.branch_id,v."Id",v."CareWorkerId",v."DurationMinutes",
     case when v."CheckedOutAt">=v."CheckedInAt" then extract(epoch from(v."CheckedOutAt"-v."CheckedInAt"))/60 end,
     case when v."CheckedOutAt">=v."CheckedInAt" then extract(epoch from(v."CheckedOutAt"-v."CheckedInAt"))/60 end,
     p.hourly_rate,'Imported completed visit',@actor
   from timesheet_periods p join "Visits" v on v."OrganizationId"=p.organization_id and v."BranchId"=p.branch_id
   where p.id=@id and v."Status"='Completed' and v."StartsAt">=p.period_start and v."StartsAt"<p.period_end
   and not exists(select 1 from finance_payroll_lines l where l.organization_id=p.organization_id and l.visit_id=v."Id")
   on conflict(organization_id,visit_id) do nothing
   """,c=>Add(c,"id",id),t);
  await Audit("timesheet.visits_imported",id,p.Value.GetProperty("branch_id").GetGuid(),t);return Ok(new{imported=count});
 },t);
 [HttpPut("entries/{id:guid}")]
 public Task<IActionResult> Adjust(Guid id,AdjustTimesheet r,CancellationToken t)=>Tx(async()=>{
  var e=await Entry(id,t);if(e is null)return NotFound();var p=await Period(e.Value.GetProperty("period_id").GetGuid(),t);if(p is null)return NotFound();
  if(!Manage&&!(user.IsCareWorker&&user.CareWorkerId==e.Value.GetProperty("care_worker_id").GetGuid()))return Forbid();
  if(p.Value.GetProperty("status").GetString()!="Open"||e.Value.GetProperty("status").GetString()!="Draft")return Conflict(new{message="Only draft entries in an open period can be adjusted."});
  if(r.ExpectedRevision!=e.Value.GetProperty("revision").GetInt32())return Conflict(new{message="Entry changed. Reload before saving."});
  if(string.IsNullOrWhiteSpace(r.Reason)||r.PayableMinutes<0||r.TravelMinutes<0||r.Mileage<0||r.HourlyRate is <0)return BadRequest(new{message="A reason and nonnegative values are required."});
  if(r.HourlyRate is not null&&!Manage)return Forbid();
  await Exec("update timesheet_entries set payable_minutes=@minutes,travel_minutes=@travel,mileage=@mileage,hourly_rate=coalesce(@rate,hourly_rate),reason=@reason,changed_by=@actor,revision=revision+1,updated_at=now() where id=@id",c=>{Add(c,"id",id);Add(c,"minutes",r.PayableMinutes);Add(c,"travel",r.TravelMinutes);Add(c,"mileage",r.Mileage);Add(c,"rate",r.HourlyRate);Add(c,"reason",r.Reason.Trim());},t);
  await Audit("timesheet.adjusted",id,e.Value.GetProperty("branch_id").GetGuid(),t);return NoContent();
 },t);
 [HttpPost("entries/{id:guid}/transition")]
 public Task<IActionResult> Transition(Guid id,TimesheetTransition r,CancellationToken t)=>Tx(async()=>{
  var e=await Entry(id,t);if(e is null)return NotFound();var p=await Period(e.Value.GetProperty("period_id").GetGuid(),t);if(p is null)return NotFound();
  var own=user.CareWorkerId==e.Value.GetProperty("care_worker_id").GetGuid();
  if(r.Action=="Submit" ? !(Manage||own) : !Manage)return Forbid();
  if(p.Value.GetProperty("status").GetString()!="Open"||r.ExpectedRevision!=e.Value.GetProperty("revision").GetInt32())return Conflict(new{message="Period is locked or entry has changed."});
  var status=e.Value.GetProperty("status").GetString();
  var next=r.Action switch {"Submit" when status=="Draft"=>"Submitted","Approve" when status=="Submitted"=>"Approved","Return" when status is "Submitted" or "Approved"=>"Draft",_=>null};
  if(next is null)return Conflict(new{message="Invalid timesheet transition."});
  if(string.IsNullOrWhiteSpace(r.Reason))return BadRequest(new{message="Transition reason is required."});
  if(next=="Submitted"&&e.Value.GetProperty("payable_minutes").ValueKind==JsonValueKind.Null)return Conflict(new{message="Missing actual time requires a documented adjustment before submission."});
  if(next=="Approved"&&own)return Conflict(new{message="A different manager must approve your timesheet."});
  await Exec("update timesheet_entries set status=@status,reason=@reason,changed_by=@actor,submitted_by=case when @status='Submitted' then @actor when @status='Draft' then null else submitted_by end,approved_by=case when @status='Approved' then @actor else null end,revision=revision+1,updated_at=now() where id=@id",c=>{Add(c,"id",id);Add(c,"status",next);Add(c,"reason",r.Reason.Trim());},t);
  await Audit("timesheet."+r.Action.ToLowerInvariant(),id,e.Value.GetProperty("branch_id").GetGuid(),t);return NoContent();
 },t);
 [HttpGet("entries/{id:guid}/history")]
 public async Task<IActionResult> History(Guid id,CancellationToken t)
 {
  var e=await Entry(id,t);if(e is null)return NotFound();if(!Manage&&user.CareWorkerId!=e.Value.GetProperty("care_worker_id").GetGuid())return Forbid();
  return Ok(await Rows("select snapshot,recorded_at from timesheet_history where entry_id=@id order by recorded_at",c=>Add(c,"id",id),t));
 }
 [HttpPost("periods/{id:guid}/lock"),Authorize(Roles="CareManager,Administrator")]
 public Task<IActionResult> Lock(Guid id,LockTimesheetPeriod r,CancellationToken t)=>Tx(async()=>{
  var p=await Period(id,t);if(p is null)return NotFound();
  if(p.Value.GetProperty("status").GetString()!="Open"||p.Value.GetProperty("revision").GetInt32()!=r.ExpectedRevision)return Conflict(new{message="Period is locked or has changed."});
  if(await Count("select count(*) from timesheet_entries where period_id=@id",c=>Add(c,"id",id),t)==0||await Count("select count(*) from timesheet_entries where period_id=@id and status<>'Approved'",c=>Add(c,"id",id),t)>0)return Conflict(new{message="All entries must be approved before locking a nonempty period."});
  if(await Count("""
   select count(*) from "Visits" v join timesheet_periods p on p.id=@id and v."OrganizationId"=p.organization_id and v."BranchId"=p.branch_id
   where v."Status"='Completed' and v."StartsAt">=p.period_start and v."StartsAt"<p.period_end
   and not exists(select 1 from timesheet_entries e where e.visit_id=v."Id" and e.organization_id=p.organization_id)
   and not exists(select 1 from finance_payroll_lines l where l.visit_id=v."Id" and l.organization_id=p.organization_id)
   """,c=>Add(c,"id",id),t)>0)return Conflict(new{message="Import all completed visits before locking."});
  await Exec("update timesheet_periods set status='Locked',locked_by=@actor,locked_at=now(),revision=revision+1 where id=@id",c=>Add(c,"id",id),t);
  await Audit("timesheet.period_locked",id,p.Value.GetProperty("branch_id").GetGuid(),t);return NoContent();
 },t);
 [HttpPost("periods/{id:guid}/payroll"),Authorize(Roles="Administrator,BackOffice")]
 public Task<IActionResult> Payroll(Guid id,CancellationToken t)=>Tx(async()=>{
  var p=await Period(id,t);if(p is null)return NotFound();if(p.Value.GetProperty("status").GetString()!="Locked"||p.Value.GetProperty("payroll_run_id").ValueKind!=JsonValueKind.Null)return Conflict(new{message="Payroll requires a locked period that has not already generated payroll."});
  if(await Count("select count(*) from timesheet_entries e join finance_payroll_lines l on l.organization_id=e.organization_id and l.visit_id=e.visit_id where e.period_id=@id",c=>Add(c,"id",id),t)>0)return Conflict(new{message="A source visit has already been paid."});
  var entries=await Rows("select * from timesheet_entries where period_id=@id and status='Approved'",c=>Add(c,"id",id),t);
  var run=Guid.NewGuid();decimal total=0;var branch=p.Value.GetProperty("branch_id").GetGuid();var travelRate=p.Value.GetProperty("travel_rate").GetDecimal();var mileageRate=p.Value.GetProperty("mileage_rate").GetDecimal();
  var period=$"{p.Value.GetProperty("period_start").GetDateTimeOffset():yyyyMMdd}-{p.Value.GetProperty("period_end").GetDateTimeOffset():yyyyMMdd}";
  await Exec("insert into \"PayrollRuns\"(\"Id\",\"Period\",\"WorkerCount\",\"GrossPay\",\"Status\",\"CreatedAt\",\"OrganizationId\",\"BranchId\") values(@id,@period,@count,0,'Generated',now(),@org,@target)",c=>{Add(c,"id",run);Add(c,"period",period);Add(c,"count",entries.Select(x=>x.GetProperty("care_worker_id").GetGuid()).Distinct().Count());Add(c,"target",branch);},t);
  foreach(var e in entries)
  {
   var hours=e.GetProperty("payable_minutes").GetDecimal()/60m;var rate=e.GetProperty("hourly_rate").GetDecimal();
   var travel=Math.Round(e.GetProperty("travel_minutes").GetDecimal()/60m*travelRate,2,MidpointRounding.AwayFromZero);
   var mileage=Math.Round(e.GetProperty("mileage").GetDecimal()*mileageRate,2,MidpointRounding.AwayFromZero);
   var gross=Math.Round(hours*rate,2,MidpointRounding.AwayFromZero)+travel+mileage;total+=gross;
   await Exec("insert into finance_payroll_lines(id,payroll_run_id,visit_id,care_worker_id,organization_id,branch_id,description,payable_hours,hourly_rate,mileage_amount,travel_amount,gross_pay,timesheet_entry_id) values(@id,@run,@visit,@worker,@org,@target,'Approved timesheet',@hours,@rate,@mileage,@travel,@gross,@entry)",c=>{Add(c,"id",Guid.NewGuid());Add(c,"run",run);Add(c,"visit",e.GetProperty("visit_id").GetGuid());Add(c,"worker",e.GetProperty("care_worker_id").GetGuid());Add(c,"target",branch);Add(c,"hours",hours);Add(c,"rate",rate);Add(c,"mileage",mileage);Add(c,"travel",travel);Add(c,"gross",gross);Add(c,"entry",e.GetProperty("id").GetGuid());},t);
  }
  await Exec("update \"PayrollRuns\" set \"GrossPay\"=@total where \"Id\"=@run; update timesheet_periods set payroll_run_id=@run where id=@id",c=>{Add(c,"total",total);Add(c,"run",run);Add(c,"id",id);},t);
  await Audit("finance.payroll_batch_generated",run,branch,t);return Created($"/api/phase1/payroll-runs/{run}",new{id=run,period,grossPay=total,status="Generated"});
 },t);
 [NonAction]
 public async Task<IActionResult> PayrollForDates(DateTimeOffset start,DateTimeOffset end,CancellationToken t)
 {
  if(!Finance)return Forbid();var periods=await Rows("select id from timesheet_periods where organization_id=@org and (@wide or branch_id=@branch) and period_start=@start and period_end=@end",c=>{Add(c,"start",start);Add(c,"end",end);},t);
  if(periods.Count!=1)return Conflict(new{message="Select a unique governed pay period and approve and lock its timesheets before payroll generation."});return await Payroll(periods[0].GetProperty("id").GetGuid(),t);
 }
 [HttpGet("/api/phase1/payroll-runs/{id:guid}/export"),Authorize(Roles="Administrator,BackOffice")]
 public Task<IActionResult> Export(Guid id,CancellationToken t)=>Tx(async()=>{
  var periods=await Rows("select p.* from timesheet_periods p join \"PayrollRuns\" r on r.\"Id\"=p.payroll_run_id where p.payroll_run_id=@id and p.organization_id=@org and (@wide or p.branch_id=@branch) and p.status='Locked' and r.\"Status\"='Approved'",c=>Add(c,"id",id),t);
  if(periods.Count!=1)return Conflict(new{message="Export requires an approved payroll run linked to a locked timesheet period."});
  var rows=await Rows("select care_worker_id,visit_id,payable_hours,hourly_rate,travel_amount,mileage_amount,gross_pay from finance_payroll_lines where payroll_run_id=@id and organization_id=@org order by care_worker_id,visit_id",c=>Add(c,"id",id),t);
  var csv=new StringBuilder("worker_id,visit_id,payable_hours,hourly_rate,travel_amount,mileage_amount,gross_pay\n");
  foreach(var row in rows)csv.AppendLine(string.Join(',',row.EnumerateObject().Select(x=>x.Value.ToString())));
  await Audit("payroll.exported",id,periods[0].GetProperty("branch_id").GetGuid(),t);return File(Encoding.UTF8.GetBytes(csv.ToString()),"text/csv",$"payroll-{id}.csv");
 },t);
 private async Task<JsonElement?> Period(Guid id,CancellationToken t)=>(await Rows("select * from timesheet_periods where id=@id and organization_id=@org and (@wide or branch_id=@branch)",c=>Add(c,"id",id),t)).Cast<JsonElement?>().SingleOrDefault();
 private async Task<JsonElement?> Entry(Guid id,CancellationToken t)=>(await Rows("select * from timesheet_entries where id=@id and organization_id=@org and (@wide or branch_id=@branch)",c=>Add(c,"id",id),t)).Cast<JsonElement?>().SingleOrDefault();
 private async Task<IActionResult> Tx(Func<Task<IActionResult>> action,CancellationToken t)
 {
  return await db.Database.CreateExecutionStrategy().ExecuteAsync(async()=>{
   await using var tx=await db.Database.BeginTransactionAsync(t);
   await Exec("select pg_advisory_xact_lock(hashtextextended(@key,0))",c=>Add(c,"key",$"finance:{tenant.OrganizationId}"),t);
   var result=await action();await tx.CommitAsync(t);return result;
  });
 }
 private async Task Audit(string action,Guid id,Guid branch,CancellationToken t){db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),action,user.UserName,"Timesheet",id,DateTimeOffset.UtcNow,tenant.OrganizationId,branch));await db.SaveChangesAsync(t);}
 private async Task<List<JsonElement>> Rows(string sql,Action<DbCommand> bind,CancellationToken t){await using var c=await Command($"select row_to_json(q)::text from ({sql}) q",t);bind(c);var rows=new List<JsonElement>();await using var r=await c.ExecuteReaderAsync(t);while(await r.ReadAsync(t))rows.Add(JsonSerializer.Deserialize<JsonElement>(r.GetString(0)));return rows;}
 private async Task<long> Count(string sql,Action<DbCommand> bind,CancellationToken t){await using var c=await Command(sql,t);bind(c);return Convert.ToInt64(await c.ExecuteScalarAsync(t));}
 private async Task<int> Exec(string sql,Action<DbCommand> bind,CancellationToken t){await using var c=await Command(sql,t);bind(c);return await c.ExecuteNonQueryAsync(t);}
 private async Task<DbCommand> Command(string sql,CancellationToken t){var cn=db.Database.GetDbConnection();if(cn.State!=System.Data.ConnectionState.Open)await cn.OpenAsync(t);var c=cn.CreateCommand();c.Transaction=db.Database.CurrentTransaction?.GetDbTransaction();c.CommandText=sql;Add(c,"org",tenant.OrganizationId);Add(c,"branch",tenant.BranchId??Guid.Empty);Add(c,"wide",tenant.IsOrganizationWide);Add(c,"actor",user.UserId);return c;}
 private static void Add(DbCommand c,string name,object? value){var p=c.CreateParameter();p.ParameterName=name;p.Value=value??DBNull.Value;c.Parameters.Add(p);}
}
public sealed record CreatePayPeriod(Guid BranchId,DateTimeOffset PeriodStart,DateTimeOffset PeriodEnd,decimal? HourlyRate,decimal? TravelRate,decimal? MileageRate);
public sealed record AdjustTimesheet(int ExpectedRevision,decimal PayableMinutes,decimal TravelMinutes,decimal Mileage,decimal? HourlyRate,string Reason);
public sealed record TimesheetTransition(int ExpectedRevision,string Action,string Reason);
public sealed record LockTimesheetPeriod(int ExpectedRevision);
