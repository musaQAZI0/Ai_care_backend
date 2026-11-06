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
public sealed class SchedulingVisitIntervalRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Theory]
    [InlineData("restriction-inside", false, true)]
    [InlineData("restriction-inside", true, true)]
    [InlineData("restriction-ends-at-start", false, false)]
    [InlineData("restriction-starts-at-end", false, false)]
    [InlineData("dbs-expires-inside", false, true)]
    [InlineData("dbs-expires-inside", true, true)]
    [InlineData("dbs-expires-at-end", false, false)]
    [InlineData("dbs-expires-after-end", false, false)]
    public async Task BookingRequiresEligibilityForTheFullVisit(string scenario, bool additionalWorker, bool blocked)
    {
        await factory.EnsureClinicalSeedAsync();
        var branch=Guid.NewGuid(); var person=Guid.NewGuid(); var primary=Guid.NewGuid(); var secondary=Guid.NewGuid();
        var actor=$"visit.interval.{Guid.NewGuid():N}";
        // Whole seconds avoid PostgreSQL timestamp rounding affecting boundaries.
        var starts=DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddDays(40).ToUnixTimeSeconds());
        var ends=starts.AddMinutes(60);
        var target=additionalWorker?secondary:primary;
        using var scope=factory.Services.CreateScope();
        var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
        db.Branches.Add(new(branch,TenantDefaults.OrganizationId,"Interval regression branch","Test","Active"));
        db.ServiceUsers.Add(new(person,"Fictional interval person",new DateOnly(1950,1,1),"1","Care","Contact","",RiskLevel.Low,
            "Active","Address","None","None","Private","","","Independent","Independent","Verbal","Routine","Standard",TenantDefaults.OrganizationId,branch));
        foreach(var id in new[]{primary,secondary})
            db.CareWorkers.Add(new(id,"Interval worker","Care","Flexible",0,0,"Valid","Compliant","Local",TenantDefaults.OrganizationId,branch));
        db.AppUsers.Add(new(Guid.NewGuid(),actor,actor+"@test.invalid",PasswordHasher.HashPassword("Admin123!"),UserRole.CareManager,true,TenantDefaults.OrganizationId,branch));
        await db.SaveChangesAsync();
        if(scenario.StartsWith("restriction"))
        {
            var restrictionStart=scenario=="restriction-starts-at-end"?ends:scenario=="restriction-ends-at-start"?starts.AddHours(-1):starts.AddMinutes(30);
            var restrictionEnd=scenario=="restriction-ends-at-start"?starts:ends.AddHours(1);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                insert into worker_assignment_restrictions(id,care_worker_id,organization_id,branch_id,restriction_type,skill,reason,evidence_reference,starts_at,ends_at,status,approved_by)
                values({Guid.NewGuid()},{target},{TenantDefaults.OrganizationId},{branch},{"Assignment"},{""},{"Synthetic interval restriction"},{"TEST"},{restrictionStart},{restrictionEnd},{"Active"},{actor})
                """);
        }
        else
        {
            var expiry=scenario=="dbs-expires-inside"?starts.AddMinutes(30):scenario=="dbs-expires-at-end"?ends:ends.AddMinutes(1);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                insert into worker_compliance_records(id,care_worker_id,organization_id,branch_id,compliance_type,status,expires_at)
                values({Guid.NewGuid()},{target},{TenantDefaults.OrganizationId},{branch},{"DBS"},{"Valid"},{expiry})
                """);
        }
        using var client=factory.CreateClient();
        var login=await client.PostAsJsonAsync("/api/auth/login",new{userName=actor,password="Admin123!",mfaCode=(string?)null});
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await login.Content.ReadFromJsonAsync<Login>())!.Token);
        var request=new{serviceUserId=person,careWorkerId=primary,additionalCareWorkerIds=additionalWorker?new[]{secondary}:Array.Empty<Guid>(),
            startsAt=starts,visitType="Interval regression",durationMinutes=60,requiredSkills=""};
        var preflight=await client.PostAsJsonAsync("/api/phase1/visits/conflicts",request);
        Assert.Equal(HttpStatusCode.OK,preflight.StatusCode);
        var body=await preflight.Content.ReadAsStringAsync();
        var code=scenario.StartsWith("restriction")?"assignment-restricted":"dbs-invalid";
        if(blocked) Assert.Contains(code,body); else Assert.DoesNotContain(code,body);
        var booking=await client.PostAsJsonAsync("/api/phase1/visits",request);
        Assert.Equal(blocked?HttpStatusCode.BadRequest:HttpStatusCode.Created,booking.StatusCode);
        Assert.Equal(blocked?0:1,await db.Visits.CountAsync(v=>v.ServiceUserId==person));
        if(blocked) Assert.False(await db.AuditEvents.AnyAsync(a=>a.Actor==actor&&a.EntityType==nameof(Visit)));
    }
    private sealed record Login(string Token);
}
