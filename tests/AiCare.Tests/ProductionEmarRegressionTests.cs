using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed class ProductionEmarRegressionTests(PostgresRegressionFactory factory):IClassFixture<PostgresRegressionFactory>
{
 [Fact]
 public async Task PrnWitnessStockOmissionEscalationAndImmutableHistoryAreEnforced()
 {
  await factory.EnsureClinicalSeedAsync();var person=Guid.NewGuid();var workerId=Guid.NewGuid();var medication=Guid.NewGuid();var first=Guid.NewGuid();var second=Guid.NewGuid();var omitted=Guid.NewGuid();var held=Guid.NewGuid();var notRequired=Guid.NewGuid();var workerName=$"emar.worker.{Guid.NewGuid():N}";Guid adminId;
  using(var scope=factory.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();adminId=(await db.AppUsers.SingleAsync(x=>x.UserName=="admin")).Id;db.ServiceUsers.Add(new ServiceUser(person,"eMAR Person",new DateOnly(1950,1,1),"+10000000009","Medication support","Contact","",RiskLevel.Medium,"Active","Address","None","None","Private","Regression","","Independent","Independent","Verbal","None","Standard",TenantDefaults.OrganizationId,TenantDefaults.BranchId));db.CareWorkers.Add(new CareWorker(workerId,"eMAR Worker","Medication administration","Available",1,20,"Valid","Compliant","10 miles",TenantDefaults.OrganizationId,TenantDefaults.BranchId));db.AppUsers.Add(new AppUser(Guid.NewGuid(),workerName,$"{workerName}@aicare.local",PasswordHasher.HashPassword("Worker123!"),UserRole.CareWorker,true,TenantDefaults.OrganizationId,TenantDefaults.BranchId,workerId,null));db.Medications.Add(new Medication(medication,person,"Morphine solution","2.5 mg","Oral","PRN",true,"Pharmacy","None",TenantDefaults.OrganizationId,TenantDefaults.BranchId));db.MedicationAdministrationRecords.AddRange(Mar(first,medication,workerId),Mar(second,medication,workerId),Mar(omitted,medication,workerId),Mar(held,medication,workerId),Mar(notRequired,medication,workerId));await db.SaveChangesAsync();await db.Database.ExecuteSqlInterpolatedAsync($"""insert into worker_competency_records(id,care_worker_id,organization_id,branch_id,competency,level,status,assessed_by,assessed_at,expires_at,notes,created_at) values({Guid.NewGuid()},{workerId},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},'Medication administration','Competent','Valid','Regression',{DateTimeOffset.UtcNow.AddDays(-1)},{DateTimeOffset.UtcNow.AddYears(1)},'Route gate',now())""");}
  var admin=await Client("admin","Admin123!");var worker=await Client(workerName,"Worker123!");
  Assert.Equal(HttpStatusCode.OK,(await admin.PutAsJsonAsync($"/api/phase1/service-users/{person}/allergies/status",new{status="NoKnownAllergies",source="Clinical record reviewed by test medication lead"})).StatusCode);
  await StepUpTestGrants.GrantAsync(factory,admin,"medication");
  var profile=await MedicationVerificationTestHelper.SaveAndVerifyAsync(factory, admin, medication, new{indication="Severe breakthrough pain",prescriber="Dr Safety",form="Solution",strength="10mg/5ml",startDate=DateTimeOffset.UtcNow.AddDays(-1),endDate=DateTimeOffset.UtcNow.AddMonths(1),doseWindowMinutes=60,maxPrnDoses24h=4,minPrnIntervalMinutes=240,prnIndication="Pain score 7 or above",prnEffectReviewMinutes=30,stockOnHand=5m,reorderLevel=2m,requiresWitness=true,lastReconciledAt=DateTimeOffset.UtcNow,reconciledBy="Medication lead",reconciliationStatus="Verified",sourceType="Prescription",sourceReference="RX-REG-1",changeReason="Initial verified reconciliation"});Assert.Equal(HttpStatusCode.OK,profile.StatusCode);
  await StepUpTestGrants.GrantAsync(factory,worker,"medication");
  worker.DefaultRequestHeaders.Remove("Idempotency-Key");worker.DefaultRequestHeaders.Add("Idempotency-Key",$"emar-{Guid.NewGuid():N}");var selfWitness=await worker.PostAsJsonAsync($"/api/phase1/emar-safety/mar/{first}/record",new{outcome="Administered",occurredAt=DateTimeOffset.UtcNow,doseQuantity=1m,reasonCode="",reasonDetail="",prnIndication="Pain score 8",witnessUserId=(Guid?)null,overrideReason=""});Assert.Equal(HttpStatusCode.BadRequest,selfWitness.StatusCode);Assert.Contains("witness",await selfWitness.Content.ReadAsStringAsync(),StringComparison.OrdinalIgnoreCase);
  await StepUpTestGrants.GrantAsync(factory,worker,"medication");
  worker.DefaultRequestHeaders.Remove("Idempotency-Key");worker.DefaultRequestHeaders.Add("Idempotency-Key",$"emar-{Guid.NewGuid():N}");var administered=await worker.PostAsJsonAsync($"/api/phase1/emar-safety/mar/{first}/record",new{outcome="Administered",occurredAt=DateTimeOffset.UtcNow,doseQuantity=1m,reasonCode="",reasonDetail="",prnIndication="Pain score 8",witnessUserId=adminId,overrideReason=""});Assert.True(administered.StatusCode==HttpStatusCode.Created,$"Administration returned {administered.StatusCode}: {await administered.Content.ReadAsStringAsync()}");var ledgerId=(await administered.Content.ReadFromJsonAsync<Created>())!.Id;
  await StepUpTestGrants.GrantAsync(factory,worker,"medication");
  worker.DefaultRequestHeaders.Remove("Idempotency-Key");worker.DefaultRequestHeaders.Add("Idempotency-Key",$"emar-{Guid.NewGuid():N}");var tooSoon=await worker.PostAsJsonAsync($"/api/phase1/emar-safety/mar/{second}/record",new{outcome="Administered",occurredAt=DateTimeOffset.UtcNow.AddMinutes(1),doseQuantity=1m,reasonCode="",reasonDetail="",prnIndication="Pain persists",witnessUserId=adminId,overrideReason=""});Assert.Equal(HttpStatusCode.Conflict,tooSoon.StatusCode);Assert.Contains("interval",await tooSoon.Content.ReadAsStringAsync(),StringComparison.OrdinalIgnoreCase);
  await StepUpTestGrants.GrantAsync(factory,worker,"medication");
  worker.DefaultRequestHeaders.Remove("Idempotency-Key");worker.DefaultRequestHeaders.Add("Idempotency-Key",$"emar-{Guid.NewGuid():N}");var omission=await worker.PostAsJsonAsync($"/api/phase1/emar-safety/mar/{omitted}/record",new{outcome="Omitted",occurredAt=DateTimeOffset.UtcNow,doseQuantity=0m,reasonCode="NotAvailable",reasonDetail="Supply not delivered",prnIndication="",witnessUserId=adminId,overrideReason=""});Assert.Equal(HttpStatusCode.Created,omission.StatusCode);
  await StepUpTestGrants.GrantAsync(factory,worker,"medication");
  worker.DefaultRequestHeaders.Remove("Idempotency-Key");worker.DefaultRequestHeaders.Add("Idempotency-Key",$"emar-{Guid.NewGuid():N}");
  var hold=await worker.PostAsJsonAsync($"/api/phase1/emar-safety/mar/{held}/record",new{outcome="Held",occurredAt=DateTimeOffset.UtcNow,doseQuantity=0m,reasonCode="ClinicalHold",reasonDetail="Prescriber advised clinical hold",prnIndication="",witnessUserId=(Guid?)null,overrideReason=""});
  Assert.Equal(HttpStatusCode.Created,hold.StatusCode);
  await StepUpTestGrants.GrantAsync(factory,worker,"medication");
  worker.DefaultRequestHeaders.Remove("Idempotency-Key");worker.DefaultRequestHeaders.Add("Idempotency-Key",$"emar-{Guid.NewGuid():N}");
  var noNeed=await worker.PostAsJsonAsync($"/api/phase1/emar-safety/mar/{notRequired}/record",new{outcome="NotRequired",occurredAt=DateTimeOffset.UtcNow,doseQuantity=0m,reasonCode="NotRequired",reasonDetail="No breakthrough pain reported",prnIndication="",witnessUserId=(Guid?)null,overrideReason=""});
  Assert.Equal(HttpStatusCode.Created,noNeed.StatusCode);
  Assert.Contains("\"eventType\":\"Held\"",await admin.GetStringAsync($"/api/phase1/emar-safety/mar/{held}/history"));
  Assert.Contains("\"eventType\":\"NotRequired\"",await admin.GetStringAsync($"/api/phase1/emar-safety/mar/{notRequired}/history"));
  using(var outcomeScope=factory.Services.CreateScope())
  {
   var outcomeDb=outcomeScope.ServiceProvider.GetRequiredService<CareDbContext>();
   Assert.Equal("Held",(await outcomeDb.MedicationAdministrationRecords.SingleAsync(x=>x.Id==held)).Outcome);
   Assert.Equal("NotRequired",(await outcomeDb.MedicationAdministrationRecords.SingleAsync(x=>x.Id==notRequired)).Outcome);
  }
  var history=await admin.GetStringAsync($"/api/phase1/emar-safety/mar/{first}/history");Assert.Contains("PRNEffectReview",history);Assert.Contains("stockAfter\":4",history);Assert.Contains("admin",history);
  var omissionHistory=await admin.GetStringAsync($"/api/phase1/emar-safety/mar/{omitted}/history");Assert.Contains("MissedDose",omissionHistory);Assert.Contains("Supply not delivered",omissionHistory);
  var effect=await worker.PostAsJsonAsync($"/api/phase1/emar-safety/mar/{first}/prn-effect",new{effect="Pain reduced from 8 to 3",observedAt=DateTimeOffset.UtcNow.AddMinutes(30)});Assert.Equal(HttpStatusCode.Created,effect.StatusCode);history=await admin.GetStringAsync($"/api/phase1/emar-safety/mar/{first}/history");Assert.Contains("Pain reduced from 8 to 3",history);Assert.Contains("Resolved",history);
  await StepUpTestGrants.GrantAsync(factory,admin,"medication");
  var missingWasteWitness=await admin.PostAsJsonAsync($"/api/phase1/emar-safety/medications/{medication}/stock",new{transactionType="Waste",quantity=1m,reference="W-1",reason="Spillage",witnessUserId=(Guid?)null});Assert.Equal(HttpStatusCode.BadRequest,missingWasteWitness.StatusCode);
  Guid witnessId;Guid escalationId;using(var operationsScope=factory.Services.CreateScope()){var operationsDb=operationsScope.ServiceProvider.GetRequiredService<CareDbContext>();witnessId=(await operationsDb.AppUsers.SingleAsync(x=>x.UserName==workerName)).Id;var connection=operationsDb.Database.GetDbConnection();await connection.OpenAsync();await using var command=connection.CreateCommand();command.CommandText="select id from emar_escalations where mar_record_id=@mar and escalation_type='MissedDose'";command.Parameters.Add(new NpgsqlParameter("mar",omitted));escalationId=(Guid)(await command.ExecuteScalarAsync())!;}
  await StepUpTestGrants.GrantAsync(factory,admin,"medication");
  var receipt=await admin.PostAsJsonAsync($"/api/phase1/emar-safety/medications/{medication}/stock",new{transactionType="Receipt",quantity=3m,reference="DEL-1",reason="Pharmacy delivery",witnessUserId=witnessId});Assert.Equal(HttpStatusCode.Created,receipt.StatusCode);Assert.Contains("balanceAfter",await receipt.Content.ReadAsStringAsync());
  await StepUpTestGrants.GrantAsync(factory,admin,"medication");
  var correction=await admin.PostAsJsonAsync($"/api/phase1/emar-safety/mar/{first}/corrections",new{correctsLedgerId=ledgerId,correctedOutcome="Administered - note corrected",reason="Original narrative omitted route confirmation"});Assert.Equal(HttpStatusCode.Created,correction.StatusCode);history=await admin.GetStringAsync($"/api/phase1/emar-safety/mar/{first}/history");Assert.Contains("Correction",history);Assert.Contains("Original narrative omitted route confirmation",history);
  Assert.Equal(HttpStatusCode.NoContent,(await admin.PostAsJsonAsync($"/api/phase1/emar-safety/escalations/{escalationId}/progress",new{action="Acknowledge",comment="Medication lead contacted"})).StatusCode);Assert.Equal(HttpStatusCode.NoContent,(await admin.PostAsJsonAsync($"/api/phase1/emar-safety/escalations/{escalationId}/progress",new{action="Resolve",comment="Replacement supply received"})).StatusCode);
  using var verify=factory.Services.CreateScope();var verifyDb=verify.ServiceProvider.GetRequiredService<CareDbContext>();var error=await Assert.ThrowsAsync<PostgresException>(()=>verifyDb.Database.ExecuteSqlRawAsync("update emar_ledger set reason_detail='changed' where id={0}",ledgerId));Assert.Contains("immutable",error.MessageText,StringComparison.OrdinalIgnoreCase);Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="emar.governed_outcome_recorded"&&x.EntityId==first));
 }
 [Fact]
 public async Task ManagerOverrideRequiresEvidenceRejectsCareWorkerAndPreservesEscalationHistory()
 {
  await factory.EnsureClinicalSeedAsync();
  var medication=Guid.NewGuid();var marId=Guid.NewGuid();var workerName=$"override.worker.{Guid.NewGuid():N}";
  using(var scope=factory.Services.CreateScope())
  {
   var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
   db.AppUsers.Add(new AppUser(Guid.NewGuid(),workerName,$"{workerName}@aicare.local",PasswordHasher.HashPassword("Worker123!"),UserRole.CareWorker,true,TenantDefaults.OrganizationId,TenantDefaults.BranchId,RegressionIds.WorkerId,null));
   db.Medications.Add(new Medication(medication,RegressionIds.ServiceUserId,$"Override medicine {medication:N}","1 tablet","Oral","Morning",false,"Pharmacy","None",TenantDefaults.OrganizationId,TenantDefaults.BranchId));
   db.MedicationAdministrationRecords.Add(new MedicationAdministrationRecord(marId,medication,RegressionIds.VisitId,RegressionIds.WorkerId,DateTimeOffset.UtcNow.AddHours(-3),null,"Scheduled","",TenantDefaults.OrganizationId,TenantDefaults.BranchId));
   await db.SaveChangesAsync();
   await db.Database.ExecuteSqlInterpolatedAsync($"""insert into worker_competency_records(id,care_worker_id,organization_id,branch_id,competency,level,status,assessed_by,assessed_at,expires_at,notes,created_at) values({Guid.NewGuid()},{RegressionIds.WorkerId},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},'Medication administration','Competent','Valid','Regression',{DateTimeOffset.UtcNow.AddDays(-1)},{DateTimeOffset.UtcNow.AddYears(1)},'Override regression',now())""");
  }
  var admin=await Client("admin","Admin123!");var worker=await Client(workerName,"Worker123!");
  using(var status=JsonDocument.Parse(await admin.GetStringAsync("/api/phase1/emar-safety/decision-support/status")))
  {
   Assert.Equal("Disabled",status.RootElement.GetProperty("mode").GetString());
   Assert.False(status.RootElement.GetProperty("comprehensiveScreeningAvailable").GetBoolean());
  }
  Assert.Equal(HttpStatusCode.OK,(await admin.PutAsJsonAsync($"/api/phase1/service-users/{RegressionIds.ServiceUserId}/allergies/status",new{status="NoKnownAllergies",source="Clinical record reviewed by test medication lead"})).StatusCode);
  await StepUpTestGrants.GrantAsync(factory,admin,"medication");
  var profile=await MedicationVerificationTestHelper.SaveAndVerifyAsync(factory,admin,medication,new{indication="Regression",prescriber="Dr Safety",form="Tablet",strength="1 tablet",startDate=DateTimeOffset.UtcNow.AddDays(-1),doseWindowMinutes=30,stockOnHand=10m,reorderLevel=2m,requiresWitness=false,reconciliationStatus="Verified",sourceType="Prescription",sourceReference="RX-OVERRIDE",changeReason="Initial verification"});
  Assert.Equal(HttpStatusCode.OK,profile.StatusCode);
  async Task<HttpResponseMessage> Record(HttpClient client,string? evidence,string? authoriser)
  {
   client.DefaultRequestHeaders.Remove("Idempotency-Key");client.DefaultRequestHeaders.Add("Idempotency-Key",Guid.NewGuid().ToString("N"));
   return await client.PostAsJsonAsync($"/api/phase1/emar-safety/mar/{marId}/record",new{outcome="Administered",occurredAt=DateTimeOffset.UtcNow,doseQuantity=1m,reasonCode="",reasonDetail="",prnIndication="",witnessUserId=(Guid?)null,overrideReason="Clinical instruction permits late administration",overrideEvidenceReference=evidence,overrideAuthoriser=authoriser});
  }
  await StepUpTestGrants.GrantAsync(factory,admin,"medication");
  var missing=await Record(admin,null,null);Assert.Equal(HttpStatusCode.BadRequest,missing.StatusCode);
  Assert.Contains("evidence",await missing.Content.ReadAsStringAsync(),StringComparison.OrdinalIgnoreCase);
  await StepUpTestGrants.GrantAsync(factory,worker,"medication");
  Assert.Equal(HttpStatusCode.Forbidden,(await Record(worker,"RX-OVERRIDE-1","Dr Safety, prescriber")).StatusCode);
  await StepUpTestGrants.GrantAsync(factory,admin,"medication");
  var approved=await Record(admin,"RX-OVERRIDE-1","Dr Safety, prescriber");
  Assert.Equal(HttpStatusCode.Created,approved.StatusCode);
  using var history=JsonDocument.Parse(await admin.GetStringAsync($"/api/phase1/emar-safety/mar/{marId}/history"));
  var entry=history.RootElement.GetProperty("entries")[0];
  Assert.Equal("Clinical instruction permits late administration",entry.GetProperty("overrideReason").GetString());
  Assert.Equal("RX-OVERRIDE-1",entry.GetProperty("overrideEvidenceReference").GetString());
  Assert.Equal("Dr Safety, prescriber",entry.GetProperty("overrideAuthoriser").GetString());
  var escalation=history.RootElement.GetProperty("escalations").EnumerateArray().Single(x=>x.GetProperty("escalationType").GetString()=="LateDose");
  var escalationId=escalation.GetProperty("id").GetGuid();
  foreach(var action in new[]{"Acknowledge","Start","Resolve"})
   Assert.Equal(HttpStatusCode.NoContent,(await admin.PostAsJsonAsync($"/api/phase1/emar-safety/escalations/{escalationId}/progress",new{action,comment=$"Evidence for {action}"})).StatusCode);
  var resolved=await admin.GetStringAsync($"/api/phase1/emar-safety/mar/{marId}/history");
  Assert.Contains("\"status\":\"Resolved\"",resolved);
  using var verify=factory.Services.CreateScope();var dbVerify=verify.ServiceProvider.GetRequiredService<CareDbContext>();
  var immutable=await Assert.ThrowsAsync<PostgresException>(()=>dbVerify.Database.ExecuteSqlRawAsync("update emar_ledger set override_evidence_reference='changed' where mar_record_id={0}",marId));
  Assert.Contains("immutable",immutable.MessageText,StringComparison.OrdinalIgnoreCase);
  Assert.True(await dbVerify.AuditEvents.AnyAsync(x=>x.Action=="emar.manager_override_recorded"&&x.EntityId==marId));
 }

 [Fact]
 public async Task AdministrationRoutesStayLockedWhenClinicalSafetyGateIsDisabled()
 {
  await factory.EnsureClinicalSeedAsync();
  using var gatedFactory=factory.WithWebHostBuilder(builder=>builder.ConfigureAppConfiguration(configuration=>configuration.AddInMemoryCollection(new Dictionary<string,string?>
  {
   ["MedicationSafety:EmarProductionEnabled"]="false"
  })));
  var client=gatedFactory.CreateClient();
  var login=await client.PostAsJsonAsync("/api/auth/login",new{userName="admin",password="Admin123!",mfaCode=(string?)null});
  login.EnsureSuccessStatusCode();
  client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await login.Content.ReadFromJsonAsync<Login>())!.Token);

  await StepUpTestGrants.GrantAsync(factory,client,"medication");
  var governed=await client.PostAsJsonAsync($"/api/phase1/emar-safety/mar/{Guid.NewGuid()}/record",new{outcome="Administered",occurredAt=DateTimeOffset.UtcNow,doseQuantity=1m,reasonCode="",reasonDetail="",prnIndication="",witnessUserId=(Guid?)null,overrideReason=""});
  Assert.Equal(HttpStatusCode.Locked,governed.StatusCode);
  Assert.Contains("clinical-safety gate",await governed.Content.ReadAsStringAsync(),StringComparison.OrdinalIgnoreCase);

  var legacy=await client.PostAsJsonAsync($"/api/phase1/mar/{Guid.NewGuid()}/administer",new{administeredAt=DateTimeOffset.UtcNow,notes="Regression"});
  Assert.Equal(HttpStatusCode.Locked,legacy.StatusCode);
  Assert.Contains("clinical-safety gate",await legacy.Content.ReadAsStringAsync(),StringComparison.OrdinalIgnoreCase);

  using var otherBranchFactory=factory.WithWebHostBuilder(builder=>builder.ConfigureAppConfiguration(configuration=>configuration.AddInMemoryCollection(new Dictionary<string,string?>
  {
   ["MedicationSafety:EmarProductionEnabled"]="true",
   ["MedicationSafety:PilotBranchId"]=Guid.NewGuid().ToString()
  })));
  var otherBranch=otherBranchFactory.CreateClient();
  var otherLogin=await otherBranch.PostAsJsonAsync("/api/auth/login",new{userName="admin",password="Admin123!",mfaCode=(string?)null});
  otherLogin.EnsureSuccessStatusCode();
  otherBranch.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await otherLogin.Content.ReadFromJsonAsync<Login>())!.Token);
  await StepUpTestGrants.GrantAsync(factory,otherBranch,"medication");
  var blockedPilot=await otherBranch.PostAsJsonAsync($"/api/phase1/emar-safety/mar/{Guid.NewGuid()}/record",new{outcome="Administered",occurredAt=DateTimeOffset.UtcNow,doseQuantity=1m,reasonCode="",reasonDetail="",prnIndication="",witnessUserId=(Guid?)null,overrideReason=""});
  Assert.Equal(HttpStatusCode.Locked,blockedPilot.StatusCode);
 }
 [Fact]
 public async Task AllergyDuplicateClockIdempotencyAndRouteCompetencyAreEnforced()
 {
  await factory.EnsureClinicalSeedAsync();var person=Guid.NewGuid();var workerId=Guid.NewGuid();var medication=Guid.NewGuid();var duplicate=Guid.NewGuid();var allergyMedication=Guid.NewGuid();var routeMedication=Guid.NewGuid();var first=Guid.NewGuid();var replayRecord=Guid.NewGuid();var futureRecord=Guid.NewGuid();var duplicateRecord=Guid.NewGuid();var allergyRecord=Guid.NewGuid();var routeRecord=Guid.NewGuid();var workerName=$"emar.safety.{Guid.NewGuid():N}";Guid adminId;
  using(var scope=factory.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();adminId=(await db.AppUsers.SingleAsync(x=>x.UserName=="admin")).Id;db.ServiceUsers.Add(new ServiceUser(person,"eMAR Safety Person",new DateOnly(1950,1,1),"+10000000019","Medication support","Contact","",RiskLevel.Medium,"Active","Address","None","None","Private","Regression","","Independent","Independent","Verbal","None","Standard",TenantDefaults.OrganizationId,TenantDefaults.BranchId));db.CareWorkers.Add(new CareWorker(workerId,"eMAR Safety Worker","Medication administration","Available",1,20,"Valid","Compliant","10 miles",TenantDefaults.OrganizationId,TenantDefaults.BranchId));db.AppUsers.Add(new AppUser(Guid.NewGuid(),workerName,$"{workerName}@aicare.local",PasswordHasher.HashPassword("Worker123!"),UserRole.CareWorker,true,TenantDefaults.OrganizationId,TenantDefaults.BranchId,workerId,null));db.Medications.AddRange(new Medication(medication,person,"Paracetamol","500 mg","Oral","Morning",false,"Pharmacy","None",TenantDefaults.OrganizationId,TenantDefaults.BranchId),new Medication(duplicate,person,"Paracetamol","500 mg","Oral","Evening",false,"Pharmacy","None",TenantDefaults.OrganizationId,TenantDefaults.BranchId),new Medication(allergyMedication,person,"Amoxicillin","250 mg","Oral","Morning",false,"Pharmacy","Amoxicillin",TenantDefaults.OrganizationId,TenantDefaults.BranchId),new Medication(routeMedication,person,"Eye drops","1 drop","Eye","Morning",false,"Pharmacy","None",TenantDefaults.OrganizationId,TenantDefaults.BranchId));db.MedicationAdministrationRecords.AddRange(Mar(first,medication,workerId),Mar(replayRecord,medication,workerId),Mar(futureRecord,medication,workerId),Mar(duplicateRecord,duplicate,workerId),Mar(allergyRecord,allergyMedication,workerId),Mar(routeRecord,routeMedication,workerId));await db.SaveChangesAsync();await db.Database.ExecuteSqlInterpolatedAsync($"""insert into worker_competency_records(id,care_worker_id,organization_id,branch_id,competency,level,status,assessed_by,assessed_at,expires_at,notes,created_at) values({Guid.NewGuid()},{workerId},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},'Medication administration','Competent','Valid','Regression',{DateTimeOffset.UtcNow.AddDays(-1)},{DateTimeOffset.UtcNow.AddYears(1)},'General route',{DateTimeOffset.UtcNow})""");}
  var admin=await Client("admin","Admin123!");var worker=await Client(workerName,"Worker123!");
  var createdAllergy=await admin.PostAsJsonAsync($"/api/phase1/service-users/{person}/allergies",new{allergen="Amoxicillin",allergenType="Medicine",terminologyCode="",terminologySystem="",reaction="Rash",severity="Moderate",recordType="Allergy",informationSource="Clinical record",notes="Regression"});
  Assert.Equal(HttpStatusCode.Created,createdAllergy.StatusCode);
  using(var allergyJson=JsonDocument.Parse(await createdAllergy.Content.ReadAsStringAsync()))
  {
   var allergyId=allergyJson.RootElement.GetProperty("records")[0].GetProperty("id").GetGuid();
   Assert.Equal(HttpStatusCode.OK,(await admin.PostAsJsonAsync($"/api/phase1/service-users/{person}/allergies/{allergyId}/review",new{clinicalStatus="Active",verified=true,reason="Confirmed against clinical record"})).StatusCode);
  }
  await StepUpTestGrants.GrantAsync(factory,admin,"medication");
  async Task Verify(Guid med,string source){await StepUpTestGrants.GrantAsync(factory,admin,"medication");Assert.Equal(HttpStatusCode.OK,(await MedicationVerificationTestHelper.SaveAndVerifyAsync(factory, admin, med, new{indication="Regression",prescriber="Dr Safety",form="Tablet",strength="500mg",startDate=DateTimeOffset.UtcNow.AddDays(-1),endDate=(DateTimeOffset?)null,doseWindowMinutes=60,maxPrnDoses24h=(int?)null,minPrnIntervalMinutes=(int?)null,prnIndication="",prnEffectReviewMinutes=(int?)null,stockOnHand=10m,reorderLevel=2m,requiresWitness=false,lastReconciledAt=DateTimeOffset.UtcNow,reconciledBy="Medication lead",reconciliationStatus="Verified",sourceType="Prescription",sourceReference=source,changeReason="Regression verification"})).StatusCode);}
  await Verify(medication,"RX-A");await Verify(duplicate,"RX-B");await Verify(allergyMedication,"RX-C");await Verify(routeMedication,"RX-D");
  void Key(string value){worker.DefaultRequestHeaders.Remove("Idempotency-Key");worker.DefaultRequestHeaders.Add("Idempotency-Key",value);}
  await StepUpTestGrants.GrantAsync(factory,worker,"medication");
  Key($"future-{Guid.NewGuid():N}");var future=await worker.PostAsJsonAsync($"/api/phase1/emar-safety/mar/{futureRecord}/record",new{outcome="Administered",occurredAt=DateTimeOffset.UtcNow.AddMinutes(10),doseQuantity=1m,reasonCode="",reasonDetail="",prnIndication="",witnessUserId=(Guid?)null,overrideReason=""});Assert.Equal(HttpStatusCode.Conflict,future.StatusCode);Assert.Contains("future",await future.Content.ReadAsStringAsync(),StringComparison.OrdinalIgnoreCase);
  await StepUpTestGrants.GrantAsync(factory,worker,"medication");
  Key($"allergy-{Guid.NewGuid():N}");var allergy=await worker.PostAsJsonAsync($"/api/phase1/emar-safety/mar/{allergyRecord}/record",new{outcome="Administered",occurredAt=DateTimeOffset.UtcNow,doseQuantity=1m,reasonCode="",reasonDetail="",prnIndication="",witnessUserId=(Guid?)null,overrideReason=""});Assert.Equal(HttpStatusCode.Conflict,allergy.StatusCode);Assert.Contains("allergy",await allergy.Content.ReadAsStringAsync(),StringComparison.OrdinalIgnoreCase);
  await StepUpTestGrants.GrantAsync(factory,worker,"medication");
  Key($"duplicate-{Guid.NewGuid():N}");var duplicateResponse=await worker.PostAsJsonAsync($"/api/phase1/emar-safety/mar/{duplicateRecord}/record",new{outcome="Administered",occurredAt=DateTimeOffset.UtcNow,doseQuantity=1m,reasonCode="",reasonDetail="",prnIndication="",witnessUserId=(Guid?)null,overrideReason=""});Assert.Equal(HttpStatusCode.Conflict,duplicateResponse.StatusCode);Assert.Contains("Duplicate",await duplicateResponse.Content.ReadAsStringAsync(),StringComparison.OrdinalIgnoreCase);
  using(var scope=factory.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();await db.Database.ExecuteSqlRawAsync("delete from worker_competency_records where care_worker_id={0}",workerId);}
  await StepUpTestGrants.GrantAsync(factory,worker,"medication");
  Key($"route-{Guid.NewGuid():N}");var route=await worker.PostAsJsonAsync($"/api/phase1/emar-safety/mar/{routeRecord}/record",new{outcome="Administered",occurredAt=DateTimeOffset.UtcNow,doseQuantity=1m,reasonCode="",reasonDetail="",prnIndication="",witnessUserId=(Guid?)null,overrideReason=""});Assert.Equal(HttpStatusCode.Conflict,route.StatusCode);Assert.Contains("competency",await route.Content.ReadAsStringAsync(),StringComparison.OrdinalIgnoreCase);
  using(var scope=factory.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();await db.Database.ExecuteSqlInterpolatedAsync($"""insert into worker_competency_records(id,care_worker_id,organization_id,branch_id,competency,level,status,assessed_by,assessed_at,expires_at,notes,created_at) values({Guid.NewGuid()},{workerId},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},'Medication administration','Competent','Valid','Regression',{DateTimeOffset.UtcNow.AddDays(-1)},{DateTimeOffset.UtcNow.AddYears(1)},'General route',{DateTimeOffset.UtcNow})""");await db.Database.ExecuteSqlRawAsync("update medication_safety_profiles set reconciliation_status='Superseded', change_reason='Duplicate resolved' where medication_id={0}",duplicate);}
  await StepUpTestGrants.GrantAsync(factory,worker,"medication");
  var idem=$"emar-replay-{Guid.NewGuid():N}";Key(idem);var firstSubmit=await worker.PostAsJsonAsync($"/api/phase1/emar-safety/mar/{replayRecord}/record",new{outcome="Administered",occurredAt=DateTimeOffset.UtcNow,doseQuantity=1m,reasonCode="",reasonDetail="",prnIndication="",witnessUserId=(Guid?)null,overrideReason=""});Assert.Equal(HttpStatusCode.Created,firstSubmit.StatusCode);await StepUpTestGrants.GrantAsync(factory,worker,"medication");Key(idem);var replay=await worker.PostAsJsonAsync($"/api/phase1/emar-safety/mar/{replayRecord}/record",new{outcome="Administered",occurredAt=DateTimeOffset.UtcNow,doseQuantity=1m,reasonCode="",reasonDetail="",prnIndication="",witnessUserId=(Guid?)null,overrideReason=""});Assert.Equal(HttpStatusCode.OK,replay.StatusCode);Assert.Contains("replayed",await replay.Content.ReadAsStringAsync(),StringComparison.OrdinalIgnoreCase);
 }
 [Theory]
 [InlineData(2, false, false, 2)]
 [InlineData(1, false, false, 1)]
 [InlineData(2, true, false, 1)]
 [InlineData(2, false, true, 1)]
 public async Task ConcurrentAdministrationsPreserveStockAndTerminalOutcome(int initialStock, bool sameMar, bool prn, int expectedSuccesses)
 {
  await factory.EnsureClinicalSeedAsync();
  var medication=Guid.NewGuid(); var first=Guid.NewGuid(); var second=sameMar?first:Guid.NewGuid();
  using(var scope=factory.Services.CreateScope())
  {
   var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
   db.Medications.Add(new Medication(medication,RegressionIds.ServiceUserId,$"Concurrency {medication}","1 mg","Oral",prn?"PRN":"Morning",prn,"Pharmacy","None",TenantDefaults.OrganizationId,TenantDefaults.BranchId));
   db.MedicationAdministrationRecords.Add(Mar(first,medication,RegressionIds.WorkerId));
   if(!sameMar)db.MedicationAdministrationRecords.Add(Mar(second,medication,RegressionIds.WorkerId));
   await db.SaveChangesAsync();
   await db.Database.ExecuteSqlInterpolatedAsync($"""insert into worker_competency_records(id,care_worker_id,organization_id,branch_id,competency,level,status,assessed_by,assessed_at,expires_at,notes,created_at) values({Guid.NewGuid()},{RegressionIds.WorkerId},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},'Medication administration','Competent','Valid','Regression',{DateTimeOffset.UtcNow.AddDays(-1)},{DateTimeOffset.UtcNow.AddYears(1)},'Concurrency',now())""");
  }
  var admin=await Client("admin","Admin123!");
  Assert.Equal(HttpStatusCode.OK,(await admin.PutAsJsonAsync($"/api/phase1/service-users/{RegressionIds.ServiceUserId}/allergies/status",new{status="NoKnownAllergies",source="Clinical record reviewed by test medication lead"})).StatusCode);
  await StepUpTestGrants.GrantAsync(factory,admin,"medication");
  var profile=await MedicationVerificationTestHelper.SaveAndVerifyAsync(factory, admin, medication, new { indication="Regression",prescriber="Dr Safety",form="Tablet",strength="1mg",startDate=DateTimeOffset.UtcNow.AddDays(-1),doseWindowMinutes=60,maxPrnDoses24h=4,minPrnIntervalMinutes=240,prnIndication="Pain",prnEffectReviewMinutes=30,stockOnHand=initialStock,reorderLevel=0,requiresWitness=false,lastReconciledAt=DateTimeOffset.UtcNow,reconciledBy="Lead",reconciliationStatus="Verified",sourceType="Prescription",sourceReference="RACE",changeReason="Regression" });
  profile.EnsureSuccessStatusCode();
  var occurred=DateTimeOffset.UtcNow;
  async Task<HttpResponseMessage> Send(Guid mar)
  {
   var request=new HttpRequestMessage(HttpMethod.Post,$"/api/phase1/emar-safety/mar/{mar}/record");
   request.Headers.Add("Idempotency-Key",Guid.NewGuid().ToString());
   request.Content=JsonContent.Create(new { outcome="Administered",occurredAt=occurred,doseQuantity=1m,prnIndication="Pain",reasonCode="",reasonDetail="",overrideReason="" });
   return await admin.SendAsync(request);
  }
  await StepUpTestGrants.GrantAsync(factory,admin,"medication");
  await StepUpTestGrants.GrantAsync(factory,admin,"medication");
  var responses=await Task.WhenAll(Send(first),Send(second));
  Assert.Equal(expectedSuccesses,responses.Count(x=>x.StatusCode==HttpStatusCode.Created));
  Assert.All(responses,x=>Assert.True(x.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict,$"Unexpected response {x.StatusCode}"));
  using var verify=factory.Services.CreateScope();var context=verify.ServiceProvider.GetRequiredService<CareDbContext>();
  var connection=context.Database.GetDbConnection();await connection.OpenAsync();
  await using var command=connection.CreateCommand();
  command.CommandText="select stock_on_hand,(select count(*) from emar_ledger where medication_id=@id and event_type='Administration'),(select count(*) from medication_stock_transactions where medication_id=@id and transaction_type='Administration') from medication_safety_profiles where medication_id=@id";
  command.Parameters.Add(new NpgsqlParameter("id",medication));
  await using var reader=await command.ExecuteReaderAsync();Assert.True(await reader.ReadAsync());
  Assert.Equal(initialStock-expectedSuccesses,reader.GetDecimal(0));
  Assert.Equal(expectedSuccesses,reader.GetInt64(1));Assert.Equal(expectedSuccesses,reader.GetInt64(2));
 }

 [Fact]
 public async Task AllergyRegisterRequiresReviewAndPreventsContradictoryNoKnownStatus()
 {
  await factory.EnsureClinicalSeedAsync();
  var person=Guid.NewGuid();
  using(var scope=factory.Services.CreateScope())
  {
   var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
   db.ServiceUsers.Add(new ServiceUser(person,"Allergy Register Person",new DateOnly(1970,1,1),"07000000001","Medication support","Contact","",RiskLevel.Medium,"Active","Address","None","None","Private","Regression","","Independent","Independent","Verbal","None","Standard",TenantDefaults.OrganizationId,TenantDefaults.BranchId));
   await db.SaveChangesAsync();
  }
  var admin=await Client("admin","Admin123!");
  var path=$"/api/phase1/service-users/{person}/allergies";
  var initial=await admin.GetStringAsync(path);
  Assert.Contains("\"status\":\"Unknown\"",initial);
  var created=await admin.PostAsJsonAsync(path,new{allergen="Penicillin",allergenType="Medicine",terminologyCode="123",terminologySystem="dm+d",reaction="Anaphylaxis",severity="Severe",recordType="Allergy",informationSource="GP summary",notes="Emergency alert"});
  Assert.Equal(HttpStatusCode.Created,created.StatusCode);
  using var json=JsonDocument.Parse(await created.Content.ReadAsStringAsync());
  var id=json.RootElement.GetProperty("records")[0].GetProperty("id").GetGuid();
  Assert.Equal("Unknown",json.RootElement.GetProperty("status").GetString());
  var contradiction=await admin.PutAsJsonAsync(path+"/status",new{status="NoKnownAllergies",source="Person report"});
  Assert.Equal(HttpStatusCode.Conflict,contradiction.StatusCode);
  var verified=await admin.PostAsJsonAsync(path+"/"+id+"/review",new{clinicalStatus="Active",verified=true,reason="GP summary confirmed"});
  Assert.Equal(HttpStatusCode.OK,verified.StatusCode);
  Assert.Contains("KnownAllergies",await verified.Content.ReadAsStringAsync());
  var resolved=await admin.PostAsJsonAsync(path+"/"+id+"/review",new{clinicalStatus="Resolved",verified=true,reason="Specialist review"});
  Assert.Equal(HttpStatusCode.OK,resolved.StatusCode);
  Assert.Contains("\"status\":\"Unknown\"",await resolved.Content.ReadAsStringAsync());
  var noKnown=await admin.PutAsJsonAsync(path+"/status",new{status="NoKnownAllergies",source="Specialist review"});
  Assert.Equal(HttpStatusCode.OK,noKnown.StatusCode);
  var history=await admin.GetStringAsync(path+"/history");
  Assert.Contains("Created",history);Assert.Contains("Reviewed",history);Assert.Contains("StatusReviewed",history);
 }
 private static MedicationAdministrationRecord Mar(Guid id,Guid medication,Guid worker)=>new(id,medication,Guid.NewGuid(),worker,DateTimeOffset.UtcNow,null,"Scheduled","",TenantDefaults.OrganizationId,TenantDefaults.BranchId);
 private async Task<HttpClient> Client(string name,string password){var client=factory.CreateClient();var login=await client.PostAsJsonAsync("/api/auth/login",new{userName=name,password,mfaCode=(string?)null});login.EnsureSuccessStatusCode();client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await login.Content.ReadFromJsonAsync<Login>())!.Token);return client;}
 private sealed record Login(string Token);private sealed record Created(Guid Id,string Outcome,decimal? StockAfter);
}
