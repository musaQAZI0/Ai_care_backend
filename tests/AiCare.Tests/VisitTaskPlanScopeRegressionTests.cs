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

    [Theory]
    [InlineData(UserRole.CareManager)]
    [InlineData(UserRole.Administrator)]
    public async Task SupersessionBlocksPendingSnapshotsAndPreservesCompletedEvidence(UserRole reviewerRole)
    {
        await factory.EnsureClinicalSeedAsync();
        var person = Guid.NewGuid(); var worker = Guid.NewGuid(); var visit = Guid.NewGuid(); var freshVisit = Guid.NewGuid();
        var actor = $"snapshot.worker.{Guid.NewGuid():N}"; Guid plan; Guid replacementPlan;
        var reviewer = actor + ".reviewer"; var otherManager = actor + ".other"; var otherBranch = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Branches.Add(new(otherBranch, TenantDefaults.OrganizationId, "Other reconciliation branch", "North", "Active"));
            db.ServiceUsers.Add(new(person, "Fictional snapshot person", new DateOnly(1945, 1, 1), "123", "Support", "Contact", "", RiskLevel.Low,
                "Active", "Address", "None", "None", "Private", "", "", "Independent", "Independent", "Verbal", "Routine", "Standard", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.CareWorkers.Add(new(worker, "Snapshot worker", "Care", "Flexible", 0, 0, "Valid", "Compliant", "Local", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.AppUsers.Add(new(Guid.NewGuid(), actor, $"{actor}@example.test", PasswordHasher.HashPassword("Admin123!"), UserRole.CareWorker,
                true, TenantDefaults.OrganizationId, TenantDefaults.BranchId, worker));
            db.AppUsers.Add(new(Guid.NewGuid(), reviewer, $"{reviewer}@example.test", PasswordHasher.HashPassword("Admin123!"), reviewerRole,
                true, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.AppUsers.Add(new(Guid.NewGuid(), otherManager, $"{otherManager}@example.test", PasswordHasher.HashPassword("Admin123!"), UserRole.CareManager,
                true, TenantDefaults.OrganizationId, otherBranch));
            foreach (var id in new[] { visit, freshVisit })
                db.Visits.Add(new(id, person, worker, DateTimeOffset.UtcNow.AddHours(id == visit ? -1 : -2), "Care", 30, "Care", VisitStatus.Scheduled,
                    null, null, null, null, null, null, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await db.SaveChangesAsync();
            plan = await Plan(db, person, TenantDefaults.OrganizationId, TenantDefaults.BranchId, 1);
            await TaskRow(db, plan, person, TenantDefaults.OrganizationId, TenantDefaults.BranchId, "PENDING OLD INSTRUCTION");
            await TaskRow(db, plan, person, TenantDefaults.OrganizationId, TenantDefaults.BranchId, "COMPLETED HISTORICAL INSTRUCTION");
            await SetState(db, plan, "Active");
        }
        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = actor, password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<Login>())!.Token);
        var rows = await client.GetFromJsonAsync<JsonElement>($"/api/phase1/visits/{visit}/tasks");
        var pending = rows.EnumerateArray().Single(item => item.GetProperty("title").GetString() == "PENDING OLD INSTRUCTION").GetProperty("id").GetGuid();
        var completed = rows.EnumerateArray().Single(item => item.GetProperty("title").GetString() == "COMPLETED HISTORICAL INSTRUCTION").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/phase1/visits/{visit}/tasks/{completed}/outcome", new { outcome = "Completed", exceptionReason = "" })).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            await SetState(db, plan, "Superseded");
            replacementPlan = await Plan(db, person, TenantDefaults.OrganizationId, TenantDefaults.BranchId, 2);
            await TaskRow(db, replacementPlan, person, TenantDefaults.OrganizationId, TenantDefaults.BranchId, "REPLACEMENT INSTRUCTION");
            await SetState(db, replacementPlan, "Active");
        }
        var response = await client.GetAsync($"/api/phase1/visits/{visit}/tasks");
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("PENDING OLD INSTRUCTION", text);
        Assert.Contains("COMPLETED HISTORICAL INSTRUCTION", text);
        var after = JsonSerializer.Deserialize<JsonElement>(text).EnumerateArray().ToArray();
        Assert.Equal("RequiresReview", after.Single(item => item.GetProperty("id").GetGuid() == pending).GetProperty("status").GetString());
        Assert.Equal("", after.Single(item => item.GetProperty("id").GetGuid() == pending).GetProperty("instructions").GetString());
        Assert.Equal("Completed", after.Single(item => item.GetProperty("id").GetGuid() == completed).GetProperty("status").GetString());
        foreach (var id in new[] { pending, completed })
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/phase1/visits/{visit}/tasks/{id}/outcome", new { outcome = "Completed", exceptionReason = "" })).StatusCode);
        var fresh = await client.GetStringAsync($"/api/phase1/visits/{freshVisit}/tasks");
        Assert.Contains("REPLACEMENT INSTRUCTION", fresh);
        Assert.DoesNotContain("OLD INSTRUCTION", fresh);
        var reviewPath = $"/api/phase1/visits/{visit}/tasks/reconcile";
        var request = new { carePlanId = replacementPlan, reason = "Reviewed replacement plan against completed visit evidence" };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(reviewPath, request)).StatusCode);
        using var manager = await ReviewClient(reviewer);
        using var other = await ReviewClient(otherManager);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync(reviewPath, request)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync(reviewPath, request with { reason = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync(reviewPath, request with { carePlanId = plan })).StatusCode);
        var reconciled = await manager.PostAsJsonAsync(reviewPath, request);
        Assert.Equal(HttpStatusCode.OK, reconciled.StatusCode);
        var reviewedRows = (await reconciled.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToArray();
        Assert.Equal("Retired", reviewedRows.Single(item => item.GetProperty("id").GetGuid() == pending).GetProperty("status").GetString());
        Assert.Equal("Completed", reviewedRows.Single(item => item.GetProperty("id").GetGuid() == completed).GetProperty("status").GetString());
        Assert.Single(reviewedRows, item => item.GetProperty("status").GetString() == "Pending" && item.GetProperty("instructions").GetString() == "REPLACEMENT INSTRUCTION");
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync(reviewPath, request)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/phase1/visits/{visit}/tasks/{pending}/outcome", new { outcome = "Completed", exceptionReason = "" })).StatusCode);
        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        var stored = await verifyDb.Database.SqlQuery<string>($"""select instructions as "Value" from visit_tasks where id={pending}""").SingleAsync();
        Assert.Equal("PENDING OLD INSTRUCTION", stored);
        Assert.False(await verifyDb.AuditEvents.AnyAsync(item => item.EntityId == pending && item.Action == "visit_task.outcome_recorded"));
        Assert.Equal(1, await verifyDb.AuditEvents.CountAsync(item => item.EntityId == completed && item.Action == "visit_task.outcome_recorded"));
        var closed = await verifyDb.Visits.SingleAsync(item => item.Id == visit);
        verifyDb.Entry(closed).CurrentValues.SetValues(closed with { Status = VisitStatus.Completed });
        await verifyDb.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync(reviewPath, request)).StatusCode);
        Assert.Equal(1, await verifyDb.AuditEvents.CountAsync(item => item.EntityId == visit && item.Action == "visit_tasks.reconciled" && item.Actor == reviewer));
        var reviewReason = await verifyDb.Database.SqlQuery<string>($"""select reason as "Value" from visit_task_reconciliations where visit_id={visit}""").SingleAsync();
        Assert.Equal(request.reason, reviewReason);
        await Assert.ThrowsAnyAsync<Exception>(() => verifyDb.Database.ExecuteSqlInterpolatedAsync($"update visit_task_reconciliations set reason='Rewritten historical review' where visit_id={visit}"));
    }

    private async Task<HttpClient> ReviewClient(string name)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { userName = name, password = "Admin123!", mfaCode = (string?)null });
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<Login>())!.Token);
        return client;
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
