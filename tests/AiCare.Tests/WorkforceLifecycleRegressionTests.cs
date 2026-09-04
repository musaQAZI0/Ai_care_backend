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
public sealed class WorkforceLifecycleRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task EmploymentSupervisionCoverAndReturnToWorkAreContextuallyGovernedAndAudited()
    {
        await factory.EnsureClinicalSeedAsync();
        var workerId=Guid.NewGuid();var otherWorkerId=Guid.NewGuid();var managerName=$"workforce.manager.{Guid.NewGuid():N}";var coordinatorName=$"workforce.coordinator.{Guid.NewGuid():N}";var workerName=$"workforce.worker.{Guid.NewGuid():N}";var otherName=$"workforce.other.{Guid.NewGuid():N}";
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.CareWorkers.AddRange(new CareWorker(workerId,"Lifecycle Worker","Care","Flexible",0,0,"Valid","Compliant","Local",TenantDefaults.OrganizationId,TenantDefaults.BranchId),new CareWorker(otherWorkerId,"Other Worker","Care","Flexible",0,0,"Valid","Compliant","Local",TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            db.AppUsers.AddRange(User(managerName,UserRole.CareManager),User(coordinatorName,UserRole.CareCoordinator),User(workerName,UserRole.CareWorker,workerId),User(otherName,UserRole.CareWorker,otherWorkerId));await db.SaveChangesAsync();
        }
        async Task<HttpClient> Login(string name){var c=factory.CreateClient();var response=await c.PostAsJsonAsync("/api/auth/login",new{userName=name,password="Admin123!",mfaCode=(string?)null});response.EnsureSuccessStatusCode();c.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await response.Content.ReadFromJsonAsync<LoginResponse>())!.Token);return c;}
        var manager=await Login(managerName);var coordinator=await Login(coordinatorName);var worker=await Login(workerName);var other=await Login(otherName);
        var root=$"/api/phase1/workforce-lifecycle/care-workers/{workerId}";
        Assert.Equal(HttpStatusCode.NotFound,(await other.GetAsync(root)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await worker.GetAsync(root)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await coordinator.PutAsJsonAsync($"{root}/employment",new{recruitmentStatus="Cleared",employmentStatus="Employed",contractType="Permanent",jobTitle="Care worker",contractedWeeklyMinutes=2100,startDate=new DateOnly(2026,1,1),endDate=(DateOnly?)null,lineManager="Manager",statusReason=""})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await manager.PutAsJsonAsync($"{root}/employment",new{recruitmentStatus="Cleared",employmentStatus="Leaver",contractType="Permanent",jobTitle="Care worker",contractedWeeklyMinutes=2100,startDate=new DateOnly(2026,1,1),endDate=(DateOnly?)null,lineManager="Manager",statusReason="Resigned"})).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await manager.PutAsJsonAsync($"{root}/employment",new{recruitmentStatus="Cleared",employmentStatus="Employed",contractType="Permanent",jobTitle="Care worker",contractedWeeklyMinutes=2100,startDate=new DateOnly(2026,1,1),endDate=(DateOnly?)null,lineManager="Manager",statusReason=""})).StatusCode);

        var scheduled=await coordinator.PostAsJsonAsync($"{root}/supervisions",new{recordType="Appraisal",scheduledAt=DateTimeOffset.UtcNow.AddDays(1),supervisor="Manager"});Assert.Equal(HttpStatusCode.Created,scheduled.StatusCode);var supervisionId=JsonDocument.Parse(await scheduled.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest,(await manager.PostAsJsonAsync($"{root}/supervisions/{supervisionId}/complete",new{discussion="Objectives",outcome="",actions="Review",evidenceReference="",reviewDue=(DateOnly?)null})).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await coordinator.PostAsJsonAsync($"{root}/supervisions/{supervisionId}/complete",new{discussion="Objectives",outcome="Met",actions="Review",evidenceReference="APP-1",reviewDue=DateOnly.FromDateTime(DateTime.UtcNow.AddDays(20))})).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await manager.PostAsJsonAsync($"{root}/supervisions/{supervisionId}/complete",new{discussion="Objectives",outcome="Met",actions="Review",evidenceReference="APP-1",reviewDue=DateOnly.FromDateTime(DateTime.UtcNow.AddDays(20))})).StatusCode);

        var absence=await coordinator.PostAsJsonAsync($"/api/phase1/scheduling/care-workers/{workerId}/absences",new{absenceType="Sickness",startsAt=DateTimeOffset.UtcNow.AddDays(-3),endsAt=DateTimeOffset.UtcNow.AddDays(-1),status="Approved",notes="Illness"});Assert.Equal(HttpStatusCode.Created,absence.StatusCode);var absenceId=JsonDocument.Parse(await absence.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest,(await coordinator.PutAsJsonAsync($"{root}/absences/{absenceId}/cover",new{coverStatus="Covered",coveredBy=(Guid?)null,notes=""})).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await coordinator.PutAsJsonAsync($"{root}/absences/{absenceId}/cover",new{coverStatus="Covered",coveredBy=otherWorkerId,notes="Cover confirmed"})).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await coordinator.PostAsJsonAsync($"{root}/absences/{absenceId}/return-to-work",new{meetingAt=DateTimeOffset.UtcNow,fitToReturn=true,adjustments="",restrictions="",evidenceReference="RTW-1",reviewDue=(DateOnly?)null})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await manager.PostAsJsonAsync($"{root}/absences/{absenceId}/return-to-work",new{meetingAt=DateTimeOffset.UtcNow,fitToReturn=false,adjustments="",restrictions="",evidenceReference="RTW-1",reviewDue=(DateOnly?)null})).StatusCode);
        Assert.Equal(HttpStatusCode.Created,(await manager.PostAsJsonAsync($"{root}/absences/{absenceId}/return-to-work",new{meetingAt=DateTimeOffset.UtcNow,fitToReturn=true,adjustments="Phased first shift",restrictions="",evidenceReference="RTW-1",reviewDue=DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7))})).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await manager.PostAsJsonAsync($"{root}/absences/{absenceId}/return-to-work",new{meetingAt=DateTimeOffset.UtcNow,fitToReturn=true,adjustments="",restrictions="",evidenceReference="RTW-2",reviewDue=(DateOnly?)null})).StatusCode);

        var payload=JsonDocument.Parse(await (await worker.GetAsync(root)).Content.ReadAsStringAsync()).RootElement;Assert.Equal("Employed",payload.GetProperty("employment").GetProperty("employmentStatus").GetString());Assert.Contains(payload.GetProperty("events").EnumerateArray(),x=>x.GetProperty("eventType").GetString()=="return_to_work.completed");
        using var verify=factory.Services.CreateScope();var verifyDb=verify.ServiceProvider.GetRequiredService<CareDbContext>();Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="return_to_work.completed"&&x.EntityId!=null));
    }
    private static AppUser User(string name,UserRole role,Guid? worker=null)=>new(Guid.NewGuid(),name,$"{name}@aicare.local",PasswordHasher.HashPassword("Admin123!"),role,true,TenantDefaults.OrganizationId,TenantDefaults.BranchId,worker,null);
    private sealed record LoginResponse(string Token);
}
