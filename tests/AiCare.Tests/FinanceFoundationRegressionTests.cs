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
public sealed class FinanceFoundationRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task FundersFundingAndVersionedRatesAreScopedValidatedAndAudited()
    {
        await factory.EnsureClinicalSeedAsync();
        var personId=Guid.NewGuid();var financeName=$"foundation.finance.{Guid.NewGuid():N}";var managerName=$"foundation.manager.{Guid.NewGuid():N}";
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.ServiceUsers.Add(new ServiceUser(personId,"Foundation Person",new DateOnly(1955,1,1),"+10000000999","Personal care","Contact","",RiskLevel.Low,"Active","Address","None","None","Private","","","Independent","Independent","Verbal","Routine","Standard",TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            db.AppUsers.AddRange(User(financeName,UserRole.BackOffice),User(managerName,UserRole.CareManager));await db.SaveChangesAsync();
        }
        var finance=await Client(financeName);var manager=await Client(managerName);
        var funderResponse=await finance.PostAsJsonAsync("/api/phase1/finance/funders",new{name="Foundation Council",funderType="LocalAuthority",billingEmail="billing@council.invalid",paymentTermsDays=30,defaultInvoiceFrequency="Monthly",currency="GBP",organizationWide=false});
        Assert.Equal(HttpStatusCode.Created,funderResponse.StatusCode);var funder=(await funderResponse.Content.ReadFromJsonAsync<Identifier>())!;
        Assert.Equal(HttpStatusCode.Conflict,(await finance.PostAsJsonAsync("/api/phase1/finance/funders",new{name="foundation council",funderType="LocalAuthority",billingEmail="",paymentTermsDays=30,defaultInvoiceFrequency="Monthly",currency="GBP"})).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await manager.PostAsJsonAsync("/api/phase1/finance/funders",new{name="Not allowed",funderType="Other",paymentTermsDays=30,defaultInvoiceFrequency="Monthly",currency="GBP"})).StatusCode);
        Assert.Contains("Foundation Council",await manager.GetStringAsync("/api/phase1/finance/funders"));
        Assert.Equal("[]", await manager.GetStringAsync("/api/phase1/finance/funding-arrangements"));

        var cardResponse=await finance.PostAsJsonAsync("/api/phase1/finance/rate-cards",new{name="Council Personal Care 2040",serviceType="Personal care",currency="GBP"});
        Assert.Equal(HttpStatusCode.Created,cardResponse.StatusCode);var card=(await cardResponse.Content.ReadFromJsonAsync<Identifier>())!;
        var version=await finance.PostAsJsonAsync($"/api/phase1/finance/rate-cards/{card.Id}/versions",new{effectiveFrom="2040-04-01",effectiveTo="2041-03-31",rules=new[]{new{ruleType="Standard",dayType="Weekday",unit="Hour",unitRate=24.50m,minimumQuantity=0.25m,priority=10},new{ruleType="Weekend",dayType="Weekend",unit="Hour",unitRate=27m,minimumQuantity=0.25m,priority=20}}});
        Assert.True(version.StatusCode==HttpStatusCode.Created,await version.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict,(await finance.PostAsJsonAsync($"/api/phase1/finance/rate-cards/{card.Id}/versions",new{effectiveFrom="2040-06-01",effectiveTo="2040-12-31",rules=new[]{new{ruleType="Standard",dayType="Any",unit="Hour",unitRate=25m,minimumQuantity=0m,priority=0}}})).StatusCode);

        var arrangement=await finance.PostAsJsonAsync("/api/phase1/finance/funding-arrangements",new{serviceUserId=personId,funderId=funder.Id,rateCardId=card.Id,startDate="2040-04-01T00:00:00Z",endDate="2041-03-31T23:59:59Z",invoiceFrequency="Monthly",allocationRule="Primary",billingReference="PO-2040",serviceType="Personal care",authorizedHoursPerWeek=7m,notes="Regression"});
        Assert.Equal(HttpStatusCode.Created,arrangement.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await finance.PostAsJsonAsync("/api/phase1/finance/funding-arrangements",new{serviceUserId=personId,funderId=funder.Id,rateCardId=card.Id,startDate="2040-05-01T00:00:00Z",endDate=(string?)null,invoiceFrequency="Monthly",allocationRule="Primary",authorizedHoursPerWeek=2m})).StatusCode);
        Assert.Contains("Foundation Council",await manager.GetStringAsync($"/api/phase1/finance/funding-arrangements?serviceUserId={personId}"));

        using var verify=factory.Services.CreateScope();var verifyDb=verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="FUNDER_CREATED"&&x.EntityId==funder.Id));
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="RATE_CHANGED"));
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="FUNDING_ARRANGEMENT_CREATED"));
    }

    private static AppUser User(string name,UserRole role)=>new(Guid.NewGuid(),name,$"{name}@aicare.local",PasswordHasher.HashPassword("Admin123!"),role,true,TenantDefaults.OrganizationId,TenantDefaults.BranchId,null,null);
    private async Task<HttpClient> Client(string name){var client=factory.CreateClient();var response=await client.PostAsJsonAsync("/api/auth/login",new{userName=name,password="Admin123!",mfaCode=(string?)null});response.EnsureSuccessStatusCode();client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await response.Content.ReadFromJsonAsync<Login>())!.Token);return client;}
    private sealed record Login(string Token);private sealed record Identifier(Guid Id);
}
