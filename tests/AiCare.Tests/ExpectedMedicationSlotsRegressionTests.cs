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
public sealed class ExpectedMedicationSlotsRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task ApprovedFixedTimesProduceDistinctSlotsAndRetryCannotDuplicateMar()
    {
        await factory.EnsureClinicalSeedAsync();
        var medicationId = Guid.NewGuid();
        var clockMedicationId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Medications.AddRange(
                new Medication(medicationId, RegressionIds.ServiceUserId, "Fixed dose regression", "5 mg",
                    "Oral", "Daily", false, "Pharmacy", "None", TenantDefaults.OrganizationId, TenantDefaults.BranchId),
                new Medication(clockMedicationId, RegressionIds.ServiceUserId, "Clock transition regression", "5 mg",
                    "Oral", "Daily", false, "Pharmacy", "None", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await db.SaveChangesAsync();
        }
        var admin = factory.CreateClient();
        var login = await admin.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode();
        using (var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync()))
            admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginJson.RootElement.GetProperty("token").GetString());

        await StepUpTestGrants.GrantAsync(factory, admin, "medication");
        var invalidTimes = await admin.PutAsJsonAsync($"/api/phase1/medication-safety/medications/{medicationId}/profile", new {
            doseWindowMinutes = 60, scheduledLocalTimes = new[] { "08:00", "08:00" }
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidTimes.StatusCode);

        async Task Verify(Guid id, string[] times)
        {
            var result = await MedicationVerificationTestHelper.SaveAndVerifyAsync(factory, admin, id, new {
                indication = "Regression fixed dose", prescriber = "Dr Regression", form = "Tablet", strength = "5 mg",
                doseUnit = "tablet", frequency = "Daily", administrationInstructions = "With water",
                scheduledLocalTimes = times, startDate = DateTimeOffset.UtcNow.AddYears(-2),
                doseWindowMinutes = 60, prnIndication = "Not applicable", stockOnHand = 10m,
                reorderLevel = 2m, requiresWitness = false, reconciliationStatus = "Verified",
                sourceType = "Prescription", sourceReference = "RX-FIXED", changeReason = "Initial review"
            });
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            using var profile = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
            Assert.Equal(times.Length, profile.RootElement.GetProperty("scheduledLocalTimes").GetArrayLength());
        }
        await Verify(medicationId, ["08:00", "20:00"]);
        await Verify(clockMedicationId, ["01:30"]);
        await MedicationAuthorityTestHelper.SeedAsync(factory, RegressionIds.ServiceUserId, medicationId);

        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var tomorrow = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date).AddDays(1);
        var scheduledAt = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(tomorrow.ToDateTime(new TimeOnly(8, 0)), zone));
        var day = tomorrow.ToString("yyyy-MM-dd");
        var url = $"/api/phase1/emar-safety/expected-slots?personId={RegressionIds.ServiceUserId}&from={day}&to={day}";
        var before = await admin.GetFromJsonAsync<JsonElement>(url);
        Assert.Contains(before.GetProperty("slots").EnumerateArray(), slot =>
            slot.GetProperty("medicationId").GetGuid() == medicationId && slot.GetProperty("status").GetString() == "Unscheduled");

        await StepUpTestGrants.GrantAsync(factory, admin, "medication");
        var wrongTime = await admin.PostAsJsonAsync("/api/phase1/mar", new {
            medicationId, visitId = RegressionIds.VisitId, careWorkerId = RegressionIds.WorkerId,
            scheduledAt = scheduledAt.AddMinutes(15), notes = "Wrong fixed time"
        });
        Assert.Equal(HttpStatusCode.Conflict, wrongTime.StatusCode);
        await StepUpTestGrants.GrantAsync(factory, admin, "medication");
        var first = await admin.PostAsJsonAsync("/api/phase1/mar", new {
            medicationId, visitId = RegressionIds.VisitId, careWorkerId = RegressionIds.WorkerId,
            scheduledAt, notes = "Approved fixed time"
        });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        await StepUpTestGrants.GrantAsync(factory, admin, "medication");
        var retry = await admin.PostAsJsonAsync("/api/phase1/mar", new {
            medicationId, visitId = RegressionIds.VisitId, careWorkerId = RegressionIds.WorkerId,
            scheduledAt, notes = "Approved fixed time"
        });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var retryJson = JsonDocument.Parse(await retry.Content.ReadAsStringAsync());
        Assert.Equal(firstJson.RootElement.GetProperty("id").GetGuid(), retryJson.RootElement.GetProperty("id").GetGuid());
        var after = await admin.GetFromJsonAsync<JsonElement>(url);
        Assert.Contains(after.GetProperty("slots").EnumerateArray(), slot =>
            slot.GetProperty("medicationId").GetGuid() == medicationId && slot.GetProperty("status").GetString() == "Scheduled" &&
            slot.GetProperty("marIds").GetArrayLength() == 1);
        await StepUpTestGrants.GrantAsync(factory, admin, "medication");
        await StepUpTestGrants.GrantAsync(factory, admin, "medication");
        var concurrentRequest = new {
            medicationId, visitId = RegressionIds.VisitId, careWorkerId = RegressionIds.WorkerId,
            scheduledAt = scheduledAt.AddHours(12), notes = "Concurrent fixed slot"
        };
        var concurrent = await Task.WhenAll(admin.PostAsJsonAsync("/api/phase1/mar", concurrentRequest),
            admin.PostAsJsonAsync("/api/phase1/mar", concurrentRequest));
        Assert.Contains(concurrent, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Contains(concurrent, response => response.StatusCode == HttpStatusCode.OK);
        var afterConcurrent = await admin.GetFromJsonAsync<JsonElement>(url);
        Assert.Contains(afterConcurrent.GetProperty("slots").EnumerateArray(), slot =>
            slot.GetProperty("medicationId").GetGuid() == medicationId && slot.GetProperty("localTime").GetString() == "20:00" &&
            slot.GetProperty("marIds").GetArrayLength() == 1);
        var yesterday = tomorrow.AddDays(-2).ToString("yyyy-MM-dd");
        var historical = await admin.GetFromJsonAsync<JsonElement>($"/api/phase1/emar-safety/expected-slots?personId={RegressionIds.ServiceUserId}&from={yesterday}&to={yesterday}");
        Assert.DoesNotContain(historical.GetProperty("slots").EnumerateArray(), slot => slot.GetProperty("medicationId").GetGuid() == medicationId);

        var otherPerson = await admin.GetFromJsonAsync<JsonElement>($"/api/phase1/emar-safety/expected-slots?personId={Guid.NewGuid()}&from={day}&to={day}");
        Assert.Empty(otherPerson.GetProperty("slots").EnumerateArray());
        var excessiveRange = await admin.GetAsync($"/api/phase1/emar-safety/expected-slots?personId={RegressionIds.ServiceUserId}&from={day}&to={tomorrow.AddDays(31):yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.BadRequest, excessiveRange.StatusCode);

        var spring = await admin.GetFromJsonAsync<JsonElement>($"/api/phase1/emar-safety/expected-slots?personId={RegressionIds.ServiceUserId}&from=2027-03-28&to=2027-03-28");
        Assert.Contains(spring.GetProperty("slots").EnumerateArray(), slot =>
            slot.GetProperty("medicationId").GetGuid() == clockMedicationId &&
            slot.GetProperty("status").GetString() == "ClockReviewRequired" &&
            slot.GetProperty("candidateInstants").GetArrayLength() == 0);
        var autumn = await admin.GetFromJsonAsync<JsonElement>($"/api/phase1/emar-safety/expected-slots?personId={RegressionIds.ServiceUserId}&from=2027-10-31&to=2027-10-31");
        Assert.Contains(autumn.GetProperty("slots").EnumerateArray(), slot =>
            slot.GetProperty("medicationId").GetGuid() == clockMedicationId &&
            slot.GetProperty("status").GetString() == "ClockReviewRequired" &&
            slot.GetProperty("candidateInstants").GetArrayLength() == 2);

        // Synthetic legacy data exercises reporting of an earlier verified window and duplicate MAR rows.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"update medication_safety_profiles set reviewed_at={DateTimeOffset.UtcNow.AddDays(-3)} where medication_id={medicationId}");
            db.MedicationAdministrationRecords.Add(new MedicationAdministrationRecord(Guid.NewGuid(), medicationId,
                RegressionIds.VisitId, RegressionIds.WorkerId, scheduledAt, null, "Scheduled", "Synthetic legacy duplicate",
                TenantDefaults.OrganizationId, TenantDefaults.BranchId,
                firstJson.RootElement.GetProperty("medicationProfileVersion").GetInt32()));
            await db.SaveChangesAsync();
        }
        var missing = await admin.GetFromJsonAsync<JsonElement>($"/api/phase1/emar-safety/expected-slots?personId={RegressionIds.ServiceUserId}&from={yesterday}&to={yesterday}");
        Assert.Contains(missing.GetProperty("slots").EnumerateArray(), slot =>
            slot.GetProperty("medicationId").GetGuid() == medicationId && slot.GetProperty("status").GetString() == "Missing");
        var duplicates = await admin.GetFromJsonAsync<JsonElement>(url);
        Assert.Contains(duplicates.GetProperty("slots").EnumerateArray(), slot =>
            slot.GetProperty("medicationId").GetGuid() == medicationId && slot.GetProperty("localTime").GetString() == "08:00" &&
            slot.GetProperty("status").GetString() == "Duplicate");

        await StepUpTestGrants.GrantAsync(factory, admin, "medication");
        var pendingStop = await admin.PostAsJsonAsync($"/api/phase1/medication-safety/medications/{medicationId}/changes", new {
            changeType = "Stop", proposal = (object?)null, effectiveAt = scheduledAt.AddDays(1),
            reason = "Regression stop instruction", sourceType = "Prescription", sourceReference = "RX-STOP-FIXED",
            prescriberInstruction = "Stop after tomorrow"
        });
        Assert.Equal(HttpStatusCode.Created, pendingStop.StatusCode);
        var afterStopDay = tomorrow.AddDays(2).ToString("yyyy-MM-dd");
        var afterPending = await admin.GetFromJsonAsync<JsonElement>($"/api/phase1/emar-safety/expected-slots?personId={RegressionIds.ServiceUserId}&from={afterStopDay}&to={afterStopDay}");
        Assert.DoesNotContain(afterPending.GetProperty("slots").EnumerateArray(), slot => slot.GetProperty("medicationId").GetGuid() == medicationId);
    }
}
