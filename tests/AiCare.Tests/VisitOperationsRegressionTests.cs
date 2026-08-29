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
public sealed class VisitOperationsRegressionTests(PostgresRegressionFactory factory):IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task AssignedWorkerAndManagerCompleteAuditedExceptionHandoverAndHistoryWorkflow()
    {
        await factory.EnsureClinicalSeedAsync();var workerId=Guid.NewGuid();var otherWorkerId=Guid.NewGuid();var personId=Guid.NewGuid();var visitId=Guid.NewGuid();var assignedName=$"visit.worker.{Guid.NewGuid():N}";var otherName=$"other.worker.{Guid.NewGuid():N}";var adminName=$"visit.manager.{Guid.NewGuid():N}";var starts=new DateTimeOffset(2032,1,12,10,0,0,TimeSpan.Zero);
        using(var scope=factory.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();db.CareWorkers.AddRange(new CareWorker(workerId,"Assigned Visit Worker","Personal care","Flexible",0,0,"Valid","Compliant","10 miles",TenantDefaults.OrganizationId,TenantDefaults.BranchId),new CareWorker(otherWorkerId,"Other Visit Worker","Personal care","Flexible",0,0,"Valid","Compliant","10 miles",TenantDefaults.OrganizationId,TenantDefaults.BranchId));db.ServiceUsers.Add(new ServiceUser(personId,"Phase Three Person",new DateOnly(1975,1,1),"+10000000003","Personal care","Contact","",RiskLevel.Low,"Onboarded","Address","None","None","Private","Regression","","Independent","Independent","Verbal","None","Standard",TenantDefaults.OrganizationId,TenantDefaults.BranchId));db.Visits.Add(new Visit(visitId,personId,workerId,starts,"Phase three visit",45,"Personal care",VisitStatus.Scheduled,null,null,null,null,null,null,TenantDefaults.OrganizationId,TenantDefaults.BranchId));db.AppUsers.AddRange(User(assignedName,UserRole.CareWorker,workerId),User(otherName,UserRole.CareWorker,otherWorkerId),User(adminName,UserRole.Administrator,null));await db.SaveChangesAsync();}
        var assigned=await Client(assignedName);var other=await Client(otherName);var admin=await Client(adminName);
        Assert.Equal(HttpStatusCode.NotFound,(await other.GetAsync($"/api/phase1/visit-operations/visits/{visitId}")).StatusCode);
        var created=await assigned.PostAsJsonAsync($"/api/phase1/visit-operations/visits/{visitId}/exceptions",new{exceptionType="NoAccess",severity="High",reason="No answer at the property",immediateAction="Coordinator called and welfare procedure started",notifyManager=true,followUpOwner="Duty manager",escalationDueAt=(DateTimeOffset?)null});Assert.Equal(HttpStatusCode.Created,created.StatusCode);var exceptionId=(await created.Content.ReadFromJsonAsync<Created>())!.Id;
        Assert.Equal(HttpStatusCode.Forbidden,(await assigned.PatchAsJsonAsync($"/api/phase1/visit-operations/exceptions/{exceptionId}",new{action="Close",detail="Worker attempted closure"})).StatusCode);
        var handover=await assigned.PostAsJsonAsync($"/api/phase1/visit-operations/visits/{visitId}/handovers",new{summary="No-access welfare follow-up",outstandingActions="Duty manager must confirm welfare",urgent=true,attachmentReference=""});Assert.Equal(HttpStatusCode.Created,handover.StatusCode);var handoverId=(await handover.Content.ReadFromJsonAsync<Created>())!.Id;Assert.Equal(HttpStatusCode.NoContent,(await assigned.PostAsync($"/api/phase1/visit-operations/handovers/{handoverId}/acknowledge",null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await admin.PatchAsJsonAsync($"/api/phase1/visit-operations/exceptions/{exceptionId}",new{action="Acknowledge",detail="Duty manager accepted ownership"})).StatusCode);Assert.Equal(HttpStatusCode.NoContent,(await admin.PatchAsJsonAsync($"/api/phase1/visit-operations/exceptions/{exceptionId}",new{action="Resolve",detail="Welfare confirmed by family"})).StatusCode);Assert.Equal(HttpStatusCode.NoContent,(await admin.PatchAsJsonAsync($"/api/phase1/visit-operations/exceptions/{exceptionId}",new{action="Close",detail="Follow-up complete"})).StatusCode);
        var reschedule=await admin.PutAsJsonAsync($"/api/phase1/visits/{visitId}",new{serviceUserId=personId,careWorkerId=workerId,startsAt=starts.AddHours(2),visitType="Phase three visit",durationMinutes=45,requiredSkills="Personal care",additionalCareWorkerIds=Array.Empty<Guid>(),changeReason="Family requested later call"});Assert.True(reschedule.StatusCode==HttpStatusCode.OK,$"Reschedule returned {(int)reschedule.StatusCode}: {await reschedule.Content.ReadAsStringAsync()}");
        var workspace=await admin.GetAsync($"/api/phase1/visit-operations/visits/{visitId}");Assert.Equal(HttpStatusCode.OK,workspace.StatusCode);var body=await workspace.Content.ReadAsStringAsync();Assert.Contains("NoAccess",body);Assert.Contains("No-access welfare follow-up",body);Assert.Contains("Family requested later call",body);Assert.Contains("Closed",body);
        var dashboard=await admin.GetAsync("/api/phase1/visit-operations/dashboard");Assert.Equal(HttpStatusCode.OK,dashboard.StatusCode);
        using var verify=factory.Services.CreateScope();var verifyDb=verify.ServiceProvider.GetRequiredService<CareDbContext>();Assert.True(await verifyDb.AuditEvents.CountAsync(x=>x.EntityId==exceptionId)>=4);Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.EntityId==handoverId&&x.Action=="visit_handover.created"));
    }
    private static AppUser User(string name,UserRole role,Guid? worker)=>new(Guid.NewGuid(),name,$"{name}@aicare.local",PasswordHasher.HashPassword("Admin123!"),role,true,TenantDefaults.OrganizationId,TenantDefaults.BranchId,worker,null);
    private async Task<HttpClient> Client(string name){var client=factory.CreateClient();var response=await client.PostAsJsonAsync("/api/auth/login",new{userName=name,password="Admin123!",mfaCode=(string?)null});response.EnsureSuccessStatusCode();client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await response.Content.ReadFromJsonAsync<Login>())!.Token);return client;}
    private sealed record Login(string Token);private sealed record Created(Guid Id);
}
