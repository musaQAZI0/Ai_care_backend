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
public sealed class CareDocumentationRegressionTests(PostgresRegressionFactory factory):IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task CareDocumentationGovernanceCreatesAuditedAlertsAmendmentsReviewsAndTimeline()
    {
        await factory.EnsureClinicalSeedAsync();
        var workerId=Guid.NewGuid();var otherWorkerId=Guid.NewGuid();var personId=Guid.NewGuid();var visitId=Guid.NewGuid();var taskId=Guid.NewGuid();var noteId=Guid.NewGuid();
        var workerName=$"phase4.worker.{Guid.NewGuid():N}";var otherName=$"phase4.other.{Guid.NewGuid():N}";var adminName=$"phase4.admin.{Guid.NewGuid():N}";
        var starts=new DateTimeOffset(2032,2,1,9,0,0,TimeSpan.Zero);

        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.CareWorkers.AddRange(new CareWorker(workerId,"Phase Four Worker","Personal care","Flexible",0,0,"Valid","Compliant","10 miles",TenantDefaults.OrganizationId,TenantDefaults.BranchId),new CareWorker(otherWorkerId,"Unassigned Phase Four Worker","Personal care","Flexible",0,0,"Valid","Compliant","10 miles",TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            db.ServiceUsers.Add(new ServiceUser(personId,"Phase Four Person",new DateOnly(1978,4,4),"+10000000004","Personal care","Contact","",RiskLevel.Low,"Onboarded","Address","None","None","Private","Regression","","Independent","Independent","Verbal","None","Standard",TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            db.Visits.Add(new Visit(visitId,personId,workerId,starts,"Phase four visit",45,"Personal care",VisitStatus.Scheduled,null,null,null,null,null,null,TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            db.CareNotes.Add(new CareNote(noteId,visitId,personId,workerId,"Baseline care delivered","Washed and dressed","Tea offered","Prompt completed","Raised temperature",true,DateTimeOffset.UtcNow,TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            db.AppUsers.AddRange(User(workerName,UserRole.CareWorker,workerId),User(otherName,UserRole.CareWorker,otherWorkerId),User(adminName,UserRole.Administrator,null));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("insert into visit_tasks(id,visit_id,care_plan_task_id,service_user_id,care_worker_id,organization_id,branch_id,title,category,instructions,is_required,status,outcome,exception_reason,completed_at,created_at) values({0},{1},null,{2},{3},{4},{5},{6},{7},{8},true,'Pending','','',null,now())",taskId,visitId,personId,workerId,TenantDefaults.OrganizationId,TenantDefaults.BranchId,"Medication prompt","Medication","Prompt prescribed medication");
            await db.SaveChangesAsync();
        }

        var worker=await Client(workerName);var other=await Client(otherName);var admin=await Client(adminName);

        var threshold=await admin.PutAsJsonAsync("/api/phase1/care-documentation/thresholds/Temperature",new{minimumValue=(decimal?)null,maximumValue=38m,severity="High",instructions="Call the duty manager"});
        Assert.Equal(HttpStatusCode.OK,threshold.StatusCode);

        var observation=await worker.PostAsJsonAsync($"/api/phase1/visits/{visitId}/delivery/observations",new{observationType="Temperature",value="39.2",unit="C",notes="Temperature above agreed range",recordedAt=(DateTimeOffset?)null});
        Assert.Equal(HttpStatusCode.Created,observation.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound,(await other.GetAsync($"/api/phase1/care-documentation/visits/{visitId}/alerts")).StatusCode);
        var alerts=await worker.GetAsync($"/api/phase1/care-documentation/visits/{visitId}/alerts");
        Assert.Equal(HttpStatusCode.OK,alerts.StatusCode);
        var alertId=(await alerts.Content.ReadFromJsonAsync<List<AlertRow>>())!.Single().Id;


        var missingReason=await worker.PostAsJsonAsync($"/api/phase1/care-documentation/notes/{noteId}/amendments",new{reason="",summary="Updated care note",personalCare="Washed and dressed",mealsAndHydration="Tea offered",medication="Prompt completed",concerns="Raised temperature",requiresReview=true});
        Assert.Equal(HttpStatusCode.BadRequest,missingReason.StatusCode);
        var amended=await worker.PostAsJsonAsync($"/api/phase1/care-documentation/notes/{noteId}/amendments",new{reason="Late clarification from worker",summary="Updated care note",personalCare="Washed and dressed",mealsAndHydration="Tea accepted",medication="Prompt completed",concerns="Raised temperature",requiresReview=true});
        Assert.Equal(HttpStatusCode.Created,amended.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await worker.PostAsJsonAsync($"/api/phase1/care-documentation/notes/{noteId}/reviews",new{status="Approved",comment="Worker cannot approve"})).StatusCode);
        Assert.Equal(HttpStatusCode.Created,(await admin.PostAsJsonAsync($"/api/phase1/care-documentation/notes/{noteId}/reviews",new{status="Approved",comment="Reviewed by manager"})).StatusCode);

        var taskOutcome=await worker.PostAsJsonAsync($"/api/phase1/visits/{visitId}/tasks/{taskId}/outcome",new{outcome="Refused",exceptionReason="Person declined medication prompt"});
        Assert.Equal(HttpStatusCode.OK,taskOutcome.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,(await admin.PatchAsJsonAsync($"/api/phase1/care-documentation/alerts/{alertId}",new{action="Acknowledge",immediateAction="Worker advised to re-check",externalContact="Duty manager",owner="Care coordinator"})).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await admin.PatchAsJsonAsync($"/api/phase1/care-documentation/alerts/{alertId}",new{action="Resolve",immediateAction="Follow-up completed",externalContact="GP updated",owner="Care coordinator"})).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound,(await other.GetAsync($"/api/phase1/care-documentation/service-users/{personId}/timeline")).StatusCode);
        var timeline=await admin.GetStringAsync($"/api/phase1/care-documentation/service-users/{personId}/timeline");
        Assert.Contains("CareNote",timeline);Assert.Contains("Observation",timeline);Assert.Contains("TaskException",timeline);Assert.Contains("Deterioration",timeline);

        using var verify=factory.Services.CreateScope();
        var verifyDb=verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="care_note.reviewed"));
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="care_note.amended"));
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.EntityId==alertId&&x.Action=="deterioration_alert.resolved"));
    }

    private static AppUser User(string name,UserRole role,Guid? worker)=>new(Guid.NewGuid(),name,$"{name}@aicare.local",PasswordHasher.HashPassword("Admin123!"),role,true,TenantDefaults.OrganizationId,TenantDefaults.BranchId,worker,null);
    private async Task<HttpClient> Client(string name){var client=factory.CreateClient();var response=await client.PostAsJsonAsync("/api/auth/login",new{userName=name,password="Admin123!",mfaCode=(string?)null});response.EnsureSuccessStatusCode();client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await response.Content.ReadFromJsonAsync<Login>())!.Token);return client;}
    private sealed record Login(string Token);private sealed record AlertRow(Guid Id);
}







