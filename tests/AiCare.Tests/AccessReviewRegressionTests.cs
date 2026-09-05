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
public sealed class AccessReviewRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task AccessReviewCampaignInventoryDecisionRemediationAndEventsAreGoverned()
    {
        await factory.EnsureClinicalSeedAsync();
        var targetName = $"access.review.target.{Guid.NewGuid():N}";
        Guid targetUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            var target = new AppUser(
                Guid.NewGuid(),
                targetName,
                $"{targetName}@aicare.local",
                PasswordHasher.HashPassword("Admin123!"),
                UserRole.CareWorker,
                true,
                TenantDefaults.OrganizationId,
                TenantDefaults.BranchId,
                null,
                null);
            targetUserId = target.Id;
            db.AppUsers.Add(target);
            await db.SaveChangesAsync();
        }

        var admin = factory.CreateClient();
        var login = await admin.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString());

        var create = await admin.PostAsJsonAsync("/api/security/access-reviews", new
        {
            name = $"Quarterly access review {Guid.NewGuid():N}",
            branchId = TenantDefaults.BranchId,
            dormantDays = 30,
            dueAt = DateTimeOffset.UtcNow.AddDays(14)
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var campaignId = JsonDocument.Parse(await create.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var run = await admin.PostAsync($"/api/security/access-reviews/{campaignId}/run", null);
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var detail = JsonDocument.Parse(await run.Content.ReadAsStringAsync()).RootElement;
        var item = detail.GetProperty("items").EnumerateArray().Single(row => row.GetProperty("user_name").GetString() == targetName);
        var itemId = item.GetProperty("id").GetGuid();
        Assert.Equal("Critical", item.GetProperty("severity").GetString());
        Assert.Contains("Dormant", item.GetProperty("finding_codes").ToString());
        Assert.Contains("OrphanedWorker", item.GetProperty("finding_codes").ToString());

        var weakDecision = await admin.PostAsJsonAsync($"/api/security/access-reviews/{campaignId}/items/{itemId}/decide", new { decision = "Suspend", justification = "Too short" });
        Assert.Equal(HttpStatusCode.BadRequest, weakDecision.StatusCode);

        var decision = await admin.PostAsJsonAsync($"/api/security/access-reviews/{campaignId}/items/{itemId}/decide", new { decision = "Suspend", justification = "Orphaned dormant care-worker account must be suspended." });
        Assert.Equal(HttpStatusCode.NoContent, decision.StatusCode);

        var closeWithPendingItems = await admin.PostAsync($"/api/security/access-reviews/{campaignId}/close", null);
        Assert.Equal(HttpStatusCode.Conflict, closeWithPendingItems.StatusCode);

        var remediate = await admin.PostAsync($"/api/security/access-reviews/{campaignId}/items/{itemId}/remediate", null);
        Assert.Equal(HttpStatusCode.NoContent, remediate.StatusCode);

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.False(await verifyDb.AppUsers.Where(user => user.Id == targetUserId).Select(user => user.IsActive).SingleAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => verifyDb.Database.ExecuteSqlInterpolatedAsync($"update access_review_events set detail='changed' where campaign_id={campaignId}"));
    }
}
