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
public sealed class BillingEngineRegressionTests(PostgresRegressionFactory factory):IClassFixture<PostgresRegressionFactory>
{
 [Fact]
 public async Task GenerationIsDeterministicIdempotentAuditedAndApprovedEventsAreImmutable()
 {
  await factory.EnsureClinicalSeedAsync();var person=Guid.NewGuid();var worker=Guid.NewGuid();var validVisit=Guid.NewGuid();var invalidVisit=Guid.NewGuid();var name=$"billing.finance.{Guid.NewGuid():N}";var starts=new DateTimeOffset(2042,4,7,9,0,0,TimeSpan.Zero);
  using(var scope=factory.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();db.ServiceUsers.Add(new ServiceUser(person,"Billing Person",new DateOnly(1950,1,1),"1","Care","Contact","",RiskLevel.Low,"Active","Address","None","None","Private","","","Independent","Independent","Verbal","Routine","Standard",TenantDefaults.OrganizationId,TenantDefaults.BranchId));db.CareWorkers.Add(new CareWorker(worker,"Billing Worker","Care","Flexible",0,0,"Valid","Compliant","10",TenantDefaults.OrganizationId,TenantDefaults.BranchId));db.Visits.AddRange(new Visit(validVisit,person,worker,starts,"Personal care",90,"Care",VisitStatus.Completed,null,null,null,null,null,null,TenantDefaults.OrganizationId,TenantDefaults.BranchId),new Visit(invalidVisit,person,worker,starts.AddDays(1),"Personal care",60,"Care",VisitStatus.Scheduled,null,null,null,null,null,null,TenantDefaults.OrganizationId,TenantDefaults.BranchId));db.AppUsers.Add(new AppUser(Guid.NewGuid(),name,$"{name}@aicare.local",PasswordHasher.HashPassword("Admin123!"),UserRole.BackOffice,true,TenantDefaults.OrganizationId,TenantDefaults.BranchId,null,null));await db.SaveChangesAsync();}
  var client=await Client(name);var funderResponse=await client.PostAsJsonAsync("/api/phase1/finance/funders",new{name="Billing Council",funderType="LocalAuthority",paymentTermsDays=30,defaultInvoiceFrequency="Monthly",currency="GBP"});funderResponse.EnsureSuccessStatusCode();var funder=(await funderResponse.Content.ReadFromJsonAsync<Identifier>())!;var cardResponse=await client.PostAsJsonAsync("/api/phase1/finance/rate-cards",new{name="Billing Rate 2042",serviceType="Personal care",currency="GBP"});cardResponse.EnsureSuccessStatusCode();var card=(await cardResponse.Content.ReadFromJsonAsync<Identifier>())!;
  (await client.PostAsJsonAsync($"/api/phase1/finance/rate-cards/{card.Id}/versions",new{effectiveFrom="2042-04-01",effectiveTo="2043-03-31",rules=new[]{new{ruleType="Standard",dayType="Weekday",unit="Hour",unitRate=24m,minimumQuantity=1m,priority=10}}})).EnsureSuccessStatusCode();(await client.PostAsJsonAsync("/api/phase1/finance/funding-arrangements",new{serviceUserId=person,funderId=funder.Id,rateCardId=card.Id,startDate="2042-04-01T00:00:00Z",endDate="2043-03-31T23:59:59Z",invoiceFrequency="Monthly",allocationRule="Primary",serviceType="Personal care",authorizedHoursPerWeek=10m})).EnsureSuccessStatusCode();
  var generated=await client.PostAsJsonAsync("/api/phase1/finance/billing/generate",new{periodStart=starts.AddHours(-1),periodEnd=starts.AddDays(2)});generated.EnsureSuccessStatusCode();var summary=(await generated.Content.ReadFromJsonAsync<Summary>())!;Assert.Equal(1,summary.Generated);Assert.Equal(1,summary.Exceptions);
  var repeat=(await client.PostAsJsonAsync("/api/phase1/finance/billing/generate",new{periodStart=starts.AddHours(-1),periodEnd=starts.AddDays(2)}));repeat.EnsureSuccessStatusCode();Assert.Equal(0,(await repeat.Content.ReadFromJsonAsync<Summary>())!.Generated);
  var events=(await client.GetFromJsonAsync<List<Event>>("/api/phase1/finance/billing/events"))!;var billable=Assert.Single(events);Assert.Equal(36m,billable.FinalAmount);Assert.Contains("1.5 hour",billable.CalculationExplanation);Assert.Single((await client.GetFromJsonAsync<List<ExceptionRow>>("/api/phase1/finance/billing/exceptions"))!);
  Assert.Equal(HttpStatusCode.NoContent,(await client.PostAsync($"/api/phase1/finance/billing/events/{billable.Id}/approve",null)).StatusCode);Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync($"/api/phase1/finance/billing/events/{billable.Id}/adjust",new{adjustmentAmount=1m,reason="Late adjustment"})).StatusCode);
  using var verify=factory.Services.CreateScope();var verifyDb=verify.ServiceProvider.GetRequiredService<CareDbContext>();Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="BILLABLE_EVENTS_GENERATED"));Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="BILLABLE_EVENT_APPROVED"&&x.EntityId==billable.Id));await Assert.ThrowsAnyAsync<Exception>(()=>verifyDb.Database.ExecuteSqlRawAsync("update finance_billable_events set final_amount=99 where id={0}",billable.Id));
 }
 async Task<HttpClient> Client(string name){var c=factory.CreateClient();var r=await c.PostAsJsonAsync("/api/auth/login",new{userName=name,password="Admin123!",mfaCode=(string?)null});r.EnsureSuccessStatusCode();c.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await r.Content.ReadFromJsonAsync<Login>())!.Token);return c;}
 sealed record Login(string Token);sealed record Identifier(Guid Id);sealed record Summary(int Generated,int Exceptions);sealed record Event(Guid Id,decimal FinalAmount,string CalculationExplanation);sealed record ExceptionRow(Guid Id);
}
