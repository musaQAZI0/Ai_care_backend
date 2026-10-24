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
public sealed class UnassignedVisitRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task AssignmentCreatesOneVisitAndRemovesTheCallFromTheQueue()
    {
        await factory.EnsureClinicalSeedAsync();
        var personId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var managerName = $"unassigned.manager.{Guid.NewGuid():N}";
        var startsAt = DateTimeOffset.UtcNow.AddDays(35);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.ServiceUsers.Add(new ServiceUser(personId, "Unassigned Person", new DateOnly(1970, 1, 1), "+10000000022", "Personal care", "Contact", "", RiskLevel.Low, "Onboarded", "Address", "None", "None", "Private", "Regression", "", "Independent", "Independent", "Verbal", "None", "Standard", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.CareWorkers.Add(new CareWorker(workerId, "Available Worker", "Personal care", "Flexible", 0, 0, "Valid", "Compliant", "10 miles", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.AppUsers.Add(new AppUser(Guid.NewGuid(), managerName, $"{managerName}@aicare.local", PasswordHasher.HashPassword("Admin123!"), UserRole.CareCoordinator, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId, null, null));
            await db.SaveChangesAsync();
        }
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = managerName, password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<LoginToken>())!.Token);

        var created = await client.PostAsJsonAsync("/api/phase1/unassigned-visits", new { serviceUserId = personId, startsAt, visitType = "Morning care", durationMinutes = 30, requiredSkills = "", reason = "Awaiting allocation" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var callId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Contains(callId.ToString(), await client.GetStringAsync("/api/phase1/unassigned-visits"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/phase1/unassigned-visits/{callId}/assign", new { careWorkerId = Guid.NewGuid() })).StatusCode);
        var assigned = await client.PostAsJsonAsync($"/api/phase1/unassigned-visits/{callId}/assign", new { careWorkerId = workerId });
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        var visitId = (await assigned.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/phase1/unassigned-visits/{callId}/assign", new { careWorkerId = workerId })).StatusCode);
        Assert.DoesNotContain(callId.ToString(), await client.GetStringAsync("/api/phase1/unassigned-visits"), StringComparison.OrdinalIgnoreCase);
        using var verifyScope = factory.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal(workerId, (await verify.Visits.SingleAsync(x => x.Id == visitId)).CareWorkerId);
        Assert.True(await verify.AuditEvents.AnyAsync(x => x.Action == "unassigned_visit.assigned" && x.EntityId == callId));
    }

    private sealed record LoginToken(string Token);
}
