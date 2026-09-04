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
public sealed class PrivilegedAccessRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task DelegationAndEmergencyAccessAreScopedExpiringReviewedAndImmutable()
    {
        await factory.EnsureClinicalSeedAsync();
        var person = Guid.NewGuid(); var otherPerson = Guid.NewGuid(); var workerId = Guid.NewGuid();
        var managerName = $"access.manager.{Guid.NewGuid():N}"; var workerName = $"access.worker.{Guid.NewGuid():N}";
        Guid workerUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.ServiceUsers.AddRange(Person(person,"Scoped person"),Person(otherPerson,"Other person"));
            db.CareWorkers.Add(new CareWorker(workerId,"Delegated worker","Care","Available",0,0,"Valid","Compliant","Local",TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            var worker = User(workerName,UserRole.CareWorker,workerId); workerUserId=worker.Id;
            db.AppUsers.AddRange(User(managerName,UserRole.CareManager),worker); await db.SaveChangesAsync();
        }
        var manager=await Login(managerName);var workerClient=await Login(workerName);var prefix="/api/phase1/service-users/";var route=prefix+person+"/clinical-governance";
        Assert.Equal(HttpStatusCode.Forbidden,(await workerClient.GetAsync(route)).StatusCode);
        var delegated=await manager.PostAsJsonAsync("/api/security/privileged-access/delegations",new{grantedToUserId=workerUserId,delegatedRole="CareCoordinator",actionScope="assessment.read",routePrefix=prefix,httpMethods=new[]{"GET"},branchId=TenantDefaults.BranchId,personId=person,reason="Temporary assessment review cover",startsAt=DateTimeOffset.UtcNow,expiresAt=DateTimeOffset.UtcNow.AddHours(1)});
        Assert.Equal(HttpStatusCode.Created,delegated.StatusCode);var delegationId=Id(delegated);
        Assert.Equal(HttpStatusCode.OK,(await workerClient.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await workerClient.GetAsync(prefix+otherPerson+"/clinical-governance")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await workerClient.PostAsJsonAsync(route+"/assessments",new{})).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await manager.PostAsJsonAsync($"/api/security/privileged-access/delegations/{delegationId}/review",new{decision="Approved",notes="Cover was appropriate and proportionate"})).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await manager.PostAsJsonAsync($"/api/security/privileged-access/delegations/{delegationId}/close",new{reason="Cover period ended"})).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await workerClient.GetAsync(route)).StatusCode);

        var emergency=await workerClient.PostAsJsonAsync("/api/security/privileged-access/emergency",new{personId=person,actionScope="assessment.read",routePrefix=prefix,httpMethods=new[]{"GET"},justification="Immediate access required to prevent serious harm",durationMinutes=15});
        Assert.Equal(HttpStatusCode.Created,emergency.StatusCode);var emergencyId=Id(emergency);
        Assert.Equal(HttpStatusCode.OK,(await workerClient.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await manager.PostAsJsonAsync($"/api/security/privileged-access/emergency/{emergencyId}/review",new{decision="Concern",notes="Emergency rationale requires follow-up"})).StatusCode);
        var dashboard=await manager.GetStringAsync("/api/security/privileged-access");Assert.Contains("EmergencyActivated",dashboard);
        Assert.Equal(HttpStatusCode.NoContent,(await workerClient.PostAsJsonAsync($"/api/security/privileged-access/emergency/{emergencyId}/close",new{reason="Immediate risk resolved"})).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await workerClient.GetAsync(route)).StatusCode);

        using var verify=factory.Services.CreateScope();var verifyDb=verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.True(await verifyDb.Database.SqlQuery<int>($"select 1 as \"Value\" from privileged_access_events where access_id={delegationId} and event_type='Used' limit 1").AnyAsync());
        await Assert.ThrowsAnyAsync<Exception>(()=>verifyDb.Database.ExecuteSqlRawAsync("update privileged_access_events set detail='changed'"));
    }

    private async Task<HttpClient> Login(string name){var client=factory.CreateClient();var response=await client.PostAsJsonAsync("/api/auth/login",new{userName=name,password="Admin123!",mfaCode=(string?)null});response.EnsureSuccessStatusCode();client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString());return client;}
    private static Guid Id(HttpResponseMessage response)=>JsonDocument.Parse(response.Content.ReadAsStringAsync().Result).RootElement.GetProperty("id").GetGuid();
    private static ServiceUser Person(Guid id,string name)=>new(id,name,new DateOnly(1980,1,1),"+100","Care","Contact","",RiskLevel.Low,"Onboarded","Address","None","None","Private","Regression","","Independent","Independent","Verbal","None","Standard",TenantDefaults.OrganizationId,TenantDefaults.BranchId);
    private static AppUser User(string name,UserRole role,Guid? worker=null)=>new(Guid.NewGuid(),name,$"{name}@aicare.local",PasswordHasher.HashPassword("Admin123!"),role,true,TenantDefaults.OrganizationId,TenantDefaults.BranchId,worker,null);
}
