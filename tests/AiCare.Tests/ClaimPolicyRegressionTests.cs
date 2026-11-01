using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AiCare.Api;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiCare.Tests;
[Collection("Postgres regression")]
public sealed class ClaimPolicyRegressionTests(PostgresRegressionFactory factory):IClassFixture<PostgresRegressionFactory>
{
 [Fact]
 public async Task ApprovedPoliciesControlRatesEvidenceScopeRetriesAndIndependentApproval()
 {
  await factory.EnsureClinicalSeedAsync();var org=Guid.NewGuid();var branch=Guid.NewGuid();var worker=Guid.NewGuid();var person=Guid.NewGuid();var visit=Guid.NewGuid();var prefix=$"claims.{Guid.NewGuid():N}";
  using(var scope=factory.Services.CreateScope())
  {
   var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();db.Organizations.Add(new Organization(org,"Claims test","Trial","Active"));db.Branches.Add(new Branch(branch,org,"Main","Test","Active"));db.CareWorkers.Add(new CareWorker(worker,"Claim worker","Care","Flexible",0,0,"Valid","Compliant","10",org,branch));
   db.ServiceUsers.Add(new ServiceUser(person,"Claim person",new DateOnly(1950,1,1),"1","Care","Contact","",RiskLevel.Low,"Active","Address","None","None","Private","","","Independent","Independent","Verbal","Routine","Standard",org,branch));db.Visits.Add(new Visit(visit,person,worker,new DateTimeOffset(2052,1,10,10,0,0,TimeSpan.Zero),"Care",60,"Care",VisitStatus.Completed,null,null,null,null,null,null,org,branch));
   foreach(var suffix in new[]{"author","reviewer"})db.AppUsers.Add(new AppUser(Guid.NewGuid(),prefix+suffix,prefix+suffix+"@test.invalid",PasswordHasher.HashPassword("Admin123!"),UserRole.BackOffice,true,org,branch));
   db.AppUsers.Add(new AppUser(Guid.NewGuid(),prefix+"worker",prefix+"worker@test.invalid",PasswordHasher.HashPassword("Admin123!"),UserRole.CareWorker,true,org,branch,worker));await db.SaveChangesAsync();
  }
  var author=await Login(prefix+"author");var reviewer=await Login(prefix+"reviewer");var staff=await Login(prefix+"worker");
  var policyResponse=await author.PostAsJsonAsync(Root+"/policies",new{branchId=branch,reference="EXPLICIT-TEST-MILEAGE",claimType="Mileage",category="Business trip",validFrom="2052-01-01",validTo="2052-01-31",currency="GBP",unitRate=0.5m,maximumAmount=10m,evidenceRequired=true,visitRequired=true,roundingMode="AwayFromZero"});policyResponse.EnsureSuccessStatusCode();var policy=(await policyResponse.Content.ReadFromJsonAsync<Id>())!.Value;
  Assert.Equal(HttpStatusCode.Conflict,(await author.PostAsync($"{Root}/policies/{policy}/approve",null)).StatusCode);
  (await reviewer.PostAsync($"{Root}/policies/{policy}/approve",null)).EnsureSuccessStatusCode();
  using var evidenceForm = new MultipartFormDataContent();
  evidenceForm.Add(new StringContent(worker.ToString()), "careWorkerId");
  using var evidenceFile = new ByteArrayContent(FinanceExportDocument.Pdf("Trip evidence", ["Reference"], [["TRIP-1"]]));
  evidenceFile.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
  evidenceForm.Add(evidenceFile, "file", "trip-evidence.pdf");
  var upload = await author.PostAsync(Root + "/evidence", evidenceForm);
  upload.EnsureSuccessStatusCode();
  var evidenceId = (await upload.Content.ReadFromJsonAsync<Id>())!.Value;
  var request=new{careWorkerId=worker,visitId=visit,policyId=policy,claimType="Mileage",category="Business trip",claimDate="2052-01-10",quantity=4m,unitRate=999m,sourceReference="TRIP-1",evidenceId,evidenceReference="Approved trip evidence",notes="Business trip"};
  Assert.Equal(HttpStatusCode.Conflict,(await author.PostAsJsonAsync(Root,new{careWorkerId=worker,visitId=visit,policyId=policy,claimType="Mileage",category="Business trip",claimDate="2052-01-10",quantity=4m,unitRate=999m,sourceReference="TRIP-MISSING-EVIDENCE",notes="Business trip"})).StatusCode);
  var submissions=await Task.WhenAll(author.PostAsJsonAsync(Root,request),author.PostAsJsonAsync(Root,request));Assert.All(submissions,r=>r.EnsureSuccessStatusCode());Assert.Equal(1,submissions.Count(r=>r.StatusCode==HttpStatusCode.Created));
  var claim=(await submissions.Single(r=>r.StatusCode==HttpStatusCode.Created).Content.ReadFromJsonAsync<Claim>())!;Assert.Equal(2m,claim.Amount);
  Assert.Equal(HttpStatusCode.Conflict,(await author.PostAsJsonAsync($"{Root}/{claim.Id}/review",new{decision="Approve",reason="Self review"})).StatusCode);
  (await reviewer.PostAsJsonAsync($"{Root}/{claim.Id}/review",new{decision="Approve",reason="Independent evidence check"})).EnsureSuccessStatusCode();
  var payrollRoot="/api/phase1/finance/payroll-ready";var contractId=Guid.NewGuid();
  var contract=new PayrollExportContractRequest(contractId,branch,"TEST-PAYROLL-CONTRACT","GBP",new(2052,1,1),new(2052,1,31),new(){{worker.ToString(),"TEST-WORKER-001"}},new(){["Worked"]="TEST-WORK",["Travel"]="TEST-TRAVEL",["TimesheetMileage"]="TEST-TM",["ClaimMileage"]="TEST-CM",["ClaimExpense"]="TEST-EXP",["Leave"]="TEST-LEAVE",["Adjustment"]="TEST-ADJ"});
  (await author.PostAsJsonAsync(payrollRoot+"/contracts",contract)).EnsureSuccessStatusCode();Assert.Equal(HttpStatusCode.Conflict,(await author.PostAsync($"{payrollRoot}/contracts/{contractId}/approve",null)).StatusCode);(await reviewer.PostAsync($"{payrollRoot}/contracts/{contractId}/approve",null)).EnsureSuccessStatusCode();
  var absence=Guid.NewGuid();using(var leaveScope=factory.Services.CreateScope()){var leaveDb=leaveScope.ServiceProvider.GetRequiredService<CareDbContext>();await leaveDb.Database.ExecuteSqlRawAsync("insert into worker_absences(id,care_worker_id,organization_id,branch_id,absence_type,starts_at,ends_at,status) values({0},{1},{2},{3},'Annual leave','2052-01-11T00:00:00Z','2052-01-12T00:00:00Z','Approved')",absence,worker,org,branch);}
  var leave=new PayrollExtraRequest(Guid.NewGuid(),branch,worker,"Leave",new(2052,1,11),"GBP",2,10,20,absence,null,"SYNTHETIC-LEAVE-PAY","Two explicitly approved paid hours, not the full absence duration","ToEven");
  Assert.Equal(HttpStatusCode.Conflict,(await author.PostAsJsonAsync(payrollRoot+"/extras",leave with{RequestId=Guid.NewGuid(),Amount=21,Reference="INCONSISTENT-LEAVE"})).StatusCode);(await author.PostAsJsonAsync(payrollRoot+"/extras",leave)).EnsureSuccessStatusCode();Assert.Equal(HttpStatusCode.Conflict,(await author.PostAsync($"{payrollRoot}/extras/{leave.RequestId}/approve",null)).StatusCode);(await reviewer.PostAsync($"{payrollRoot}/extras/{leave.RequestId}/approve",null)).EnsureSuccessStatusCode();
  var batch=new PayrollReadyBatchRequest(Guid.NewGuid(),contractId,new(2052,1,1),new(2052,1,31),"TEST-CLAIM-BATCH",[],[claim.Id],[leave.RequestId],null);
  var attempts=await Task.WhenAll(author.PostAsJsonAsync(payrollRoot+"/batches",batch),author.PostAsJsonAsync(payrollRoot+"/batches",batch));Assert.All(attempts,r=>r.EnsureSuccessStatusCode());Assert.Single(attempts,r=>r.StatusCode==HttpStatusCode.Created);
  Assert.Equal(HttpStatusCode.Conflict,(await author.PostAsJsonAsync(payrollRoot+"/batches",batch with{RequestId=Guid.NewGuid(),Reference="DUPLICATE-SOURCE"})).StatusCode);
  Assert.Equal(HttpStatusCode.Conflict,(await author.PostAsJsonAsync($"{payrollRoot}/batches/{batch.RequestId}/confirm-payment",new{reference="EARLY"})).StatusCode);
  Assert.Equal(HttpStatusCode.Conflict,(await author.PostAsJsonAsync($"{payrollRoot}/batches/{batch.RequestId}/review",new{decision="Approve",reason="Self review"})).StatusCode);
  (await reviewer.PostAsJsonAsync($"{payrollRoot}/batches/{batch.RequestId}/review",new{decision="Reject",reason="Release pending source for corrected batch reference"})).EnsureSuccessStatusCode();
  var replacement=batch with{RequestId=Guid.NewGuid(),Reference="REPLACEMENT-CLAIM-BATCH"};(await author.PostAsJsonAsync(payrollRoot+"/batches",replacement)).EnsureSuccessStatusCode();(await reviewer.PostAsJsonAsync($"{payrollRoot}/batches/{replacement.RequestId}/review",new{decision="Approve",reason="Independent source reconciliation"})).EnsureSuccessStatusCode();
  Assert.Equal(HttpStatusCode.Forbidden,(await author.GetAsync($"{payrollRoot}/batches/{replacement.RequestId}/export")).StatusCode);
  var confirmations=await Task.WhenAll(author.PostAsJsonAsync($"{payrollRoot}/batches/{replacement.RequestId}/confirm-payment",new{reference="BANK-CONFIRMATION-1"}),author.PostAsJsonAsync($"{payrollRoot}/batches/{replacement.RequestId}/confirm-payment",new{reference="BANK-CONFIRMATION-1"}));Assert.All(confirmations,r=>r.EnsureSuccessStatusCode());
  Assert.Equal(HttpStatusCode.Conflict,(await author.PostAsJsonAsync($"{payrollRoot}/batches/{replacement.RequestId}/confirm-payment",new{reference="OTHER-PAYMENT"})).StatusCode);
  Assert.Equal(HttpStatusCode.Forbidden,(await staff.PostAsync($"{Root}/policies/{policy}/approve",null)).StatusCode);
  using var verify=factory.Services.CreateScope();var verifyDb=verify.ServiceProvider.GetRequiredService<CareDbContext>();
  Assert.Equal(0.5m,await verifyDb.Database.SqlQueryRaw<decimal>("select unit_rate as \"Value\" from finance_workforce_claims where id={0}",claim.Id).SingleAsync());
  Assert.Equal("Paid",await verifyDb.Database.SqlQueryRaw<string>("select status as \"Value\" from finance_workforce_claims where id={0}",claim.Id).SingleAsync());
  Assert.Equal(1,await verifyDb.Database.SqlQueryRaw<int>("select count(*)::int as \"Value\" from finance_payroll_batch_lines where source_id={0} and not released",claim.Id).SingleAsync());
  Assert.Equal(22m,await verifyDb.Database.SqlQueryRaw<decimal>("select sum(amount) as \"Value\" from finance_payroll_batch_lines where batch_id={0}",replacement.RequestId).SingleAsync());
  await Assert.ThrowsAnyAsync<Exception>(()=>verifyDb.Database.ExecuteSqlRawAsync("update finance_claim_policies set unit_rate=1 where id={0}",policy));
  await Assert.ThrowsAnyAsync<Exception>(()=>verifyDb.Database.ExecuteSqlRawAsync("update finance_workforce_claims set amount=3 where id={0}",claim.Id));
  var period=Guid.NewGuid();
  await verifyDb.Database.ExecuteSqlRawAsync("insert into timesheet_periods(id,organization_id,branch_id,period_start,period_end,hourly_rate,travel_rate,mileage_rate,created_by) values({0},{1},{2},'2052-01-01','2052-02-01',0,0,0,{3})",period,org,branch,Guid.NewGuid());
  await Assert.ThrowsAnyAsync<Exception>(()=>verifyDb.Database.ExecuteSqlRawAsync("insert into timesheet_entries(id,period_id,organization_id,branch_id,visit_id,care_worker_id,planned_minutes,payable_minutes,hourly_rate,mileage,reason,changed_by) values({0},{1},{2},{3},{4},{5},60,60,0,4,'Duplicate mileage',{6})",Guid.NewGuid(),period,org,branch,visit,worker,Guid.NewGuid()));
 }
 const string Root="/api/phase1/finance/workforce-claims";
 async Task<HttpClient> Login(string name){var c=factory.CreateClient();var r=await c.PostAsJsonAsync("/api/auth/login",new{userName=name,password="Admin123!",mfaCode=(string?)null});r.EnsureSuccessStatusCode();c.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await r.Content.ReadFromJsonAsync<Token>())!.Value);return c;}
 sealed record Id([property:System.Text.Json.Serialization.JsonPropertyName("id")]Guid Value);
 sealed record Token([property:System.Text.Json.Serialization.JsonPropertyName("token")]string Value);
 sealed record Claim(Guid Id,decimal Amount);
}
