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
public sealed class CareOperationsWebRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task WorkerContactDetailsAreSavedSeparatelyAndScopedToTheLinkedWorker()
    {
        await factory.EnsureClinicalSeedAsync();
        using var admin = await AdminClient();
        var email = $"worker.{Guid.NewGuid():N}@example.test";
        var created = await admin.PostAsJsonAsync("/api/phase1/care-workers", new
        {
            fullName = $"Contact worker {Guid.NewGuid():N}", specialization = "Care", availability = "Weekdays",
            contact = new { email, mobilePhone = "07123456789", preferredContactMethod = "Email" }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<CreatedId>())!.Id;
        Assert.DoesNotContain(email, await created.Content.ReadAsStringAsync());
        Assert.Contains(email, await admin.GetStringAsync($"/api/phase1/profile-contact/CareWorker/{id}"));
        Assert.DoesNotContain(email, await admin.GetStringAsync("/api/phase1/care-workers"));
        var duplicate = await admin.PostAsJsonAsync("/api/phase1/care-workers", new
        {
            fullName = $"Other worker {Guid.NewGuid():N}", specialization = "Care", availability = "Weekdays",
            contact = new { email = email.ToUpperInvariant() }
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var userName = $"contact.worker.{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.AppUsers.Add(new AppUser(Guid.NewGuid(), userName, $"{userName}@example.test",
                PasswordHasher.HashPassword("Admin123!"), UserRole.CareWorker, true,
                TenantDefaults.OrganizationId, TenantDefaults.BranchId, id, null));
            await db.SaveChangesAsync();
        }
        using var worker = factory.CreateClient();
        var login = await worker.PostAsJsonAsync("/api/auth/login", new { userName, password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode();
        worker.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<Login>())!.Token);
        Assert.Contains(email, await worker.GetStringAsync($"/api/phase1/profile-contact/CareWorker/{id}"));
        Assert.Equal(HttpStatusCode.Forbidden, (await worker.GetAsync($"/api/phase1/profile-contact/CareWorker/{RegressionIds.WorkerId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await worker.GetAsync($"/api/phase1/profile-contact/ServiceUser/{RegressionIds.ServiceUserId}")).StatusCode);
    }

    [Fact]
    public async Task ConfiguredDiaryCategoryControlsNewEntriesAndKeepsHistory()
    {
        await factory.EnsureClinicalSeedAsync();
        using var client = await AdminClient();
        var label = $"Pilot category {Guid.NewGuid():N}";
        var created = await client.PostAsJsonAsync("/api/phase1/care-operations/options", new { kind = "DiaryCategory", value = label, active = true });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var optionId = (await created.Content.ReadFromJsonAsync<CreatedId>())!.Id;
        var entry = new { category = label, priority = "Routine", subject = "Configured category", detail = "Saved for the pilot", eventAt = DateTimeOffset.UtcNow };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/phase1/care-operations/diary", entry)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/phase1/care-operations/options/{optionId}", new { kind = "DiaryCategory", value = label, active = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/phase1/care-operations/diary", entry)).StatusCode);
        Assert.Contains(label, await client.GetStringAsync("/api/phase1/care-operations/options"));
    }

    [Fact]
    public async Task TrainingCourseCanBeEditedAndDeactivatedWithoutDeletingIt()
    {
        await factory.EnsureClinicalSeedAsync();
        using var client = await AdminClient();
        var code = $"PILOT-{Guid.NewGuid():N}";
        var jobTitle = $"Pilot role {Guid.NewGuid():N}";
        var before = await client.GetStringAsync("/api/phase1/care-operations/training-compliance");
        var course = new { code, title = "Pilot course", category = "Mandatory", provider = "In house", validityMonths = 12, isMandatory = true, description = "Initial", requiredJobTitles = new[] { jobTitle } };
        var created = await client.PostAsJsonAsync("/api/phase1/care-operations/training-courses", course);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<CreatedId>())!.Id;
        Assert.Equal(before, await client.GetStringAsync("/api/phase1/care-operations/training-compliance"));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/phase1/care-operations/training-courses/{id}", new { code, title = "Pilot course revised", category = "Mandatory", provider = "In house", validityMonths = 24, isMandatory = true, description = "Revised", requiredJobTitles = new[] { jobTitle } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/phase1/care-operations/training-courses/{id}/active", new { active = false })).StatusCode);
        var active = await client.GetStringAsync("/api/phase1/care-operations/training-courses");
        Assert.DoesNotContain(code, active);
        var all = await client.GetStringAsync("/api/phase1/care-operations/training-courses?includeInactive=true");
        Assert.Contains("Pilot course revised", all);
        Assert.Contains(jobTitle, all);
    }

    [Fact]
    public async Task LiveSharingRequiresManagerControlAndWorkerOptInAndStopsImmediately()
    {
        await factory.EnsureClinicalSeedAsync();
        var workerName = $"live.worker.{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.AppUsers.Add(new AppUser(Guid.NewGuid(), workerName, $"{workerName}@aicare.local",
                PasswordHasher.HashPassword("Admin123!"), UserRole.CareWorker, true,
                TenantDefaults.OrganizationId, TenantDefaults.BranchId, RegressionIds.WorkerId, null));
            var visit = await db.Visits.SingleAsync(x => x.Id == RegressionIds.VisitId);
            db.Entry(visit).CurrentValues.SetValues(visit with { StartsAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
            await db.SaveChangesAsync();
        }
        using var admin = await AdminClient();
        using var worker = factory.CreateClient();
        var login = await worker.PostAsJsonAsync("/api/auth/login", new { userName = workerName, password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode();
        worker.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<Login>())!.Token);
        var path = $"/api/phase1/care-operations/live-sharing/{RegressionIds.VisitId}/start";
        Assert.Equal(HttpStatusCode.Forbidden, (await worker.PostAsync(path, null)).StatusCode);
        var now = DateTimeOffset.UtcNow;
        var consent = await admin.PutAsJsonAsync($"/api/phase1/care-operations/location-consent/CareWorker/{RegressionIds.WorkerId}",
            new { sharingMode = "Exact", purpose = "Active visit safety", startsAt = now.AddMinutes(-1), endsAt = now.AddHours(1) });
        Assert.Equal(HttpStatusCode.OK, consent.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await worker.PostAsync(path, null)).StatusCode);
        var point = new { visitId = RegressionIds.VisitId, latitude = 54.5m, longitude = -2.5m, accuracyMeters = 10m, capturedAt = DateTimeOffset.UtcNow };
        Assert.Equal(HttpStatusCode.Accepted, (await worker.PostAsJsonAsync("/api/phase1/care-operations/live-location", point)).StatusCode);
        Assert.Contains(RegressionIds.WorkerId.ToString(), await admin.GetStringAsync("/api/phase1/care-operations/live-map"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NoContent, (await worker.PostAsync("/api/phase1/care-operations/live-sharing/stop", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await worker.PostAsJsonAsync("/api/phase1/care-operations/live-location", point)).StatusCode);
        Assert.DoesNotContain(RegressionIds.WorkerId.ToString(), await admin.GetStringAsync("/api/phase1/care-operations/live-map"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RosterAvailabilityShowsEffectiveWorkerRules()
    {
        await factory.EnsureClinicalSeedAsync();
        using var client = await AdminClient();
        var day = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14));
        var created = await client.PostAsJsonAsync($"/api/phase1/care-workers/{RegressionIds.WorkerId}/compliance/availability", new
        {
            dayOfWeek = (int)day.DayOfWeek,
            startTime = "08:00:00",
            endTime = "16:00:00",
            isAvailable = true,
            effectiveFrom = day,
            effectiveTo = day,
            notes = "Roster regression"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var response = await client.GetAsync($"/api/phase1/care-operations/roster-availability?day={day:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rule = document.RootElement.GetProperty("rules").EnumerateArray()
            .Single(x => x.GetProperty("careWorkerId").GetGuid() == RegressionIds.WorkerId
                && x.GetProperty("startTime").GetString()!.StartsWith("08:00", StringComparison.Ordinal));
        Assert.True(rule.GetProperty("isAvailable").GetBoolean());
    }

    [Fact]
    public async Task DiaryParticipantsCommentsAndHistoryAreAvailableToDesktopWeb()
    {
        await factory.EnsureClinicalSeedAsync();
        Guid ownerId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            ownerId = await db.AppUsers.Where(x => x.UserName == "admin").Select(x => x.Id).SingleAsync();
        }
        using var client = await AdminClient();
        var participants = await client.GetAsync("/api/phase1/care-operations/diary-participants");
        Assert.Equal(HttpStatusCode.OK, participants.StatusCode);
        Assert.Contains(ownerId.ToString(), await participants.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var eventAt = DateTimeOffset.UtcNow;
        var created = await client.PostAsJsonAsync("/api/phase1/care-operations/diary", new
        {
            category = "Care delivery", priority = "High", subject = "Web diary review", detail = "Follow up care call",
            eventAt, dueAt = eventAt.AddDays(1), ownerId, recipientIds = new[] { ownerId },
            serviceUserId = RegressionIds.ServiceUserId, careWorkerId = RegressionIds.WorkerId
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<CreatedId>())!.Id;
        var detail = await client.GetAsync($"/api/phase1/care-operations/diary/{id}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var initial = await detail.Content.ReadAsStringAsync();
        Assert.Contains("Web diary review", initial);
        Assert.Contains("recipientIds", initial);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/api/phase1/care-operations/diary/{id}/comments", new { body = "Manager reviewed" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/api/phase1/care-operations/diary/{id}/transition", new { status = "Resolved", reason = "Follow-up completed" })).StatusCode);
        var updated = await client.GetStringAsync($"/api/phase1/care-operations/diary/{id}");
        Assert.Contains("Manager reviewed", updated);
        Assert.Contains("Status changed", updated);
        Assert.Contains("Resolved", updated);
    }

    [Fact]
    public async Task TrainingAttendanceUpdatesExpiryAndCompliance()
    {
        await factory.EnsureClinicalSeedAsync();
        using var client = await AdminClient();
        var code = $"WEB-{Guid.NewGuid():N}";
        var course = await client.PostAsJsonAsync("/api/phase1/care-operations/training-courses", new { code, title = "Web training review", category = "Mandatory", provider = "Internal", validityMonths = 12, isMandatory = true, description = "" });
        Assert.Equal(HttpStatusCode.Created, course.StatusCode);
        var courseId = (await course.Content.ReadFromJsonAsync<CreatedId>())!.Id;
        var scheduledAt = DateTimeOffset.UtcNow.AddDays(2);
        var booking = await client.PostAsJsonAsync("/api/phase1/care-operations/training-bookings", new { courseId, careWorkerId = RegressionIds.WorkerId, scheduledAt });
        Assert.Equal(HttpStatusCode.Created, booking.StatusCode);
        var bookingId = (await booking.Content.ReadFromJsonAsync<CreatedId>())!.Id;
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/phase1/care-operations/training-bookings/{bookingId}/attendance", new { status = "Attended", confirmationStatus = "Confirmed", notes = "Verified" })).StatusCode);

        var calendar = await client.GetStringAsync($"/api/phase1/care-operations/training-calendar?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(7).ToString("O"))}");
        using var document = JsonDocument.Parse(calendar);
        var item = document.RootElement.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == bookingId);
        Assert.Equal("Attended", item.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, item.GetProperty("expiresAt").ValueKind);

        var compliance = await client.GetAsync("/api/phase1/care-operations/training-compliance");
        Assert.Equal(HttpStatusCode.OK, compliance.StatusCode);
        Assert.Contains(RegressionIds.WorkerId.ToString(), await compliance.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HttpClient> AdminClient()
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<Login>())!.Token);
        return client;
    }

    private sealed record CreatedId(Guid Id);
    private sealed record Login(string Token);
}
