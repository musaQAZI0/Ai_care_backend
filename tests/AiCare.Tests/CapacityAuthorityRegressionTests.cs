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
public sealed class CapacityAuthorityRegressionTests(PostgresRegressionFactory factory):IClassFixture<PostgresRegressionFactory>
{
 [Fact]
 public async Task CapacityBestInterestAndAuthorityAreVersionedRestrictedAndAudited()
 {
  await factory.EnsureClinicalSeedAsync();var person=Guid.NewGuid();var adminName=$"governance.admin.{Guid.NewGuid():N}";var coordinatorName=$"governance.coordinator.{Guid.NewGuid():N}";var workerName=$"governance.worker.{Guid.NewGuid():N}";
  using(var scope=factory.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();db.ServiceUsers.Add(new ServiceUser(person,"Governance Person",new DateOnly(1938,1,1),"+10000000007","Support","Contact","",RiskLevel.Medium,"Active","Address","None","None","Private","Regression","","Independent","Independent","Verbal","None","Standard",TenantDefaults.OrganizationId,TenantDefaults.BranchId));db.AppUsers.AddRange(User(adminName,UserRole.Administrator),User(coordinatorName,UserRole.CareCoordinator),User(workerName,UserRole.CareWorker));await db.SaveChangesAsync();}
  var admin=await Client(adminName);var coordinator=await Client(coordinatorName);var worker=await Client(workerName);
  Assert.Equal(HttpStatusCode.Forbidden,(await worker.GetAsync($"/api/phase1/service-users/{person}/governance")).StatusCode);
  var lacks=await coordinator.PostAsJsonAsync($"/api/phase1/service-users/{person}/governance/capacity",new{decisionContext="Consent to medication support",assessmentReason="Decision-specific assessment",canUnderstand=true,canRetain=false,canUseOrWeigh=true,canCommunicate=true,supportProvided="Easy read explanation and quiet environment",assessorName="Capacity assessor",assessorRole="Care manager",evidenceReference="CAP-1",assessedAt=DateTimeOffset.UtcNow,reviewDueAt=DateTimeOffset.UtcNow.AddMonths(3)});
  Assert.Equal(HttpStatusCode.Created,lacks.StatusCode);var capacity=(await lacks.Content.ReadFromJsonAsync<CreatedOutcome>())!;var capacityId=capacity.Id;Assert.Equal("LacksCapacity",capacity.Outcome);
  var incomplete=await admin.PostAsJsonAsync($"/api/phase1/service-users/{person}/governance/capacity/{capacityId}/best-interest",new{decision="Proceed",optionsConsidered="",consultedPeople="Family",personWishes="Support preferred",risksAndBenefits="Benefits outweigh risks",leastRestrictiveReason="Least restrictive option",decisionMaker="Manager"});
  Assert.Equal(HttpStatusCode.BadRequest,incomplete.StatusCode);
  Assert.Equal(HttpStatusCode.Forbidden,(await coordinator.PostAsJsonAsync($"/api/phase1/service-users/{person}/governance/capacity/{capacityId}/best-interest",new{decision="Proceed",optionsConsidered="Alternative support",consultedPeople="Family",personWishes="Support preferred",risksAndBenefits="Benefits outweigh risks",leastRestrictiveReason="Least restrictive option",decisionMaker="Manager"})).StatusCode);
  var best=await admin.PostAsJsonAsync($"/api/phase1/service-users/{person}/governance/capacity/{capacityId}/best-interest",new{decision="Proceed with prompted support",optionsConsidered="No support; reminders; prompted support",consultedPeople="Person, family, GP",personWishes="Remain independent",risksAndBenefits="Prompted support reduces missed doses",leastRestrictiveReason="Prompts preserve independent administration",decisionMaker="Registered manager",decidedAt=DateTimeOffset.UtcNow,reviewDueAt=DateTimeOffset.UtcNow.AddMonths(3)});
  Assert.Equal(HttpStatusCode.Created,best.StatusCode);var bestId=(await best.Content.ReadFromJsonAsync<Created>())!.Id;
  var reassessed=await coordinator.PostAsJsonAsync($"/api/phase1/service-users/{person}/governance/capacity",new{decisionContext="Consent to medication support",assessmentReason="Scheduled reassessment",canUnderstand=true,canRetain=true,canUseOrWeigh=true,canCommunicate=true,supportProvided="Easy read explanation",assessorName="Capacity assessor",assessorRole="Care manager",evidenceReference="CAP-2",assessedAt=DateTimeOffset.UtcNow.AddDays(1),reviewDueAt=DateTimeOffset.UtcNow.AddMonths(6)});
  Assert.Equal(HttpStatusCode.Created,reassessed.StatusCode);Assert.Equal(2,(await reassessed.Content.ReadFromJsonAsync<CreatedOutcome>())!.Version);

  var unverified=await coordinator.PostAsJsonAsync($"/api/phase1/service-users/{person}/governance/authorities",new{authorityType="Health and welfare LPA",holderName="Alex Representative",holderRelationship="Child",scope="Care decisions",verificationStatus="Pending",verificationReference="LPA-1",validFrom=DateTimeOffset.UtcNow});
  Assert.Equal(HttpStatusCode.BadRequest,unverified.StatusCode);
  var authority=await coordinator.PostAsJsonAsync($"/api/phase1/service-users/{person}/governance/authorities",new{authorityType="Health and welfare LPA",holderName="Alex Representative",holderRelationship="Child",scope="Care and support decisions",verificationStatus="Verified",verificationReference="OPG-LPA-1",validFrom=DateTimeOffset.UtcNow,validUntil=DateTimeOffset.UtcNow.AddYears(2)});
  Assert.Equal(HttpStatusCode.Created,authority.StatusCode);var authorityId=(await authority.Content.ReadFromJsonAsync<Created>())!.Id;
  var consentRequest=new{consentType="Care and support",scope="Routine support and record keeping",capacityBasis="Person has capacity",decisionMaker="Service user",evidenceReference="CONSENT-1",effectiveFrom=DateTimeOffset.UtcNow,expiresAt=DateTimeOffset.UtcNow.AddYears(1),informationCategories="Care records; medication records",sharingParties="Assigned care team",reviewDueAt=DateTimeOffset.UtcNow.AddMonths(6)};
  Assert.Equal(HttpStatusCode.Forbidden,(await worker.PostAsJsonAsync($"/api/phase1/service-users/{person}/consents",consentRequest)).StatusCode);
  Assert.Equal(HttpStatusCode.Created,(await coordinator.PostAsJsonAsync($"/api/phase1/service-users/{person}/consents",consentRequest)).StatusCode);
  Assert.Equal(HttpStatusCode.Created,(await coordinator.PostAsJsonAsync($"/api/phase1/service-users/{person}/consents",consentRequest with { evidenceReference="CONSENT-2" })).StatusCode);
  var consentHistory=await coordinator.GetStringAsync($"/api/phase1/service-users/{person}/consents");Assert.Contains("version",consentHistory);Assert.Contains(":2",consentHistory);Assert.Contains("Superseded",consentHistory);Assert.Contains("Assigned care team",consentHistory);
  Assert.Equal(HttpStatusCode.BadRequest,(await coordinator.PostAsJsonAsync($"/api/phase1/service-users/{person}/governance/authorities/{authorityId}/revoke",new{reason=""})).StatusCode);
  Assert.Equal(HttpStatusCode.NoContent,(await coordinator.PostAsJsonAsync($"/api/phase1/service-users/{person}/governance/authorities/{authorityId}/revoke",new{reason="Authority revoked by OPG"})).StatusCode);
  var view=await admin.GetStringAsync($"/api/phase1/service-users/{person}/governance");Assert.Contains("Superseded",view);Assert.Contains("HasCapacity",view);Assert.Contains("Authority revoked by OPG",view);
  using var verify=factory.Services.CreateScope();var verifyDb=verify.ServiceProvider.GetRequiredService<CareDbContext>();Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="capacity_decision.recorded"&&x.EntityId==capacityId));Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="best_interest_decision.recorded"&&x.EntityId==bestId));Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="authority_record.revoked"&&x.EntityId==authorityId));Assert.True(await verifyDb.AuditEvents.CountAsync(x=>x.Action=="consent.created")>=2);
 }
 private static AppUser User(string name,UserRole role)=>new(Guid.NewGuid(),name,$"{name}@aicare.local",PasswordHasher.HashPassword("Admin123!"),role,true,TenantDefaults.OrganizationId,TenantDefaults.BranchId,null,null);
 private async Task<HttpClient> Client(string name){var client=factory.CreateClient();var response=await client.PostAsJsonAsync("/api/auth/login",new{userName=name,password="Admin123!",mfaCode=(string?)null});response.EnsureSuccessStatusCode();client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await response.Content.ReadFromJsonAsync<Login>())!.Token);return client;}
 private sealed record Login(string Token);private sealed record Created(Guid Id);private sealed record CreatedOutcome(Guid Id,string Outcome,int Version);
}
