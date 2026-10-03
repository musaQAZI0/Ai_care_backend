using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed class InitialMedicationVerificationRegressionTests : IClassFixture<PostgresRegressionFactory>
{
    private readonly PostgresRegressionFactory factory;
    public InitialMedicationVerificationRegressionTests(PostgresRegressionFactory factory) => this.factory=factory;

    [Fact]
    public async Task NewMedicationNeedsASeparateReviewerBeforeScheduling()
    {
        await factory.EnsureClinicalSeedAsync();
        var medicationId=Guid.NewGuid();
        var managerName="initial.review."+Guid.NewGuid().ToString("N");
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Medications.Add(new Medication(medicationId,RegressionIds.ServiceUserId,"Initial review medicine",
                "250 mg","Oral","Morning",false,"Pharmacy","None",TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            db.AppUsers.Add(new AppUser(Guid.NewGuid(),managerName,managerName+"@aicare.local",
                PasswordHasher.HashPassword("Manager123!"),UserRole.CareManager,true,
                TenantDefaults.OrganizationId,TenantDefaults.BranchId,null,null));
            await db.SaveChangesAsync();
        }
        var preparer=await SignIn("admin","Admin123!");
        var reviewer=await SignIn(managerName,"Manager123!");
        var path=$"/api/phase1/medication-safety/medications/{medicationId}/profile";
        var content=new {
            indication="Regression treatment",prescriber="Dr Regression",form="Tablet",strength="250 mg",
            doseUnit="tablet",frequency="Morning",administrationInstructions="Give with breakfast",
            startDate=DateTimeOffset.UtcNow.AddDays(-1),endDate=(DateTimeOffset?)null,doseWindowMinutes=60,
            maxPrnDoses24h=(int?)null,minPrnIntervalMinutes=(int?)null,prnIndication="Not applicable",
            prnEffectReviewMinutes=(int?)null,stockOnHand=20m,reorderLevel=5m,requiresWitness=false,
            lastReconciledAt=DateTimeOffset.UtcNow,reconciledBy="Client supplied reviewer",
            reconciliationStatus="Verified",sourceType="Prescription",sourceReference="RX-INITIAL",
            changeReason="Initial reconciliation"
        };
        await StepUpTestGrants.GrantAsync(factory,preparer,"medication");
        var direct=await preparer.PutAsJsonAsync(path,content);
        Assert.Equal(HttpStatusCode.Conflict,direct.StatusCode);
        var absent=await preparer.GetStringAsync(path);
        Assert.True(string.IsNullOrEmpty(absent));

        var draftBody=JsonSerializer.SerializeToNode(content)!;
        draftBody["reconciliationStatus"]="Draft";
        await StepUpTestGrants.GrantAsync(factory,preparer,"medication");
        var draft=await preparer.PutAsJsonAsync(path,draftBody);
        Assert.True(draft.StatusCode==HttpStatusCode.OK,await draft.Content.ReadAsStringAsync());
        var saved=await preparer.GetStringAsync(path);
        Assert.Contains("\"reconciliationStatus\":\"Draft\"",saved);
        Assert.DoesNotContain("Client supplied reviewer",saved);
        await StepUpTestGrants.GrantAsync(factory,preparer,"medication");
        var blocked=await preparer.PostAsJsonAsync("/api/phase1/mar",new{
            medicationId,visitId=RegressionIds.VisitId,careWorkerId=RegressionIds.WorkerId,
            scheduledAt=DateTimeOffset.UtcNow.AddHours(1),notes="Before review"});
        Assert.Equal(HttpStatusCode.BadRequest,blocked.StatusCode);

        await StepUpTestGrants.GrantAsync(factory,preparer,"medication");
        var submission=await preparer.PostAsync(path+"/submit",null);
        Assert.True(submission.StatusCode==HttpStatusCode.Created,await submission.Content.ReadAsStringAsync());
        using var submissionJson=JsonDocument.Parse(await submission.Content.ReadAsStringAsync());
        var reviewId=submissionJson.RootElement.GetProperty("id").GetGuid();
        await StepUpTestGrants.GrantAsync(factory,preparer,"medication");
        var duplicate=await preparer.PostAsync(path+"/submit",null);
        Assert.Equal(HttpStatusCode.Conflict,duplicate.StatusCode);
        await StepUpTestGrants.GrantAsync(factory,preparer,"medication");
        var editPending=await preparer.PutAsJsonAsync(path,draftBody);
        Assert.Equal(HttpStatusCode.Conflict,editPending.StatusCode);
        await StepUpTestGrants.GrantAsync(factory,preparer,"medication");
        var self=await preparer.PostAsJsonAsync(path+"/reviews/"+reviewId,
            new{decision="Approve",reason="I prepared this"});
        Assert.Equal(HttpStatusCode.Conflict,self.StatusCode);
        await StepUpTestGrants.GrantAsync(factory,reviewer,"medication");
        var approved=await reviewer.PostAsJsonAsync(path+"/reviews/"+reviewId,
            new{decision="Approve",reason="Matched signed prescription RX-INITIAL"});
        Assert.True(approved.StatusCode==HttpStatusCode.OK,await approved.Content.ReadAsStringAsync());
        var verified=await reviewer.GetStringAsync(path);
        Assert.Contains("\"reconciliationStatus\":\"Verified\"",verified);
        Assert.Contains(managerName,verified);
        await StepUpTestGrants.GrantAsync(factory,preparer,"medication");
        var editVerified=await preparer.PutAsJsonAsync(path,draftBody);
        Assert.Equal(HttpStatusCode.Conflict,editVerified.StatusCode);
        await StepUpTestGrants.GrantAsync(factory,reviewer,"medication");
        var duplicateDecision=await reviewer.PostAsJsonAsync(path+"/reviews/"+reviewId,
            new{decision="Reject",reason="Too late"});
        Assert.Equal(HttpStatusCode.Conflict,duplicateDecision.StatusCode);
        await StepUpTestGrants.GrantAsync(factory,preparer,"medication");
        var scheduled=await preparer.PostAsJsonAsync("/api/phase1/mar",new{
            medicationId,visitId=RegressionIds.VisitId,careWorkerId=RegressionIds.WorkerId,
            scheduledAt=DateTimeOffset.UtcNow.AddHours(1),notes="After review"});
        Assert.True(scheduled.StatusCode==HttpStatusCode.Created,await scheduled.Content.ReadAsStringAsync());
        var reviews=await reviewer.GetStringAsync(path+"/reviews");
        Assert.Contains("Matched signed prescription RX-INITIAL",reviews);
    }

    [Fact]
    public async Task RejectedProfileCanBeCorrectedAndResubmittedWithoutCrossBranchReview()
    {
        await factory.EnsureClinicalSeedAsync();
        var medicationId = Guid.NewGuid();
        var reviewerName = "initial.second." + Guid.NewGuid().ToString("N");
        var otherBranchName = "initial.other." + Guid.NewGuid().ToString("N");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Medications.Add(new Medication(medicationId, RegressionIds.ServiceUserId, "Review cycle medicine",
                "5 mg", "Oral", "Morning", false, "Pharmacy", "None", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.AppUsers.AddRange(
                new AppUser(Guid.NewGuid(), reviewerName, reviewerName + "@aicare.local", PasswordHasher.HashPassword("Manager123!"),
                    UserRole.CareManager, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId, null, null),
                new AppUser(Guid.NewGuid(), otherBranchName, otherBranchName + "@aicare.local", PasswordHasher.HashPassword("Manager123!"),
                    UserRole.CareManager, true, TenantDefaults.OrganizationId, Guid.NewGuid(), null, null));
            await db.SaveChangesAsync();
        }
        var preparer = await SignIn("admin", "Admin123!");
        var reviewer = await SignIn(reviewerName, "Manager123!");
        var outsider = await SignIn(otherBranchName, "Manager123!");
        var path = $"/api/phase1/medication-safety/medications/{medicationId}/profile";
        var body = new {
            indication = "Symptom management", prescriber = "Dr Original", form = "Tablet", strength = "5 mg",
            doseUnit = "tablet", frequency = "Morning", administrationInstructions = "With water",
            doseWindowMinutes = 60, stockOnHand = 10m, reorderLevel = 2m, requiresWitness = false,
            prnIndication = "", reconciliationStatus = "Draft", sourceType = "Prescription",
            sourceReference = "RX-REVIEW-1", changeReason = "Initial reconciliation"
        };
        await StepUpTestGrants.GrantAsync(factory, preparer, "medication");
        var draft = await preparer.PutAsJsonAsync(path, body);
        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);
        await StepUpTestGrants.GrantAsync(factory, preparer, "medication");
        var submitted = await preparer.PostAsync(path + "/submit", null);
        Assert.Equal(HttpStatusCode.Created, submitted.StatusCode);
        using var firstJson = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync());
        var firstId = firstJson.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(path + "/reviews")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync(path + "/reviews/" + firstId,
            new { decision = "Approve", reason = "Other branch" })).StatusCode);
        await StepUpTestGrants.GrantAsync(factory, reviewer, "medication");
        var rejected = await reviewer.PostAsJsonAsync(path + "/reviews/" + firstId,
            new { decision = "Reject", reason = "Confirm prescriber against signed order" });
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Contains("\"reconciliationStatus\":\"Draft\"", await preparer.GetStringAsync(path));
        await StepUpTestGrants.GrantAsync(factory, reviewer, "medication");
        Assert.Equal(HttpStatusCode.Conflict, (await reviewer.PostAsJsonAsync(path + "/reviews/" + firstId,
            new { decision = "Approve", reason = "Second decision" })).StatusCode);
        await StepUpTestGrants.GrantAsync(factory, preparer, "medication");
        var corrected = await preparer.PutAsJsonAsync(path, new { body.indication, prescriber = "Dr Confirmed", body.form, body.strength, body.doseUnit, body.frequency, body.administrationInstructions, body.doseWindowMinutes, body.stockOnHand, body.reorderLevel, body.requiresWitness, body.prnIndication, body.reconciliationStatus, body.sourceType, sourceReference = "RX-REVIEW-2", body.changeReason });
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        await StepUpTestGrants.GrantAsync(factory, preparer, "medication");
        var resubmitted = await preparer.PostAsync(path + "/submit", null);
        Assert.Equal(HttpStatusCode.Created, resubmitted.StatusCode);
        using var secondJson = JsonDocument.Parse(await resubmitted.Content.ReadAsStringAsync());
        var secondId = secondJson.RootElement.GetProperty("id").GetGuid();
        Assert.NotEqual(firstId, secondId);
        await StepUpTestGrants.GrantAsync(factory, reviewer, "medication");
        var approved = await reviewer.PostAsJsonAsync(path + "/reviews/" + secondId,
            new { decision = "Approve", reason = "Matched corrected signed order RX-REVIEW-2" });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var profile = await reviewer.GetStringAsync(path);
        Assert.Contains("\"reconciliationStatus\":\"Verified\"", profile);
        Assert.Contains("Dr Confirmed", profile);
        var reviews = await reviewer.GetStringAsync(path + "/reviews");
        Assert.Contains("RX-REVIEW-2", reviews);
        Assert.Contains("\"status\":\"Rejected\"", reviews);
        Assert.Contains("\"status\":\"Approved\"", reviews);
    }

    private async Task<HttpClient> SignIn(string name,string password)
    {
        var client=factory.CreateClient();
        var response=await client.PostAsJsonAsync("/api/auth/login",new{userName=name,password,mfaCode=(string?)null});
        response.EnsureSuccessStatusCode();
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",json.RootElement.GetProperty("token").GetString());
        return client;
    }
}
