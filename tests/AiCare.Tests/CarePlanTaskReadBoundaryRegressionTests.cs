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
public sealed class CarePlanTaskReadBoundaryRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Theory]
    [InlineData(UserRole.CareCoordinator, true)]
    [InlineData(UserRole.CareManager, true)]
    [InlineData(UserRole.Administrator, true)]
    [InlineData(UserRole.CareWorker, false)]
    [InlineData(UserRole.FamilyMember, false)]
    [InlineData(UserRole.ServiceUser, false)]
    [InlineData(UserRole.BackOffice, false)]
    public async Task RawPlanTaskDefinitionsAreRestrictedToAuthoringStaff(UserRole role, bool allowed)
    {
        await factory.EnsureClinicalSeedAsync();
        var person = Guid.NewGuid(); var actor = $"plan.tasks.{Guid.NewGuid():N}";
        var plans = new Dictionary<string, Guid>();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.ServiceUsers.Add(new(person, "Fictional task boundary person", new DateOnly(1945, 1, 1), "+441234567890",
                "Support", "Contact", "", RiskLevel.Low, "Active", "Address", "None", "None", "Private", "", "",
                "Independent", "Independent", "Verbal", "Routine", "Standard", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.AppUsers.Add(new(Guid.NewGuid(), actor, $"{actor}@example.test", PasswordHasher.HashPassword("Admin123!"),
                role, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await db.SaveChangesAsync();
            foreach (var status in new[] { "Draft", "Approved", "Active", "Superseded" })
            {
                var id = Guid.NewGuid(); plans.Add(status, id);
                db.CarePlans.Add(new(id, person, "v1", "Draft", "Support", "Prompts", "Support", "Meals",
                    DateTimeOffset.UtcNow.AddMonths(1), TenantDefaults.OrganizationId, TenantDefaults.BranchId));
                // Lifecycle registration is deferred until commit. Save each draft
                // before inserting the next so its version number is visible.
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    insert into care_plan_tasks(id,care_plan_id,service_user_id,organization_id,branch_id,title,instructions)
                    values({Guid.NewGuid()},{id},{person},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},{status + " private task"},{"Private plan instructions"})
                    """);
                // Seed read-boundary states only after draft-only child creation.
                // Lifecycle transition behavior is tested by the existing lifecycle suites.
                if (status != "Draft")
                {
                    await db.Database.ExecuteSqlInterpolatedAsync($"update care_plan_versions set status={status} where care_plan_id={id}");
                    await db.Database.ExecuteSqlInterpolatedAsync($"""update "CarePlans" set "Status"={status} where "Id"={id}""");
                }
                Assert.Equal(status, (await db.CarePlans.AsNoTracking().SingleAsync(item => item.Id == id)).Status);
            }
        }
        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = actor, password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<Login>())!.Token);
        foreach (var plan in plans)
        {
            var response = await client.GetAsync($"/api/phase1/care-plans/{plan.Value}/tasks");
            Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            if (allowed) Assert.Contains(plan.Key + " private task", body);
            else Assert.DoesNotContain("Private plan instructions", body);
        }
    }
    private sealed record Login(string Token);
}
