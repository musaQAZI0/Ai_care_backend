using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed class MedicationChangeRegressionTests : IClassFixture<PostgresRegressionFactory>
{
    private readonly PostgresRegressionFactory factory;
    public MedicationChangeRegressionTests(PostgresRegressionFactory factory) => this.factory = factory;

    [Fact]
    public async Task VerifiedRegimenNeedsIndependentReviewAndPreservesChangeEvents()
    {
        await factory.EnsureClinicalSeedAsync();
        var medicationId = RegressionIds.MedicationId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.AppUsers.Add(new AppUser(Guid.NewGuid(), "change.manager", "change.manager@aicare.local",
                PasswordHasher.HashPassword("Manager123!"), UserRole.CareManager, true,
                TenantDefaults.OrganizationId, TenantDefaults.BranchId, null, null));
            await db.SaveChangesAsync();
        }

        var requester = await SignIn("admin", "Admin123!");
        var reviewer = await SignIn("change.manager", "Manager123!");
        await StepUpTestGrants.GrantAsync(factory, requester, "medication");
        var baseline = await MedicationVerificationTestHelper.SaveAndVerifyAsync(factory, requester, medicationId, new
        {
            indication = "Pain management", prescriber = "Dr Regression", form = "Tablet", strength = "500 mg",
            doseUnit = "tablet", frequency = "As needed", administrationInstructions = "Give with food",
            startDate = DateTimeOffset.UtcNow.AddDays(-1), doseWindowMinutes = 60,
            maxPrnDoses24h = 4, minPrnIntervalMinutes = 240, prnIndication = "Pain",
            prnEffectReviewMinutes = 60, stockOnHand = 20m, reorderLevel = 5m,
            requiresWitness = false, lastReconciledAt = DateTimeOffset.UtcNow,
            reconciledBy = "Regression Admin", reconciliationStatus = "Verified",
            sourceType = "Prescription", sourceReference = "RX-BASE", changeReason = "Initial reconciliation"
        });
        Assert.Equal(HttpStatusCode.OK, baseline.StatusCode);

        await StepUpTestGrants.GrantAsync(factory, requester, "medication");
        var direct = await requester.PutAsJsonAsync($"/api/phase1/medications/{medicationId}", new
        {
            serviceUserId = RegressionIds.ServiceUserId, name = "Paracetamol", dosage = "1 g",
            route = "Oral", schedule = "PRN", isPrn = true, pharmacy = "Regression Pharmacy",
            allergyWarning = "None"
        });
        Assert.Equal(HttpStatusCode.Conflict, direct.StatusCode);

        await StepUpTestGrants.GrantAsync(factory, requester, "medication");
        var created = await requester.PostAsJsonAsync($"/api/phase1/medication-safety/medications/{medicationId}/changes", new
        {
            changeType = "Regimen", effectiveAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            reason = "New authorised dose", sourceType = "Prescription",
            sourceReference = "RX-CHANGE", prescriberInstruction = "Increase to 1 g when required",
            proposal = new
            {
                dosage = "1 g", route = "Oral", schedule = "PRN", isPrn = true,
                indication = "Pain management", prescriber = "Dr Regression", form = "Tablet",
                strength = "500 mg", doseUnit = "tablet", frequency = "As needed",
                administrationInstructions = "Give two tablets with food",
                startDate = DateTimeOffset.UtcNow, endDate = (DateTimeOffset?)null,
                doseWindowMinutes = 60, maxPrnDoses24h = 4, minPrnIntervalMinutes = 240,
                prnIndication = "Pain", prnEffectReviewMinutes = 60,
                requiresWitness = false, reviewDueAt = (DateTimeOffset?)null
            }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var changeId = json.RootElement.GetProperty("id").GetGuid();

        await StepUpTestGrants.GrantAsync(factory, requester, "medication");
        var selfReview = await requester.PostAsJsonAsync($"/api/phase1/medication-safety/medications/{medicationId}/changes/{changeId}/review",
            new { decision = "Approve", reason = "Checked prescription" });
        Assert.Equal(HttpStatusCode.Conflict, selfReview.StatusCode);

        await StepUpTestGrants.GrantAsync(factory, reviewer, "medication");
        var approved = await reviewer.PostAsJsonAsync($"/api/phase1/medication-safety/medications/{medicationId}/changes/{changeId}/review",
            new { decision = "Approve", reason = "Matched signed prescription RX-CHANGE" });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Contains("Applied", await approved.Content.ReadAsStringAsync());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            var medication = await db.Medications.SingleAsync(x => x.Id == medicationId);
            Assert.Equal("1 g", medication.Dosage);
        }
        var events = await reviewer.GetStringAsync($"/api/phase1/medication-safety/medications/{medicationId}/changes/{changeId}/events");
        Assert.Contains("Requested", events);
        Assert.Contains("Approved", events);
        Assert.Contains("Applied", events);
    }

    [Fact]
    public async Task FutureStopBlocksLaterSchedulingAndPreservesEarlierMarException()
    {
        await factory.EnsureClinicalSeedAsync();
        var medicationId = Guid.NewGuid();
        var managerName = "stop.manager." + Guid.NewGuid().ToString("N");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.AppUsers.Add(new AppUser(Guid.NewGuid(), managerName, managerName + "@aicare.local",
                PasswordHasher.HashPassword("Manager123!"), UserRole.CareManager, true,
                TenantDefaults.OrganizationId, TenantDefaults.BranchId, null, null));
            db.Medications.Add(new Medication(medicationId, RegressionIds.ServiceUserId, "Test medicine",
                "500 mg", "Oral", "Morning", false, "Test pharmacy", "None",
                TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await db.SaveChangesAsync();
        }
        var requester = await SignIn("admin", "Admin123!");
        var reviewer = await SignIn(managerName, "Manager123!");
        await StepUpTestGrants.GrantAsync(factory, requester, "medication");
        var baseline = await MedicationVerificationTestHelper.SaveAndVerifyAsync(factory, requester, medicationId, new
        {
            indication = "Regression", prescriber = "Dr Regression", form = "Tablet", strength = "500 mg",
            doseUnit = "tablet", frequency = "Morning", administrationInstructions = "With food",
            startDate = DateTimeOffset.UtcNow.AddDays(-1), doseWindowMinutes = 60,
            prnIndication = "Not applicable", stockOnHand = 10m, reorderLevel = 2m, requiresWitness = false,
            lastReconciledAt = DateTimeOffset.UtcNow, reconciledBy = "Regression lead",
            reconciliationStatus = "Verified", sourceType = "Prescription",
            sourceReference = "RX-STOP-BASE", changeReason = "Initial review"
        });
        Assert.True(baseline.StatusCode == HttpStatusCode.OK, await baseline.Content.ReadAsStringAsync());

        var beforeEffective = DateTimeOffset.UtcNow.AddHours(1);
        await StepUpTestGrants.GrantAsync(factory, requester, "medication");
        var first = await requester.PostAsJsonAsync("/api/phase1/mar", new
        {
            medicationId, visitId = RegressionIds.VisitId, careWorkerId = RegressionIds.WorkerId,
            scheduledAt = beforeEffective, notes = "Original instruction"
        });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var firstMarId = firstJson.RootElement.GetProperty("id").GetGuid();
        var snapshot = firstJson.RootElement.GetProperty("medicationOrderSnapshotJson").GetString();
        Assert.Contains("500 mg", snapshot);
        Assert.Contains("Morning", snapshot);

        await StepUpTestGrants.GrantAsync(factory, requester, "medication");
        var second = await requester.PostAsJsonAsync("/api/phase1/mar", new
        {
            medicationId, visitId = RegressionIds.VisitId, careWorkerId = RegressionIds.WorkerId,
            scheduledAt = beforeEffective.AddMinutes(5), notes = "Second original dose"
        });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var secondMarId = secondJson.RootElement.GetProperty("id").GetGuid();

        await StepUpTestGrants.GrantAsync(factory, requester, "medication");
        var request = await requester.PostAsJsonAsync($"/api/phase1/medication-safety/medications/{medicationId}/changes", new
        {
            changeType = "Stop", proposal = (object?)null, effectiveAt = DateTimeOffset.UtcNow.AddDays(1),
            reason = "Prescriber stopped medication", sourceType = "Prescription",
            sourceReference = "RX-STOP", prescriberInstruction = "Stop from tomorrow"
        });
        Assert.Equal(HttpStatusCode.Created, request.StatusCode);
        using var changeJson = JsonDocument.Parse(await request.Content.ReadAsStringAsync());
        var changeId = changeJson.RootElement.GetProperty("id").GetGuid();
        await StepUpTestGrants.GrantAsync(factory, reviewer, "medication");
        var approved = await reviewer.PostAsJsonAsync($"/api/phase1/medication-safety/medications/{medicationId}/changes/{changeId}/review",
            new { decision = "Approve", reason = "Signed stop order checked" });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Contains("Approved", await approved.Content.ReadAsStringAsync());

        await StepUpTestGrants.GrantAsync(factory, reviewer, "medication");
        var tooEarly = await reviewer.PostAsync($"/api/phase1/medication-safety/medications/{medicationId}/changes/{changeId}/activate", null);
        Assert.Equal(HttpStatusCode.Conflict, tooEarly.StatusCode);
        await StepUpTestGrants.GrantAsync(factory, requester, "medication");
        var futureDose = await requester.PostAsJsonAsync("/api/phase1/mar", new
        {
            medicationId, visitId = RegressionIds.VisitId, careWorkerId = RegressionIds.WorkerId,
            scheduledAt = DateTimeOffset.UtcNow.AddDays(2), notes = "Should be blocked"
        });
        Assert.Equal(HttpStatusCode.Conflict, futureDose.StatusCode);

        // Advance only the isolated regression database's effective time to exercise activation.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            await db.Database.ExecuteSqlRawAsync("update medication_change_requests set effective_at=now()-interval '1 minute' where id={0}", changeId);
        }
        var applied = false;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var list = await reviewer.GetStringAsync($"/api/phase1/medication-safety/medications/{medicationId}/changes");
            if (list.Contains("\"status\":\"Applied\"", StringComparison.Ordinal)) { applied = true; break; }
            await Task.Delay(500);
        }
        Assert.True(applied, "The due approved stop was not activated by the background worker.");
        var activationEvents = await reviewer.GetStringAsync($"/api/phase1/medication-safety/medications/{medicationId}/changes/{changeId}/events");
        Assert.Contains("system:medication-change-activation", activationEvents);
        var oldMar = await requester.GetStringAsync($"/api/phase1/mar/{firstMarId}");
        Assert.Contains("500 mg", oldMar);
        Assert.Contains("Morning", oldMar);
        Assert.Contains("medicationProfileVersion", oldMar);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            var immutable = await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlRawAsync("update \"MedicationAdministrationRecords\" set \"MedicationOrderSnapshotJson\"='{{}}' where \"Id\"={0}", firstMarId));
            Assert.Contains("immutable", immutable.MessageText, StringComparison.OrdinalIgnoreCase);
        }
        await StepUpTestGrants.GrantAsync(factory, requester, "medication");
        var afterStop = await requester.PostAsJsonAsync("/api/phase1/mar", new
        {
            medicationId, visitId = RegressionIds.VisitId, careWorkerId = RegressionIds.WorkerId,
            scheduledAt = DateTimeOffset.UtcNow.AddDays(2), notes = "Stopped"
        });
        Assert.Equal(HttpStatusCode.Conflict, afterStop.StatusCode);

        await StepUpTestGrants.GrantAsync(factory, requester, "medication");
        requester.DefaultRequestHeaders.Add("Idempotency-Key", "stop-omission-" + Guid.NewGuid().ToString("N"));
        var omission = await requester.PostAsJsonAsync($"/api/phase1/emar-safety/mar/{firstMarId}/record", new
        {
            outcome = "Omitted", occurredAt = DateTimeOffset.UtcNow,
            doseQuantity = 0m, reasonCode = "ClinicalHold", reasonDetail = "Prescriber stopped this medication",
            prnIndication = "", witnessUserId = (Guid?)null, overrideReason = ""
        });
        Assert.Equal(HttpStatusCode.Created, omission.StatusCode);
        requester.DefaultRequestHeaders.Remove("Idempotency-Key");
        await StepUpTestGrants.GrantAsync(factory, requester, "medication");
        requester.DefaultRequestHeaders.Add("Idempotency-Key", "stop-admin-" + Guid.NewGuid().ToString("N"));
        var administration = await requester.PostAsJsonAsync($"/api/phase1/emar-safety/mar/{secondMarId}/record", new
        {
            outcome = "Administered", occurredAt = DateTimeOffset.UtcNow,
            doseQuantity = 1m, reasonCode = "", reasonDetail = "",
            prnIndication = "", witnessUserId = (Guid?)null, overrideReason = ""
        });
        Assert.Equal(HttpStatusCode.Conflict, administration.StatusCode);
    }

    private async Task<HttpClient> SignIn(string name, string password)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = name, password, mfaCode = (string?)null });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", json.RootElement.GetProperty("token").GetString());
        return client;
    }
}
