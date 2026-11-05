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
public sealed class VisitTaskPlanScopeRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Theory]
    [InlineData("Active", 1)]
    [InlineData("Draft", 0)]
    [InlineData("Superseded", 0)]
    public async Task NewVisitTasksRequireMatchingActivePlanLifecycleAndSourceScope(string localStatus, int expected)
    {
        await factory.EnsureClinicalSeedAsync();
        var person = Guid.NewGuid(); var otherPerson = Guid.NewGuid(); var worker = Guid.NewGuid(); var visit = Guid.NewGuid();
        var otherBranch = Guid.NewGuid(); var otherOrg = Guid.NewGuid(); var foreignBranch = Guid.NewGuid();
        var actor = $"plan.scope.worker.{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Organizations.Add(new(otherOrg, "Foreign plan tenant", "Pilot", "Active"));
            db.Branches.AddRange(new Branch(otherBranch, TenantDefaults.OrganizationId, "Other plan branch", "North", "Active"),
                new Branch(foreignBranch, otherOrg, "Foreign plan branch", "North", "Active"));
            foreach (var id in new[] { person, otherPerson })
                db.ServiceUsers.Add(new(id, "Fictional plan scope person", new DateOnly(1945, 1, 1), "+441234567890",
                    "Support", "Contact", "", RiskLevel.Low, "Active", "Address", "None", "None", "Private", "", "",
                    "Independent", "Independent", "Verbal", "Routine", "Standard", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.CareWorkers.Add(new(worker, "Plan scope worker", "Care", "Flexible", 0, 0, "Valid", "Compliant", "Local", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.Visits.Add(new(visit, person, worker, DateTimeOffset.UtcNow.AddHours(-1), "Care", 30, "Care", VisitStatus.Scheduled,
                null, null, null, null, null, null, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.AppUsers.Add(new(Guid.NewGuid(), actor, $"{actor}@example.test", PasswordHasher.HashPassword("Admin123!"), UserRole.CareWorker,
                true, TenantDefaults.OrganizationId, TenantDefaults.BranchId, worker));
            await db.SaveChangesAsync();

            // Read/materialisation fixtures: insert tasks while Draft, then seed the
            // lifecycle state. Existing provider-journey tests exercise real activation.
            var local = await Plan(db, person, TenantDefaults.OrganizationId, TenantDefaults.BranchId, 1);
            await TaskRow(db, local, person, TenantDefaults.OrganizationId, TenantDefaults.BranchId, "MATCHING APPROVED TASK");
            await TaskRow(db, local, otherPerson, TenantDefaults.OrganizationId, TenantDefaults.BranchId, "WRONG PERSON SECRET");
            await TaskRow(db, local, person, TenantDefaults.OrganizationId, otherBranch, "WRONG BRANCH SECRET");
            await TaskRow(db, local, person, otherOrg, foreignBranch, "WRONG TENANT SECRET");
            await TaskRow(db, local, person, TenantDefaults.OrganizationId, TenantDefaults.BranchId, "INACTIVE TASK SECRET", "Inactive");
            await SetState(db, local, localStatus);

            var branchPlan = await Plan(db, person, TenantDefaults.OrganizationId, otherBranch, 36);
            await TaskRow(db, branchPlan, person, TenantDefaults.OrganizationId, otherBranch, "FOREIGN BRANCH PLAN SECRET");
            await SetState(db, branchPlan, "Active");
            var foreign = await Plan(db, person, otherOrg, foreignBranch, 24);
            await TaskRow(db, foreign, person, otherOrg, foreignBranch, "FOREIGN TENANT PLAN SECRET");
            await SetState(db, foreign, "Active");
        }
        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = actor, password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<Login>())!.Token);
        var response = await client.GetAsync($"/api/phase1/visits/{visit}/tasks");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SECRET", body);
        Assert.Equal(expected, JsonSerializer.Deserialize<JsonElement>(body).GetArrayLength());
        if (expected > 0) Assert.Contains("MATCHING APPROVED TASK", body);
        var repeated = await client.GetFromJsonAsync<JsonElement>($"/api/phase1/visits/{visit}/tasks");
        Assert.Equal(expected, repeated.GetArrayLength());
        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        var stored = await verifyDb.Database.SqlQuery<int>($"""select count(*)::integer as "Value" from visit_tasks where visit_id={visit}""").SingleAsync();
        Assert.Equal(expected, stored);
    }

    private static async Task<Guid> Plan(CareDbContext db, Guid person, Guid org, Guid branch, int reviewMonths)
    {
        var id = Guid.NewGuid();
        db.CarePlans.Add(new(id, person, "v1", "Draft", "Care", "Prompts", "Support", "Meals", DateTimeOffset.UtcNow.AddMonths(reviewMonths), org, branch));
        await db.SaveChangesAsync();
        return id;
    }
    private static Task<int> TaskRow(CareDbContext db, Guid plan, Guid person, Guid org, Guid branch, string marker, string status = "Active") =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into care_plan_tasks(id,care_plan_id,service_user_id,organization_id,branch_id,title,instructions,status)
            values({Guid.NewGuid()},{plan},{person},{org},{branch},{marker},{marker},{status})
            """);
    private static async Task SetState(CareDbContext db, Guid plan, string status)
    {
        if (status == "Draft") return;
        await db.Database.ExecuteSqlInterpolatedAsync($"update care_plan_versions set status={status} where care_plan_id={plan}");
        await db.Database.ExecuteSqlInterpolatedAsync($"""update "CarePlans" set "Status"={status} where "Id"={plan}""");
    }
    private sealed record Login(string Token);
}
