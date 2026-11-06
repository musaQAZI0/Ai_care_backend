using System.Net;using System.Net.Http.Headers;using System.Net.Http.Json;using System.Text.Json;using AiCare.Domain;using AiCare.Infrastructure;using Microsoft.EntityFrameworkCore;using Microsoft.Extensions.DependencyInjection;using Xunit;
namespace AiCare.Tests;
[Collection("Postgres regression")]
public sealed class MobileBrowserSettingsRegressionTests(PostgresRegressionFactory factory):IClassFixture<PostgresRegressionFactory>
{
 [Fact]public async Task SettingsPersistAffectWorkerReadsAndRejectManagerWrites()
 {
  await factory.EnsureClinicalSeedAsync();var suffix=Guid.NewGuid().ToString("N");var managerName=$"mobile.manager.{suffix}";
  using(var scope=factory.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();db.AppUsers.Add(new AppUser(Guid.NewGuid(),managerName,$"{managerName}@example.test",PasswordHasher.HashPassword("Admin123!"),UserRole.CareManager,true,TenantDefaults.OrganizationId,TenantDefaults.BranchId));await db.SaveChangesAsync();}
  using var manager=await Login(managerName);var payload=new{visitAlertsEnabled=true,alertLeadMinutes=45,showCompletedVisits=false,showVisitType=false};Assert.Equal(HttpStatusCode.Forbidden,(await manager.PutAsJsonAsync("/api/phase1/mobile-browser-settings",payload)).StatusCode);
  using var admin=await Login("admin");Assert.Equal(HttpStatusCode.NoContent,(await admin.PutAsJsonAsync("/api/phase1/mobile-browser-settings",payload)).StatusCode);
  var saved=await manager.GetFromJsonAsync<JsonElement>("/api/phase1/mobile-browser-settings");Assert.Equal(45,saved.GetProperty("alertLeadMinutes").GetInt32());Assert.False(saved.GetProperty("showCompletedVisits").GetBoolean());Assert.False(saved.GetProperty("showVisitType").GetBoolean());
  using var verify=factory.Services.CreateScope();Assert.True(await verify.ServiceProvider.GetRequiredService<CareDbContext>().AuditEvents.AnyAsync(x=>x.Action=="admin.mobile_browser_settings_saved"&&x.Actor=="admin"&&x.EntityId==TenantDefaults.OrganizationId));
 }
 async Task<HttpClient> Login(string name){var c=factory.CreateClient();var r=await c.PostAsJsonAsync("/api/auth/login",new{userName=name,password="Admin123!",mfaCode=(string?)null});r.EnsureSuccessStatusCode();c.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString());return c;}
}
