using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace AiCare.Tests;
[Collection("Postgres regression")]
public sealed class TimesheetGovernanceRegressionTests(PostgresRegressionFactory factory):IClassFixture<PostgresRegressionFactory>
{
 [Fact]
 public async Task ActualTimeAdjustmentsApprovalLocksAndConcurrentPayrollAreGoverned()
 {
  await factory.EnsureClinicalSeedAsync();
  var workerId=Guid.NewGuid();var visitId=Guid.NewGuid();var start=new DateTimeOffset(2035,2,1,0,0,0,TimeSpan.Zero);
  using(var scope=factory.Services.CreateScope())
  {
   var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
   db.CareWorkers.Add(new CareWorker(workerId,"Timesheet worker","Care","Flexible",0,0,"Valid","Compliant","10 miles",TenantDefaults.OrganizationId,TenantDefaults.BranchId));
   db.Visits.Add(new Visit(visitId,RegressionIds.ServiceUserId,workerId,start.AddHours(9),"Care",60,"Care",VisitStatus.Completed,start.AddHours(9),start.AddHours(9).AddMinutes(45),null,null,null,null,TenantDefaults.OrganizationId,TenantDefaults.BranchId));
   await db.SaveChangesAsync();
  }
  var manager=await Staff(UserRole.CareManager,TenantDefaults.BranchId);
  var worker=await Staff(UserRole.CareWorker,TenantDefaults.BranchId,workerId);
  var otherWorker=await Staff(UserRole.CareWorker,TenantDefaults.BranchId,Guid.NewGuid());
  var foreign=await Staff(UserRole.CareManager,Guid.NewGuid());
  var finance=await Staff(UserRole.BackOffice,TenantDefaults.BranchId);
  await StepUpTestGrants.GrantAsync(factory,finance,"export","payroll");
  var request=new{branchId=TenantDefaults.BranchId,periodStart=start,periodEnd=start.AddDays(7),hourlyRate=20m,travelRate=10m,mileageRate=.5m};
  Assert.Equal(HttpStatusCode.Forbidden,(await worker.PostAsJsonAsync("/api/phase1/timesheets/periods",request)).StatusCode);
  var created=await manager.PostAsJsonAsync("/api/phase1/timesheets/periods",request);Assert.Equal(HttpStatusCode.Created,created.StatusCode);
  var id=(await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();var url=$"/api/phase1/timesheets/periods/{id}";
  Assert.Equal(HttpStatusCode.Conflict,(await manager.PostAsJsonAsync("/api/phase1/timesheets/periods",request)).StatusCode);
  Assert.Equal(HttpStatusCode.NotFound,(await foreign.GetAsync(url)).StatusCode);
  Assert.Equal(HttpStatusCode.Conflict,(await finance.PostAsync(url+"/payroll",null)).StatusCode);
  Assert.Equal(HttpStatusCode.Conflict,(await manager.PostAsJsonAsync(url+"/lock",new{expectedRevision=1})).StatusCode);
  (await manager.PostAsync(url+"/import",null)).EnsureSuccessStatusCode();
  Assert.Equal(0,(await (await manager.PostAsync(url+"/import",null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("imported").GetInt32());
  var detail=await manager.GetFromJsonAsync<JsonElement>(url);var entry=detail.GetProperty("entries").EnumerateArray().Single();
  Assert.Equal(45m,entry.GetProperty("actual_minutes").GetDecimal());Assert.Equal(60,entry.GetProperty("planned_minutes").GetInt32());
  var entryId=entry.GetProperty("id").GetGuid();var eurl=$"/api/phase1/timesheets/entries/{entryId}";
  Assert.Empty((await otherWorker.GetFromJsonAsync<JsonElement>(url)).GetProperty("entries").EnumerateArray());
  var adjust=new{expectedRevision=1,payableMinutes=50m,travelMinutes=30m,mileage=4m,reason="Verified additional care and travel"};
  Assert.Equal(HttpStatusCode.Forbidden,(await otherWorker.PutAsJsonAsync(eurl,adjust)).StatusCode);
  Assert.Equal(HttpStatusCode.BadRequest,(await worker.PutAsJsonAsync(eurl,new{expectedRevision=1,payableMinutes=-1m,travelMinutes=0m,mileage=0m,reason="Invalid"})).StatusCode);
  Assert.Equal(HttpStatusCode.NoContent,(await worker.PutAsJsonAsync(eurl,adjust)).StatusCode);
  Assert.Equal(HttpStatusCode.Conflict,(await worker.PutAsJsonAsync(eurl,adjust)).StatusCode);
  Assert.Equal(HttpStatusCode.Conflict,(await manager.PostAsJsonAsync(eurl+"/transition",new{expectedRevision=2,action="Approve",reason="Too early"})).StatusCode);
  Assert.Equal(HttpStatusCode.BadRequest,(await worker.PostAsJsonAsync(eurl+"/transition",new{expectedRevision=2,action="Submit",reason=" "})).StatusCode);
  (await worker.PostAsJsonAsync(eurl+"/transition",new{expectedRevision=2,action="Submit",reason="Evidence checked"})).EnsureSuccessStatusCode();
  Assert.Equal(HttpStatusCode.Forbidden,(await worker.PostAsJsonAsync(eurl+"/transition",new{expectedRevision=3,action="Approve",reason="Self approval"})).StatusCode);
  Assert.Equal(HttpStatusCode.Conflict,(await manager.PostAsJsonAsync(url+"/lock",new{expectedRevision=1})).StatusCode);
  (await manager.PostAsJsonAsync(eurl+"/transition",new{expectedRevision=3,action="Approve",reason="Manager checked evidence"})).EnsureSuccessStatusCode();
  var locks=await Task.WhenAll(manager.PostAsJsonAsync(url+"/lock",new{expectedRevision=1}),manager.PostAsJsonAsync(url+"/lock",new{expectedRevision=1}));
  Assert.Single(locks,x=>x.StatusCode==HttpStatusCode.NoContent);Assert.Single(locks,x=>x.StatusCode==HttpStatusCode.Conflict);
  Assert.Equal(HttpStatusCode.Conflict,(await worker.PutAsJsonAsync(eurl,adjust)).StatusCode);
  Assert.Equal(HttpStatusCode.Conflict,(await manager.PostAsync(url+"/import",null)).StatusCode);
  var payrolls=await Task.WhenAll(finance.PostAsync(url+"/payroll",null),finance.PostAsync(url+"/payroll",null));
  Assert.Single(payrolls,x=>x.StatusCode==HttpStatusCode.Conflict);
  var payroll=await payrolls.Single(x=>x.StatusCode==HttpStatusCode.Created).Content.ReadFromJsonAsync<JsonElement>();
  Assert.Equal(23.67m,payroll.GetProperty("grossPay").GetDecimal());var run=payroll.GetProperty("id").GetGuid();
  Assert.Equal(HttpStatusCode.OK,(await manager.GetAsync(url)).StatusCode);
  var history=await manager.GetFromJsonAsync<JsonElement>(eurl+"/history");Assert.Equal(4,history.GetArrayLength());
  Assert.Equal(45m,history[1].GetProperty("snapshot").GetProperty("actual_minutes").GetDecimal());
  var export=$"/api/phase1/payroll-runs/{run}/export";
  Assert.Equal(HttpStatusCode.Conflict,(await finance.GetAsync(export)).StatusCode);
  (await finance.PostAsync($"/api/phase1/payroll-runs/{run}/approve",null)).EnsureSuccessStatusCode();
  await StepUpTestGrants.GrantAsync(factory,finance,"export");
  Assert.Contains("23.67",await finance.GetStringAsync(export));
  using var verify=factory.Services.CreateScope();var verifyDb=verify.ServiceProvider.GetRequiredService<CareDbContext>();
  Assert.Equal(1,await verifyDb.AuditEvents.CountAsync(x=>x.EntityId==run&&x.Action=="finance.payroll_batch_generated"));
  await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>verifyDb.Database.ExecuteSqlRawAsync("update timesheet_entries set payable_minutes=1 where id={0}",entryId));
  await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>verifyDb.Database.ExecuteSqlRawAsync("delete from timesheet_history where entry_id={0}",entryId));
 }
 private async Task<HttpClient> Staff(UserRole role,Guid branch,Guid? worker=null)
 {
  var name=$"timesheet.{Guid.NewGuid():N}";
  using(var scope=factory.Services.CreateScope()) {var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();db.AppUsers.Add(new AppUser(Guid.NewGuid(),name,name+"@aicare.local",PasswordHasher.HashPassword("Admin123!"),role,true,TenantDefaults.OrganizationId,branch,worker,null));await db.SaveChangesAsync();}
  var client=factory.CreateClient();var response=await client.PostAsJsonAsync("/api/auth/login",new{userName=name,password="Admin123!",mfaCode=(string?)null});response.EnsureSuccessStatusCode();client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString());return client;
 }
}
