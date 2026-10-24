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
public sealed class VisitCoverRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task WorkerAcceptanceReassignsOnlyTheOfferedVisitAndClosesTheCoverQueue()
    {
        await factory.EnsureClinicalSeedAsync();
        var personId = Guid.NewGuid();
        var originalWorkerId = Guid.NewGuid();
        var replacementWorkerId = Guid.NewGuid();
        var otherWorkerId = Guid.NewGuid();
        var visitId = Guid.NewGuid();
        var managerName = $"cover.manager.{Guid.NewGuid():N}";
        var replacementName = $"cover.worker.{Guid.NewGuid():N}";
        var otherName = $"cover.other.{Guid.NewGuid():N}";
        var startsAt = DateTimeOffset.UtcNow.AddDays(28);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.ServiceUsers.Add(new ServiceUser(personId, "Cover Person", new DateOnly(1970, 1, 1), "+10000000021", "Personal care", "Contact", "", RiskLevel.Low, "Onboarded", "Address", "None", "None", "Private", "Regression", "", "Independent", "Independent", "Verbal", "None", "Standard", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.CareWorkers.AddRange(
                new CareWorker(originalWorkerId, "Original Worker", "Personal care", "Flexible", 0, 0, "Valid", "Compliant", "10 miles", TenantDefaults.OrganizationId, TenantDefaults.BranchId),
                new CareWorker(replacementWorkerId, "Replacement Worker", "Personal care", "Flexible", 0, 0, "Valid", "Compliant", "10 miles", TenantDefaults.OrganizationId, TenantDefaults.BranchId),
                new CareWorker(otherWorkerId, "Other Worker", "Personal care", "Flexible", 0, 0, "Valid", "Compliant", "10 miles", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.Visits.Add(new Visit(visitId, personId, originalWorkerId, startsAt, "Morning care", 30, "", VisitStatus.Scheduled, null, null, null, null, null, null, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.AppUsers.AddRange(User(managerName, UserRole.CareCoordinator), User(replacementName, UserRole.CareWorker, replacementWorkerId), User(otherName, UserRole.CareWorker, otherWorkerId));
            await db.SaveChangesAsync();
        }

        var manager = await Client(managerName);
        var replacement = await Client(replacementName);
        var other = await Client(otherName);
        var opened = await manager.PostAsJsonAsync($"/api/phase1/visits/{visitId}/cover", new { reason = "Original worker is absent" });
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
        var coverId = (await opened.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync($"/api/phase1/visits/{visitId}/cover", new { reason = "Duplicate" })).StatusCode);

        var offered = await manager.PostAsJsonAsync($"/api/phase1/visit-cover/{coverId}/offers", new { careWorkerId = replacementWorkerId });
        Assert.Equal(HttpStatusCode.Created, offered.StatusCode);
        var offerId = (await offered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Contains(offerId.ToString(), await replacement.GetStringAsync("/api/phase1/visit-cover/offers/mine"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(offerId.ToString(), await other.GetStringAsync("/api/phase1/visit-cover/offers/mine"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/phase1/visit-cover/offers/{offerId}/accept", null)).StatusCode);

        var accepted = await replacement.PostAsync($"/api/phase1/visit-cover/offers/{offerId}/accept", null);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await replacement.PostAsync($"/api/phase1/visit-cover/offers/{offerId}/accept", null)).StatusCode);
        Assert.DoesNotContain(coverId.ToString(), await manager.GetStringAsync("/api/phase1/visit-cover"), StringComparison.OrdinalIgnoreCase);
        using var verifyScope = factory.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal(replacementWorkerId, (await verify.Visits.SingleAsync(x => x.Id == visitId)).CareWorkerId);
        Assert.True(await verify.AuditEvents.AnyAsync(x => x.Action == "visit_cover.accepted" && x.EntityId == offerId));
    }

    private static AppUser User(string name, UserRole role, Guid? workerId = null) =>
        new(Guid.NewGuid(), name, $"{name}@aicare.local", PasswordHasher.HashPassword("Admin123!"), role, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId, workerId, null);

    private async Task<HttpClient> Client(string name)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = name, password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<LoginToken>())!.Token);
        return client;
    }

    private sealed record LoginToken(string Token);
}
