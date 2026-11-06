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
public sealed class SchedulingSafetyAvailabilityRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task UnavailableSafetyEvidenceBlocksBookingWithoutWritesAndRecoveryRestoresEligibility()
    {
        await factory.EnsureClinicalSeedAsync();
        var branch=Guid.NewGuid(); var person=Guid.NewGuid(); var worker=Guid.NewGuid(); var actor=$"scheduling.safety.{Guid.NewGuid():N}";
        using var scope=factory.Services.CreateScope();
        var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
        db.Branches.Add(new(branch,TenantDefaults.OrganizationId,"Safety availability branch","Test","Active"));
        db.ServiceUsers.Add(new(person,"Fictional safety availability person",new DateOnly(1950,1,1),"1","Care","Contact","",RiskLevel.Low,
            "Active","Address","None","None","Private","","","Independent","Independent","Verbal","Routine","Standard",TenantDefaults.OrganizationId,branch));
        db.CareWorkers.Add(new(worker,"Safety worker","Care","Flexible",0,0,"Valid","Compliant","Local",TenantDefaults.OrganizationId,branch));
        db.AppUsers.Add(new(Guid.NewGuid(),actor,actor+"@test.invalid",PasswordHasher.HashPassword("Admin123!"),UserRole.CareManager,true,TenantDefaults.OrganizationId,branch));
        await db.SaveChangesAsync();
        using var client=factory.CreateClient();
        var login=await client.PostAsJsonAsync("/api/auth/login",new{userName=actor,password="Admin123!",mfaCode=(string?)null});
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await login.Content.ReadFromJsonAsync<Login>())!.Token);
        var request=new{serviceUserId=person,careWorkerId=worker,additionalCareWorkerIds=Array.Empty<Guid>(),startsAt=DateTimeOffset.UtcNow.AddDays(30),
            visitType="Safety availability regression",durationMinutes=30,requiredSkills=""};
        var temporaryTable="regression_restrictions_"+Guid.NewGuid().ToString("N");
        // Only the isolated regression database is modified. Always restore the
        // required table, even when a request assertion fails.
        await db.Database.ExecuteSqlRawAsync($"""
            ALTER TABLE worker_assignment_restrictions RENAME TO "{temporaryTable}"
            """);
        try
        {
            var preflight=await client.PostAsJsonAsync("/api/phase1/visits/conflicts",request);
            Assert.Equal(HttpStatusCode.OK,preflight.StatusCode);
            Assert.Contains("safety-data-unavailable",await preflight.Content.ReadAsStringAsync());
            var booking=await client.PostAsJsonAsync("/api/phase1/visits",request);
            Assert.Equal(HttpStatusCode.BadRequest,booking.StatusCode);
            Assert.False(await db.Visits.AnyAsync(v=>v.ServiceUserId==person));
            Assert.False(await db.AuditEvents.AnyAsync(a=>a.Actor==actor&&a.EntityType==nameof(Visit)));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync($"""
                ALTER TABLE "{temporaryTable}" RENAME TO worker_assignment_restrictions
                """);
        }
        var recovered=await client.PostAsJsonAsync("/api/phase1/visits",request);
        Assert.Equal(HttpStatusCode.Created,recovered.StatusCode);
        Assert.Equal(1,await db.Visits.CountAsync(v=>v.ServiceUserId==person));
    }
    private sealed record Login(string Token);
}
