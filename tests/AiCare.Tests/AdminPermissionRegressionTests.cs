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
public sealed class AdminPermissionRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task RemovedUserManagementPermissionBlocksAccessReviewRemediation()
    {
        await factory.EnsureClinicalSeedAsync();
        var name = $"access.reviewer.{Guid.NewGuid():N}";
        var userId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.AppUsers.Add(new AppUser(userId, name, $"{name}@example.test", PasswordHasher.HashPassword("Admin123!"),
                UserRole.Administrator, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        await Authenticate(client, "admin");
        await StepUpTestGrants.GrantAsync(factory, client, "admin");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/phase1/admin/accounts/{userId}/permissions",
            new { disabledKeys = new[] { "users.manage" }, reason = "Restrict account remediation during review" })).StatusCode);
        await Authenticate(client, name);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(
            $"/api/security/access-reviews/{Guid.NewGuid()}/items/{Guid.NewGuid()}/remediate", null)).StatusCode);
    }

    [Fact]
    public async Task PermissionChangeAffectsSettingsApiAndCanBeRestored()
    {
        await factory.EnsureClinicalSeedAsync();
        var name = $"permissions.{Guid.NewGuid():N}";
        var userId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.AppUsers.Add(new AppUser(userId, name, $"{name}@example.test", PasswordHasher.HashPassword("Admin123!"),
                UserRole.Administrator, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        await Authenticate(client, name);
        var path = $"/api/phase1/admin/accounts/{userId}/permissions";
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
        await StepUpTestGrants.GrantAsync(factory, client, "admin");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(path, new { disabledKeys = new[] { "settings.manage", "reports.export" }, reason = "Limit configuration and exports during pilot" })).StatusCode);
        await Authenticate(client, name);
        Assert.Contains("Limit configuration and exports during pilot", await client.GetStringAsync($"/api/phase1/admin/accounts/{userId}/permission-history"));
        var option = new { kind = "DiaryCategory", value = $"Pilot {Guid.NewGuid():N}", active = true };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/phase1/care-operations/options", option)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/phase1/reporting-compliance/report-runs/{Guid.NewGuid()}/csv")).StatusCode);
        await StepUpTestGrants.GrantAsync(factory, client, "admin");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(path, new { disabledKeys = Array.Empty<string>(), reason = "Restore configuration after review" })).StatusCode);
        await Authenticate(client, name);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/phase1/care-operations/options", option)).StatusCode);
    }

    [Fact]
    public async Task ConfiguredCancellationReasonIsRequiredAndAudited()
    {
        await factory.EnsureClinicalSeedAsync();
        var visitId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Visits.Add(new Visit(visitId, RegressionIds.ServiceUserId, RegressionIds.WorkerId,
                DateTimeOffset.UtcNow.AddDays(14), "Pilot care", 30, "", VisitStatus.Scheduled,
                null, null, null, null, null, null, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient();
        await Authenticate(client, "admin");
        var reason = $"Client request {Guid.NewGuid():N}";
        var option = await client.PostAsJsonAsync("/api/phase1/care-operations/options",
            new { kind = "CancellationReason", value = reason, active = true });
        Assert.Equal(HttpStatusCode.Created, option.StatusCode);
        var optionId = (await option.Content.ReadFromJsonAsync<CreatedId>())!.Id;
        try
        {
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/phase1/care-operations/options/{optionId}",
                new { kind = "DiaryCategory", value = reason, active = true })).StatusCode);
            var path = $"/api/phase1/visits/{visitId}/status";
            var rejected = await client.PatchAsJsonAsync(path, new { status = "Cancelled", reason = "An unconfigured reason" });
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Contains("active cancellation reason", await rejected.Content.ReadAsStringAsync());
            var cancelled = await client.PatchAsJsonAsync(path, new { status = "Cancelled", reason });
            Assert.True(cancelled.StatusCode == HttpStatusCode.OK,
                $"Expected OK, received {(int)cancelled.StatusCode}: {await cancelled.Content.ReadAsStringAsync()}");
            using var verify = factory.Services.CreateScope();
            var audit = verify.ServiceProvider.GetRequiredService<CareDbContext>();
            Assert.True(await audit.AuditEvents.AnyAsync(item => item.EntityId == visitId && item.Action == "visit.cancelled"));
        }
        finally
        {
            await client.PutAsJsonAsync($"/api/phase1/care-operations/options/{optionId}", new { kind = "CancellationReason", value = reason, active = false });
        }
    }

    [Fact]
    public async Task ConfiguredJobTitleConstrainsEmploymentEdits()
    {
        await factory.EnsureClinicalSeedAsync();
        using var client = factory.CreateClient();
        await Authenticate(client, "admin");
        var title = $"Senior carer {Guid.NewGuid():N}";
        var option = await client.PostAsJsonAsync("/api/phase1/care-operations/options", new { kind = "JobTitle", value = title, active = true });
        Assert.Equal(HttpStatusCode.Created, option.StatusCode);
        var optionId = (await option.Content.ReadFromJsonAsync<CreatedId>())!.Id;
        try
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/phase1/care-operations/training-courses", new
            {
                code = $"title-check-{Guid.NewGuid():N}", title = "Configured title check", category = "Pilot",
                provider = "Pilot", validityMonths = 12, isMandatory = true, description = "Title validation",
                requiredJobTitles = new[] { "Unknown title" }
            })).StatusCode);
            var worker = await client.PostAsJsonAsync("/api/phase1/care-workers", new
            {
                fullName = $"Title worker {Guid.NewGuid():N}", specialization = "Care", availability = "Weekdays"
            });
            Assert.Equal(HttpStatusCode.Created, worker.StatusCode);
            var workerId = (await worker.Content.ReadFromJsonAsync<CreatedId>())!.Id;
            var path = $"/api/phase1/workforce-lifecycle/care-workers/{workerId}/employment";
            object Request(string jobTitle) => new
            {
                recruitmentStatus = "Cleared", employmentStatus = "Employed", contractType = "Permanent", jobTitle,
                contractedWeeklyMinutes = 1800, startDate = "2026-10-15", endDate = (string?)null,
                lineManager = "Manager", statusReason = "", expectedRevision = 0, changeReason = "Set pilot job title"
            };
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path, Request("Unknown title"))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(path, Request(title))).StatusCode);
        }
        finally
        {
            await client.PutAsJsonAsync($"/api/phase1/care-operations/options/{optionId}", new { kind = "JobTitle", value = title, active = false });
        }
    }

    [Fact]
    public async Task ConfiguredCarerLeavingReasonConstrainsNewDecisionButPreservesHistory()
    {
        await factory.EnsureClinicalSeedAsync();
        using var client = factory.CreateClient();
        await Authenticate(client, "admin");
        var reason = $"End of contract {Guid.NewGuid():N}";
        var option = await client.PostAsJsonAsync("/api/phase1/care-operations/options", new { kind = "WorkerLeavingReason", value = reason, active = true });
        Assert.Equal(HttpStatusCode.Created, option.StatusCode);
        var optionId = (await option.Content.ReadFromJsonAsync<CreatedId>())!.Id;
        Guid? replacementOptionId = null;
        var replacementReason = $"Role ended {Guid.NewGuid():N}";
        try
        {
            var worker = await client.PostAsJsonAsync("/api/phase1/care-workers", new
            {
                fullName = $"Leaving worker {Guid.NewGuid():N}", specialization = "Care", availability = "Weekdays"
            });
            Assert.Equal(HttpStatusCode.Created, worker.StatusCode);
            var workerId = (await worker.Content.ReadFromJsonAsync<CreatedId>())!.Id;
            var path = $"/api/phase1/workforce-lifecycle/care-workers/{workerId}/employment";
            object Request(string leavingReason, int revision) => new
            {
                recruitmentStatus = "Cleared", employmentStatus = "Leaver", contractType = "", jobTitle = "",
                contractedWeeklyMinutes = 0, startDate = (string?)null, endDate = "2026-10-15",
                lineManager = "", statusReason = leavingReason, expectedRevision = revision,
                changeReason = "Record carer departure"
            };
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path, Request("Unconfigured reason", 0))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(path, Request(reason, 0))).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/phase1/care-operations/options/{optionId}",
                new { kind = "WorkerLeavingReason", value = reason, active = false })).StatusCode);
            var replacement = await client.PostAsJsonAsync("/api/phase1/care-operations/options",
                new { kind = "WorkerLeavingReason", value = replacementReason, active = true });
            Assert.Equal(HttpStatusCode.Created, replacement.StatusCode);
            replacementOptionId = (await replacement.Content.ReadFromJsonAsync<CreatedId>())!.Id;
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(path, Request(reason, 1))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path, Request("Unconfigured reason", 2))).StatusCode);
        }
        finally
        {
            await client.PutAsJsonAsync($"/api/phase1/care-operations/options/{optionId}", new { kind = "WorkerLeavingReason", value = reason, active = false });
            if (replacementOptionId is Guid replacementId)
                await client.PutAsJsonAsync($"/api/phase1/care-operations/options/{replacementId}", new { kind = "WorkerLeavingReason", value = replacementReason, active = false });
        }
    }

    private static async Task Authenticate(HttpClient client, string name)
    {
        client.DefaultRequestHeaders.Authorization = null;
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = name, password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<Login>())!.Token);
    }

    private sealed record Login(string Token);
    private sealed record CreatedId(Guid Id);
}
