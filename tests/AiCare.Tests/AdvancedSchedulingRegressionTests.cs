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
public sealed class AdvancedSchedulingRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task AbsenceWorkingTimeTravelRestAndDoubleUpAreEnforced()
    {
        await factory.EnsureClinicalSeedAsync();
        var workers = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var people = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var userName = $"advanced.scheduling.{Guid.NewGuid():N}";
        var baseTime = new DateTimeOffset(2031, 3, 10, 12, 0, 0, TimeSpan.Zero);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            foreach (var worker in workers) db.CareWorkers.Add(new CareWorker(worker,$"Advanced Worker {worker:N}","Medication administration","Flexible",0,0,"Valid","Compliant","20 miles",TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            foreach (var person in people) db.ServiceUsers.Add(new ServiceUser(person,$"Advanced Person {person:N}",new DateOnly(1980,1,1),"+10000000002","Medication support","Contact","",RiskLevel.Low,"Onboarded","Address","None","None","Private","Regression","","Independent","Independent","Verbal","None","Standard",TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            db.AppUsers.Add(new AppUser(Guid.NewGuid(),userName,$"{userName}@aicare.local",PasswordHasher.HashPassword("Admin123!"),UserRole.Administrator,true,TenantDefaults.OrganizationId,TenantDefaults.BranchId,null,null));
            db.Visits.Add(new Visit(Guid.NewGuid(),people[1],workers[1],baseTime.AddMinutes(-35),"Prior travel visit",30,"",VisitStatus.Scheduled,null,null,null,null,null,null,TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            db.Visits.Add(new Visit(Guid.NewGuid(),people[0],workers[2],baseTime.AddHours(-3),"Daily hours",150,"",VisitStatus.Scheduled,null,null,null,null,null,null,TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            db.Visits.Add(new Visit(Guid.NewGuid(),people[0],workers[3],baseTime.AddHours(-13),"Previous day shift",60,"",VisitStatus.Scheduled,null,null,null,null,null,null,TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            await db.SaveChangesAsync();
        }

        var client=factory.CreateClient();
        var login=await client.PostAsJsonAsync("/api/auth/login",new{userName,password="Admin123!",mfaCode=(string?)null});
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await login.Content.ReadFromJsonAsync<LoginResponse>())!.Token);
        var policy=await client.PutAsJsonAsync("/api/phase1/scheduling/policy",new{minimumRestMinutes=660,maximumDailyMinutes=160,maximumWeeklyMinutes=1000,travelBufferMinutes=15,maximumContinuousMinutes=120,requiredBreakMinutes=45});
        Assert.Equal(HttpStatusCode.OK,policy.StatusCode);
        var absence=await client.PostAsJsonAsync($"/api/phase1/scheduling/care-workers/{workers[0]}/absences",new{absenceType="Sickness",startsAt=baseTime.AddHours(-1),endsAt=baseTime.AddHours(2),status="Approved",notes="Regression"});
        Assert.Equal(HttpStatusCode.Created,absence.StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync($"/api/phase1/scheduling/care-workers/{workers[0]}/absences")).StatusCode);

        async Task<string> Conflicts(Guid worker,DateTimeOffset at,int duration=30,Guid? extra=null)
        {
            var response=await client.PostAsJsonAsync("/api/phase1/visits/conflicts",new{serviceUserId=people[0],careWorkerId=worker,additionalCareWorkerIds=extra is null?Array.Empty<Guid>():new[]{extra.Value},startsAt=at,visitType="Advanced scheduling",durationMinutes=duration,requiredSkills="Medication administration"});
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);return await response.Content.ReadAsStringAsync();
        }
        Assert.Contains("worker-absent",await Conflicts(workers[0],baseTime));
        Assert.Contains("travel-time",await Conflicts(workers[1],baseTime));
        Assert.Contains("daily-hours",await Conflicts(workers[2],baseTime));
        Assert.Contains("insufficient-break",await Conflicts(workers[2],baseTime));
        Assert.Contains("minimum-rest",await Conflicts(workers[3],baseTime.AddHours(-2)));

        var blockedDoubleUp=await client.PostAsJsonAsync("/api/phase1/visits",new{serviceUserId=people[0],careWorkerId=workers[4],additionalCareWorkerIds=new[]{workers[0]},startsAt=baseTime,visitType="Double-up blocked",durationMinutes=30,requiredSkills="Medication administration"});
        Assert.Equal(HttpStatusCode.BadRequest,blockedDoubleUp.StatusCode);

        await client.PutAsJsonAsync("/api/phase1/scheduling/policy",new{minimumRestMinutes=0,maximumDailyMinutes=720,maximumWeeklyMinutes=2880,travelBufferMinutes=0,maximumContinuousMinutes=360,requiredBreakMinutes=20});
        var create=await client.PostAsJsonAsync("/api/phase1/visits",new{serviceUserId=people[0],careWorkerId=workers[4],additionalCareWorkerIds=new[]{workers[1]},startsAt=baseTime.AddDays(10),visitType="Double-up allowed",durationMinutes=30,requiredSkills="Medication administration"});
        Assert.Equal(HttpStatusCode.Created,create.StatusCode);
        using var document=JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var visitId=document.RootElement.GetProperty("id").GetGuid();
        using var verifyScope=factory.Services.CreateScope();
        var verifyDb=verifyScope.ServiceProvider.GetRequiredService<CareDbContext>();
        var assignmentCount=await verifyDb.Database.SqlQueryRaw<int>("select count(*)::int as \"Value\" from visit_care_worker_assignments where visit_id={0} and care_worker_id={1}",visitId,workers[1]).SingleAsync();
        Assert.Equal(1,assignmentCount);
    }

    private sealed record LoginResponse(string Token);
}
