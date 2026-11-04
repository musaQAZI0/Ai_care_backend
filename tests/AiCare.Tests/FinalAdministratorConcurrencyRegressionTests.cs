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
public sealed class FinalAdministratorConcurrencyRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task ConcurrentAdministratorSuspensionsCannotRemoveEveryActiveAdministrator()
    {
        var organizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var firstName = $"concurrent.admin.one.{Guid.NewGuid():N}";
        var secondName = $"concurrent.admin.two.{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Organizations.Add(new Organization(organizationId, "Final administrator concurrency", "Trial", "Active"));
            db.Branches.Add(new Branch(branchId, organizationId, "Concurrency branch", "Test", "Active"));
            db.AppUsers.AddRange(
                User(firstId, firstName, organizationId, branchId),
                User(secondId, secondName, organizationId, branchId));
            await db.SaveChangesAsync();
        }

        using var first = await Login(firstName);
        using var second = await Login(secondName);
        var responses = await Task.WhenAll(
            first.PatchAsJsonAsync($"/api/phase1/admin/accounts/{secondId}/status", new { active = false, reason = "Concurrent administrator suspension one" }),
            second.PatchAsJsonAsync($"/api/phase1/admin/accounts/{firstId}/status", new { active = false, reason = "Concurrent administrator suspension two" }));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal(1, await verifyDb.AppUsers.CountAsync(user => user.OrganizationId == organizationId && user.IsActive && user.Role == UserRole.Administrator));
        Assert.Equal(1, await verifyDb.AuditEvents.CountAsync(item => item.OrganizationId == organizationId && item.Action == "admin.account_suspended"));
    }

    private static AppUser User(Guid id, string name, Guid organizationId, Guid branchId) =>
        new(id, name, $"{name}@example.test", PasswordHasher.HashPassword("Admin123!"), UserRole.Administrator, true, organizationId, branchId);

    private async Task<HttpClient> Login(string userName)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { userName, password = "Admin123!", mfaCode = (string?)null });
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<Login>())!.Token);
        return client;
    }

    private sealed record Login(string Token);
}
