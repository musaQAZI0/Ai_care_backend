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
public sealed class PrivacyRightsGovernanceRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task SubjectAccessRestrictionAndLegalHoldLifecyclesAreScopedAuditedAndImmutable()
    {
        await factory.EnsureClinicalSeedAsync();
        var managerName = $"privacy.manager.{Guid.NewGuid():N}";
        var workerName = $"privacy.worker.{Guid.NewGuid():N}";
        var workerId = Guid.NewGuid();
        var personId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.ServiceUsers.Add(new ServiceUser(personId, "Privacy Test Person", new DateOnly(1980, 1, 1), "+10000000010", "Personal care", "Test contact", "", RiskLevel.Low, "Active", "Test address", "None", "None", "Private", "Regression", "", "Independent", "Independent", "Verbal", "None", "Standard", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.CareWorkers.Add(new CareWorker(workerId, "Privacy Worker", "Care", "Flexible", 0, 0, "Valid", "Compliant", "Local", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.AppUsers.AddRange(User(managerName, UserRole.CareManager), User(workerName, UserRole.CareWorker, workerId));
            await db.SaveChangesAsync();
        }

        var manager = await Login(managerName);
        var worker = await Login(workerName);
        var admin = await Login("admin");
        await StepUpTestGrants.GrantAsync(factory, manager, "privacy");
        await StepUpTestGrants.GrantAsync(factory, admin, "privacy");
        Assert.Equal(HttpStatusCode.Forbidden, (await worker.GetAsync("/api/phase1/privacy-rights")).StatusCode);

        var created = await manager.PostAsJsonAsync("/api/phase1/privacy-rights/requests", new
        {
            serviceUserId = personId, requestType = "SubjectAccess", requesterName = "Test representative",
            requesterRelationship = "Authorized representative", requestChannel = "Web", reason = "Access to care records",
            receivedAt = DateTimeOffset.UtcNow
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var requestId = (await created.Content.ReadFromJsonAsync<Created>())!.Id;
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync($"/api/phase1/privacy-rights/requests/{requestId}/assign", new { owner = "Privacy lead" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync($"/api/phase1/privacy-rights/requests/{requestId}/verify", new { identityEvidence = "", authorityEvidence = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync($"/api/phase1/privacy-rights/requests/{requestId}/verify", new { identityEvidence = "Photo ID checked", authorityEvidence = "Signed proxy authority checked" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync($"/api/phase1/privacy-rights/requests/{requestId}/assign", new { owner = "Privacy lead" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsync($"/api/phase1/privacy-rights/requests/{requestId}/discover", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsync($"/api/phase1/privacy-rights/requests/{requestId}/pack", null)).StatusCode);

        var caseDocument = JsonDocument.Parse(await manager.GetStringAsync($"/api/phase1/privacy-rights/requests/{requestId}"));
        var records = caseDocument.RootElement.GetProperty("records").EnumerateArray().ToList();
        Assert.NotEmpty(records);
        for (var index = 0; index < records.Count; index++)
        {
            var action = index == 0 ? "Redact" : "Disclose";
            var review = await manager.PostAsJsonAsync($"/api/phase1/privacy-rights/requests/{requestId}/records/{records[index].GetProperty("id").GetGuid()}/review", new { action, reason = action == "Redact" ? "Third-party details removed" : "Relevant personal data" });
            Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsync($"/api/phase1/privacy-rights/requests/{requestId}/pack", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync($"/api/phase1/privacy-rights/requests/{requestId}/release", new { releaseEvidence = "", decision = "Approved" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync($"/api/phase1/privacy-rights/requests/{requestId}/release", new { releaseEvidence = "Secure portal receipt SAR-1", decision = "Approved with recorded redaction" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync($"/api/phase1/privacy-rights/requests/{requestId}/transition", new { action = "Close", evidence = "Disclosure receipt confirmed" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync($"/api/phase1/privacy-rights/requests/{requestId}/transition", new { action = "Reopen", evidence = "Requester queried omitted date range" })).StatusCode);

        var restriction = await manager.PostAsJsonAsync("/api/phase1/privacy-rights/restrictions", new { serviceUserId = personId, privacyRequestId = requestId, scope = "Non-essential exports", reason = "Accuracy disputed" });
        Assert.Equal(HttpStatusCode.Created, restriction.StatusCode);
        var restrictionId = (await restriction.Content.ReadFromJsonAsync<Created>())!.Id;
        Assert.Equal(HttpStatusCode.Conflict, (await manager.GetAsync($"/api/phase1/data-governance/service-users/{personId}/export")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync($"/api/phase1/privacy-rights/restrictions/{restrictionId}/lift", new { evidence = "Accuracy review completed" })).StatusCode);

        var hold = await manager.PostAsJsonAsync("/api/phase1/privacy-rights/legal-holds", new { serviceUserId = personId, scope = "All person records", reason = "Active investigation", authority = "DPO instruction", reviewDueAt = DateTimeOffset.UtcNow.AddDays(30), organizationWide = false });
        Assert.Equal(HttpStatusCode.Created, hold.StatusCode);
        var holdId = (await hold.Content.ReadFromJsonAsync<Created>())!.Id;
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync($"/api/phase1/privacy-rights/legal-holds/{holdId}/release", new { evidence = "Not independently approved" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync($"/api/phase1/privacy-rights/legal-holds/{holdId}/release-request", new { evidence = "Investigation owner confirms completion" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/phase1/privacy-rights/legal-holds/{holdId}/release", new { evidence = "Administrator independently approved release" })).StatusCode);

        var finalCase = await manager.GetStringAsync($"/api/phase1/privacy-rights/requests/{requestId}");
        Assert.Contains("IdentityVerified", finalCase);
        Assert.Contains("PackGenerated", finalCase);
        Assert.Contains("Third-party details removed", finalCase);
        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x => x.Action == "privacy.pack_released" && x.EntityId == requestId));
        await Assert.ThrowsAnyAsync<Exception>(() => verifyDb.Database.ExecuteSqlInterpolatedAsync($"update privacy_case_events set detail='changed' where request_id={requestId}"));
    }

    private async Task<HttpClient> Login(string name, string password = "Admin123!")
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { userName = name, password, mfaCode = (string?)null });
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<LoginResponse>())!.Token);
        return client;
    }

    private static AppUser User(string name, UserRole role, Guid? workerId = null) => new(Guid.NewGuid(), name, $"{name}@aicare.local", PasswordHasher.HashPassword("Admin123!"), role, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId, workerId, null);
    private sealed record LoginResponse(string Token);
    private sealed record Created(Guid Id);
}
