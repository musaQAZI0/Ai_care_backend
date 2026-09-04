using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed class AssessmentRiskGovernanceRegressionTests(PostgresRegressionFactory factory):IClassFixture<PostgresRegressionFactory>
{
 [Fact]
 public async Task AssessmentsAndRisksRequireGovernedReviewSignatureAndImmutableCorrection()
 {
  await factory.EnsureClinicalSeedAsync();var person=Guid.NewGuid();var adminName=$"clinical.admin.{Guid.NewGuid():N}";var coordinatorName=$"clinical.coordinator.{Guid.NewGuid():N}";var workerName=$"clinical.worker.{Guid.NewGuid():N}";
  using(var scope=factory.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();db.ServiceUsers.Add(new ServiceUser(person,"Clinical Governance Person",new DateOnly(1942,2,2),"+10000000008","Support","Contact","",RiskLevel.Medium,"Active","Address","None","None","Private","Regression","","Independent","Independent","Verbal","None","Standard",TenantDefaults.OrganizationId,TenantDefaults.BranchId));db.AppUsers.AddRange(User(adminName,UserRole.Administrator),User(coordinatorName,UserRole.CareCoordinator),User(workerName,UserRole.CareWorker));await db.SaveChangesAsync();}
  var admin=await Client(adminName);var coordinator=await Client(coordinatorName);var worker=await Client(workerName);var root=$"/api/phase1/service-users/{person}/clinical-governance";
  Assert.Equal(HttpStatusCode.Forbidden,(await worker.GetAsync(root)).StatusCode);
  var assessment=new{assessmentType="Initial needs",templateKey="initial-needs",templateVersion="1.0",answersJson="{\"mobility\":\"Assisted\"}",score=8,riskLevel="Medium",summary="Assistance required",recommendedActions="Update mobility plan",assessorName="Case coordinator",assessorRole="Social worker",reviewDueAt=DateTimeOffset.UtcNow.AddMonths(3),changeReason=(string?)null};
  Assert.Equal(HttpStatusCode.BadRequest,(await coordinator.PostAsJsonAsync($"{root}/assessments",assessment with { answersJson="invalid" })).StatusCode);
  var created=await coordinator.PostAsJsonAsync($"{root}/assessments",assessment);Assert.Equal(HttpStatusCode.Created,created.StatusCode);var assessmentId=(await created.Content.ReadFromJsonAsync<Created>())!.Id;
  Assert.Equal(HttpStatusCode.NoContent,(await coordinator.PostAsync($"{root}/assessments/{assessmentId}/submit",null)).StatusCode);
  Assert.Equal(HttpStatusCode.Forbidden,(await coordinator.PostAsJsonAsync($"{root}/assessments/{assessmentId}/approve",new{signerName="Coordinator",declaration="I approve this assessment"})).StatusCode);
  Assert.Equal(HttpStatusCode.BadRequest,(await admin.PostAsJsonAsync($"{root}/assessments/{assessmentId}/approve",new{signerName="",declaration=""})).StatusCode);
  var approved=await admin.PostAsJsonAsync($"{root}/assessments/{assessmentId}/approve",new{signerName="Registered manager",declaration="I reviewed and approve this exact assessment version"});Assert.True(approved.StatusCode==HttpStatusCode.NoContent,$"Approval returned {approved.StatusCode}: {await approved.Content.ReadAsStringAsync()}");
  Assert.Equal(HttpStatusCode.Conflict,(await coordinator.PostAsJsonAsync($"{root}/assessments",assessment)).StatusCode);
  var correction=await coordinator.PostAsJsonAsync($"{root}/assessments/{assessmentId}/corrections",assessment with { score=10,summary="Needs increased assistance",changeReason="Mobility deteriorated" });Assert.Equal(HttpStatusCode.Created,correction.StatusCode);Assert.Equal(2,(await correction.Content.ReadFromJsonAsync<Created>())!.Version);

  var risk=new{category="Falls",hazard="Unsteady transfers",likelihood=4,severity=4,controls="Two-person transfer and equipment",contingency="Stop transfer and call manager",owner="Care manager",reviewDueAt=DateTimeOffset.UtcNow.AddMonths(1),changeReason=(string?)null};
  Assert.Equal(HttpStatusCode.BadRequest,(await coordinator.PostAsJsonAsync($"{root}/risks",risk with { likelihood=6 })).StatusCode);
  var riskCreated=await coordinator.PostAsJsonAsync($"{root}/risks",risk);Assert.Equal(HttpStatusCode.Created,riskCreated.StatusCode);var riskId=(await riskCreated.Content.ReadFromJsonAsync<Created>())!.Id;
  Assert.Equal(HttpStatusCode.NoContent,(await coordinator.PostAsync($"{root}/risks/{riskId}/submit",null)).StatusCode);
  Assert.Equal(HttpStatusCode.NoContent,(await admin.PostAsJsonAsync($"{root}/risks/{riskId}/approve",new{signerName="Registered manager",declaration="I reviewed the hazards, score and controls"})).StatusCode);
  var view=await admin.GetStringAsync(root);Assert.Contains("Current",view);Assert.Contains("Critical risk requires manager oversight",view);Assert.Contains("Mobility deteriorated",view);
  using var verify=factory.Services.CreateScope();var verifyDb=verify.ServiceProvider.GetRequiredService<CareDbContext>();Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="assessment.approved"&&x.EntityId==assessmentId));Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="risk_assessment.approved"&&x.EntityId==riskId));Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="assessment.correction_created"));
 }
 private static AppUser User(string name,UserRole role)=>new(Guid.NewGuid(),name,$"{name}@aicare.local",PasswordHasher.HashPassword("Admin123!"),role,true,TenantDefaults.OrganizationId,TenantDefaults.BranchId,null,null);
 private async Task<HttpClient> Client(string name){var client=factory.CreateClient();var response=await client.PostAsJsonAsync("/api/auth/login",new{userName=name,password="Admin123!",mfaCode=(string?)null});response.EnsureSuccessStatusCode();client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await response.Content.ReadFromJsonAsync<Login>())!.Token);return client;}
 private sealed record Login(string Token);private sealed record Created(Guid Id,int Version);
}
