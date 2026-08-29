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
public sealed class PersonLifecycleRegressionTests(PostgresRegressionFactory factory):IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task ReferralStatusDischargeReviewAndDashboardAreAudited()
    {
        await factory.EnsureClinicalSeedAsync();
        var personId=Guid.NewGuid();var adminName=$"phase5.admin.{Guid.NewGuid():N}";var workerName=$"phase5.worker.{Guid.NewGuid():N}";
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.ServiceUsers.Add(new ServiceUser(personId,"Phase Five Person",new DateOnly(1944,5,5),"+10000000005","Referral assessment","Contact","",RiskLevel.Medium,"Prospect","Address","None","Diabetes","Local Authority","","","Uses frame","Independent","Clear speech","Routine","Diabetic diet",TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            db.PersonRecords.Add(new PersonRecord(Guid.NewGuid(),personId,"Phase","she/her","NHS-5","GP","Pharmacy","Representative","Active consent","Person has capacity","","History","What matters","Remain independent","",DateTimeOffset.UtcNow,null,DateTimeOffset.UtcNow,TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            db.AppUsers.AddRange(User(adminName,UserRole.Administrator,null),User(workerName,UserRole.CareWorker,Guid.NewGuid()));
            await db.SaveChangesAsync();
        }
        var admin=await Client(adminName);var worker=await Client(workerName);
        Assert.Equal(HttpStatusCode.Forbidden,(await worker.GetAsync("/api/phase1/person-lifecycle/dashboard")).StatusCode);

        var referral=await admin.PostAsJsonAsync($"/api/phase1/person-lifecycle/service-users/{personId}/referrals",new{referralSource="Hospital discharge team",referrerName="Nurse",referrerContact="ward@example.test",referralReason="Needs discharge-to-assess support",priority="Urgent",receivedAt=(DateTimeOffset?)null,screeningDueAt=DateTimeOffset.UtcNow.AddDays(1),owner="Intake coordinator"});
        Assert.Equal(HttpStatusCode.Created,referral.StatusCode);
        var referralId=(await referral.Content.ReadFromJsonAsync<Created>())!.Id;

        Assert.Equal(HttpStatusCode.NoContent,(await admin.PatchAsJsonAsync($"/api/phase1/person-lifecycle/referrals/{referralId}",new{status="Accepted",outcome="Assessment booked",closureReason="",owner="Assessment lead"})).StatusCode);
        var review=await admin.PostAsJsonAsync($"/api/phase1/person-lifecycle/service-users/{personId}/reviews",new{checkpointType="Six week review",dueAt=DateTimeOffset.UtcNow.AddDays(-1),owner="Care manager",notes="Initial review required"});
        Assert.Equal(HttpStatusCode.Created,review.StatusCode);
        var reviewId=(await review.Content.ReadFromJsonAsync<Created>())!.Id;
        Assert.Equal(HttpStatusCode.NoContent,(await admin.PatchAsJsonAsync($"/api/phase1/person-lifecycle/reviews/{reviewId}",new{outcome="Care package confirmed",notes="No changes required"})).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await admin.PostAsJsonAsync($"/api/phase1/person-lifecycle/service-users/{personId}/status",new{status="Discharged",reason="Package ended safely",effectiveAt=DateTimeOffset.UtcNow})).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await admin.PostAsJsonAsync($"/api/phase1/person-lifecycle/service-users/{personId}/status",new{status="Archived",reason="Retention archive after discharge",effectiveAt=DateTimeOffset.UtcNow})).StatusCode);

        var summary=await admin.GetStringAsync($"/api/phase1/person-lifecycle/service-users/{personId}");
        Assert.Contains("Archived",summary);Assert.Contains("ReferralAccepted",summary);Assert.Contains("Six week review",summary);
        var dashboard=await admin.GetStringAsync("/api/phase1/person-lifecycle/dashboard");
        Assert.Contains("Archived",dashboard);

        using var verify=factory.Services.CreateScope();var verifyDb=verify.ServiceProvider.GetRequiredService<CareDbContext>();
        var person=await verifyDb.ServiceUsers.SingleAsync(x=>x.Id==personId);Assert.Equal("Archived",person.Status);
        Assert.True(await verifyDb.PersonRecords.AnyAsync(x=>x.ServiceUserId==personId&&x.DischargedAt!=null));
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="person_referral.created"&&x.EntityId==referralId));
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="person_review.completed"&&x.EntityId==reviewId));
        Assert.True(await verifyDb.AuditEvents.CountAsync(x=>x.Action=="service_user.status_changed"&&x.EntityId==personId)>=3);
    }

    private static AppUser User(string name,UserRole role,Guid? worker)=>new(Guid.NewGuid(),name,$"{name}@aicare.local",PasswordHasher.HashPassword("Admin123!"),role,true,TenantDefaults.OrganizationId,TenantDefaults.BranchId,worker,null);
    private async Task<HttpClient> Client(string name){var client=factory.CreateClient();var response=await client.PostAsJsonAsync("/api/auth/login",new{userName=name,password="Admin123!",mfaCode=(string?)null});response.EnsureSuccessStatusCode();client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await response.Content.ReadFromJsonAsync<Login>())!.Token);return client;}
    private sealed record Login(string Token);private sealed record Created(Guid Id);
}

