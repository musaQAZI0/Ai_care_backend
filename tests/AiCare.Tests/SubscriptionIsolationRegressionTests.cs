using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AiCare.Api;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace AiCare.Tests;
[Collection("Postgres regression")]
public sealed class SubscriptionIsolationRegressionTests(PostgresRegressionFactory factory):IClassFixture<PostgresRegressionFactory>
{
 [Fact]public async Task ManualProvisioningIsIndependentScopedIdempotentAndCannotMutateCareInvoices()
 {
  await factory.EnsureClinicalSeedAsync();var org=Guid.NewGuid();var branch=Guid.NewGuid();var worker=Guid.NewGuid();var person=Guid.NewGuid();var invoice=Guid.NewGuid();var creator=Guid.NewGuid();var approver=Guid.NewGuid();var finance=Guid.NewGuid();var prefix=$"subscription.{Guid.NewGuid():N}";
  using(var scope=factory.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();db.Organizations.Add(new Organization(org,"Subscription test","Legacy","Active"));db.Branches.Add(new Branch(branch,org,"Main","Test","Active"));db.CareWorkers.Add(new CareWorker(worker,"Test worker","Care","Flexible",0,0,"Valid","Compliant","10",org,branch));db.ServiceUsers.Add(new ServiceUser(person,"Person",new(1950,1,1),"1","Care","Contact","",RiskLevel.Low,"Active","Address","None","None","Private","","","Independent","Independent","Verbal","Routine","Standard",org,branch));db.Invoices.Add(new Invoice(invoice,person,"Payer",100,"Generated",DateTimeOffset.UtcNow,org,branch));foreach(var x in new[]{(creator,"creator",UserRole.Administrator),(approver,"approver",UserRole.Administrator),(finance,"finance",UserRole.BackOffice)})db.AppUsers.Add(new AppUser(x.Item1,prefix+x.Item2,prefix+x.Item2+"@test.invalid",PasswordHasher.HashPassword("Admin123!"),x.Item3,true,org,branch));await db.SaveChangesAsync();}
  var config=factory.Services.GetRequiredService<IConfiguration>();var old0=config["SubscriptionBilling:OperatorUserIds:0"];var old1=config["SubscriptionBilling:OperatorUserIds:1"];config["SubscriptionBilling:OperatorUserIds:0"]=creator.ToString();config["SubscriptionBilling:OperatorUserIds:1"]=approver.ToString();
  try{
   var author=await Login(prefix+"creator");var reviewer=await Login(prefix+"approver");var backOffice=await Login(prefix+"finance");var request=new SubscriptionVersionRequest(Guid.NewGuid(),"EXPLICIT-TEST-PLAN","billing@test.invalid","Active","Paid","SAAS-INVOICE-1","APPROVED-TEST-CONTRACT",DateTimeOffset.UtcNow.AddMinutes(-10),DateTimeOffset.UtcNow.AddDays(30),1,1,new(){["FinanceExports"]=true,["PayrollReady"]=true});
   Assert.Equal(HttpStatusCode.Forbidden,(await backOffice.PostAsJsonAsync(Root+"/versions",request)).StatusCode);(await author.PostAsJsonAsync(Root+"/versions",request)).EnsureSuccessStatusCode();Assert.Equal(HttpStatusCode.OK,(await author.PostAsJsonAsync(Root+"/versions",request)).StatusCode);Assert.Equal(HttpStatusCode.Conflict,(await author.PostAsJsonAsync(Root+"/versions",request with{PlanReference="CHANGED"})).StatusCode);Assert.Equal(HttpStatusCode.Conflict,(await author.PostAsync($"{Root}/versions/{request.RequestId}/approve",null)).StatusCode);(await reviewer.PostAsync($"{Root}/versions/{request.RequestId}/approve",null)).EnsureSuccessStatusCode();
   var entitlement=await author.GetFromJsonAsync<System.Text.Json.JsonElement>(Root+"/entitlements/FinanceExports");Assert.True(entitlement.GetProperty("allowed").GetBoolean());
   using(var verify=factory.Services.CreateScope()){var db=verify.ServiceProvider.GetRequiredService<CareDbContext>();await Assert.ThrowsAnyAsync<Exception>(()=>db.Database.ExecuteSqlRawAsync("insert into \"Branches\"(\"Id\",\"OrganizationId\",\"Name\",\"Region\",\"Status\") values({0},{1},'Over limit','Test','Active')",Guid.NewGuid(),org));}
   var suspension=request with{RequestId=Guid.NewGuid(),SubscriptionStatus="Suspended",EffectiveFrom=DateTimeOffset.UtcNow.AddSeconds(-1),DecisionReference="APPROVED-SUSPENSION"};(await author.PostAsJsonAsync(Root+"/versions",suspension)).EnsureSuccessStatusCode();(await reviewer.PostAsync($"{Root}/versions/{suspension.RequestId}/approve",null)).EnsureSuccessStatusCode();Assert.Equal(HttpStatusCode.Forbidden,(await author.GetAsync("/api/phase1/finance/exports/permissions")).StatusCode);Assert.Equal(HttpStatusCode.OK,(await author.GetAsync(Root)).StatusCode);
   using var final=factory.Services.CreateScope();var finalDb=final.ServiceProvider.GetRequiredService<CareDbContext>();var original=await finalDb.Invoices.SingleAsync(x=>x.Id==invoice);Assert.Equal("Generated",original.Status);Assert.Equal(100,original.Amount);Assert.Equal("Legacy",(await finalDb.Organizations.SingleAsync(x=>x.Id==org)).Plan);await Assert.ThrowsAnyAsync<Exception>(()=>finalDb.Database.ExecuteSqlRawAsync("update saas.subscription_versions set invoice_status='Void' where id={0}",request.RequestId));
  }finally{config["SubscriptionBilling:OperatorUserIds:0"]=old0;config["SubscriptionBilling:OperatorUserIds:1"]=old1;}
 }
 const string Root="/api/phase1/subscription";
 async Task<HttpClient> Login(string name){var c=factory.CreateClient();var r=await c.PostAsJsonAsync("/api/auth/login",new{userName=name,password="Admin123!",mfaCode=(string?)null});r.EnsureSuccessStatusCode();c.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await r.Content.ReadFromJsonAsync<Token>())!.Value);return c;}
 sealed record Token([property:System.Text.Json.Serialization.JsonPropertyName("token")]string Value);
}
