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
public sealed class FinanceExportPermissionRegressionTests(PostgresRegressionFactory factory):IClassFixture<PostgresRegressionFactory>
{
 [Fact]public async Task ExportRequiresIndependentBranchScopedAuthorityAndAuditsEveryDownload()
 {
  await factory.EnsureClinicalSeedAsync();var org=Guid.NewGuid();var branch=Guid.NewGuid();var other=Guid.NewGuid();var author=Guid.NewGuid();var beneficiary=Guid.NewGuid();var grantor=Guid.NewGuid();var prefix=$"export.{Guid.NewGuid():N}";
  using(var scope=factory.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();db.Organizations.Add(new Organization(org,"Export test","Trial","Active"));db.Branches.AddRange(new Branch(branch,org,"Main","Test","Active"),new Branch(other,org,"Other","Test","Active"));foreach(var x in new[]{(author,"author"),(beneficiary,"beneficiary"),(grantor,"grantor")})db.AppUsers.Add(new AppUser(x.Item1,prefix+x.Item2,prefix+x.Item2+"@test.invalid",PasswordHasher.HashPassword("Admin123!"),UserRole.BackOffice,true,org,branch));await db.SaveChangesAsync();}
  var creator=await Login(prefix+"author");var recipient=await Login(prefix+"beneficiary");var approver=await Login(prefix+"grantor");var query=$"{Root}/Ledger?branchId={branch}&from=2026-01-01&to=2099-12-31&format=csv";
  Assert.Equal(HttpStatusCode.Forbidden,(await recipient.GetAsync(query)).StatusCode);
  var request=new FinanceExportPermissionRequest(Guid.NewGuid(),branch,beneficiary,"Ledger",false,false,new(2026,1,1),new(2099,12,31),"SYNTHETIC-EXPORT-AUTHORITY");
  (await creator.PostAsJsonAsync(Root+"/permissions",request)).EnsureSuccessStatusCode();Assert.Equal(HttpStatusCode.OK,(await creator.PostAsJsonAsync(Root+"/permissions",request)).StatusCode);Assert.Equal(HttpStatusCode.Conflict,(await creator.PostAsJsonAsync(Root+"/permissions",request with{AllowIdentifiers=true})).StatusCode);
  Assert.Equal(HttpStatusCode.Conflict,(await creator.PostAsync($"{Root}/permissions/{request.RequestId}/approve",null)).StatusCode);Assert.Equal(HttpStatusCode.Conflict,(await recipient.PostAsync($"{Root}/permissions/{request.RequestId}/approve",null)).StatusCode);(await approver.PostAsync($"{Root}/permissions/{request.RequestId}/approve",null)).EnsureSuccessStatusCode();
  var downloads=await Task.WhenAll(recipient.GetAsync(query),recipient.GetAsync(query));foreach(var response in downloads){Assert.True(response.IsSuccessStatusCode,$"Export: {response.StatusCode}; {await response.Content.ReadAsStringAsync()}; {factory.LastServerException}");Assert.DoesNotContain("invoice_id",await response.Content.ReadAsStringAsync());Assert.Equal("text/csv",response.Content.Headers.ContentType!.MediaType);}
  Assert.Equal(HttpStatusCode.Forbidden,(await recipient.GetAsync(query.Replace(branch.ToString(),other.ToString()))).StatusCode);Assert.Equal(HttpStatusCode.Forbidden,(await recipient.GetAsync(query.Replace("/Ledger?","/Claims?"))).StatusCode);
  using var verify=factory.Services.CreateScope();var verifyDb=verify.ServiceProvider.GetRequiredService<CareDbContext>();Assert.Equal(2,await verifyDb.Database.SqlQueryRaw<int>("select count(*)::int as \"Value\" from \"AuditEvents\" where \"OrganizationId\"={0} and \"Action\" like 'FINANCE_EXPORT_DOWNLOADED:%'",org).SingleAsync());await Assert.ThrowsAnyAsync<Exception>(()=>verifyDb.Database.ExecuteSqlRawAsync("delete from finance_export_permissions where id={0}",request.RequestId));
 }
 const string Root="/api/phase1/finance/exports";
 async Task<HttpClient> Login(string name){var c=factory.CreateClient();var r=await c.PostAsJsonAsync("/api/auth/login",new{userName=name,password="Admin123!",mfaCode=(string?)null});r.EnsureSuccessStatusCode();c.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await r.Content.ReadFromJsonAsync<Token>())!.Value);return c;}
 sealed record Token([property:System.Text.Json.Serialization.JsonPropertyName("token")]string Value);
}
