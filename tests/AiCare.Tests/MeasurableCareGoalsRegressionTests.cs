using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiCare.Api;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed partial class MeasurableCareGoalsRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task GoalDefinitionsRespectDraftRevisionConcurrencyAndAppendOnlyHistory()
    {
        var f = await Setup(); var request = Goal(f.Owner, 1);
        var attempts = await Task.WhenAll(f.Manager.PostAsJsonAsync(Root(f.Plan), request), f.Manager.PostAsJsonAsync(Root(f.Plan), request));
        Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.OK);
        var state = await View(f.Manager, f.Plan); Assert.Equal(2, state.GetProperty("revision").GetInt64());
        Assert.Single(state.GetProperty("goals").EnumerateArray());
        await Expect(await f.Manager.PostAsJsonAsync(Root(f.Plan), request with { Title = "Changed replay" }), HttpStatusCode.Conflict);
        await Expect(await f.Manager.PostAsJsonAsync(Root(f.Plan), request with { RequestId = Guid.NewGuid() }), HttpStatusCode.Conflict);
        var next = request with { RequestId = Guid.NewGuid(), ExpectedPlanRevision = 2, TargetValue = 40, SupersedesGoalId = request.RequestId, ChangeReason = "Revised target after discussion" };
        await Expect(await f.Manager.PostAsJsonAsync(Root(f.Plan), next), HttpStatusCode.Created);
        state = await View(f.Manager, f.Plan); Assert.Equal(2, state.GetProperty("goals").GetArrayLength());
        Assert.Single(state.GetProperty("goals").EnumerateArray(), g => g.GetProperty("is_current").GetBoolean());
        await Submit(f, f.Plan);
        await Expect(await f.Manager.PostAsJsonAsync(Root(f.Plan), next with { RequestId = Guid.NewGuid(), ExpectedPlanRevision = await Revision(f.Manager, f.Plan) }), HttpStatusCode.Conflict);
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("update care_plan_measurable_goals set target_value=100 where id={0}", request.RequestId));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("delete from care_plan_measurable_goals where id={0}", next.RequestId));
        Assert.Equal(2, await db.AuditEvents.CountAsync(e => e.EntityId == request.RequestId || e.EntityId == next.RequestId));
    }

    [Fact]
    public async Task ActivePlanProgressCorrectionsAndCopiedGoalVersionsPreserveOriginalEvidence()
    {
        var f = await Setup(); var goal = Goal(f.Owner, 1);
        await Expect(await f.Manager.PostAsJsonAsync(Root(f.Plan), goal), HttpStatusCode.Created);
        var progress = Progress();
        await Expect(await f.Manager.PostAsJsonAsync(Path(f.Plan, goal.RequestId), progress), HttpStatusCode.Conflict);
        await Activate(f, f.Plan, seed: true);
        var revisionBefore = await Revision(f.Manager, f.Plan);
        var attempts = await Task.WhenAll(f.Worker.PostAsJsonAsync(Path(f.Plan, goal.RequestId), progress), f.Worker.PostAsJsonAsync(Path(f.Plan, goal.RequestId), progress));
        Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.Created); Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(revisionBefore, await Revision(f.Manager, f.Plan));
        var correction = progress with { RequestId = Guid.NewGuid(), MeasuredValue = 12, CorrectsEventId = progress.RequestId, CorrectionReason = "Transcription corrected from source note" };
        await Expect(await f.Manager.PostAsJsonAsync(Path(f.Plan, goal.RequestId), correction), HttpStatusCode.Created);
        var state = await View(f.Manager, f.Plan);
        Assert.Equal(2, state.GetProperty("progress").GetArrayLength());
        Assert.True(state.GetProperty("progress").EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == progress.RequestId).GetProperty("is_corrected").GetBoolean());
        var revision = await f.Manager.PostAsJsonAsync($"/api/phase1/care-plans/{f.Plan}/revisions", new { changeReason = "Review measurable goals", expectedRevision = revisionBefore });
        await Expect(revision, HttpStatusCode.Created);
        var revised = (await revision.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("carePlan").GetProperty("id").GetGuid();
        var copied = await View(f.Manager, revised);
        var copiedGoal = Assert.Single(copied.GetProperty("goals").EnumerateArray());
        Assert.Equal(goal.RequestId, copiedGoal.GetProperty("source_goal_id").GetGuid());
        Assert.Equal(goal.RequestId, copiedGoal.GetProperty("root_goal_id").GetGuid());
        Assert.Equal(2, copied.GetProperty("progress").GetArrayLength());
        await Activate(f, revised, seed: false);
        await Expect(await f.Worker.PostAsJsonAsync(Path(f.Plan, goal.RequestId), progress with { RequestId = Guid.NewGuid() }), HttpStatusCode.Conflict);
        var historicalCorrection = correction with { RequestId = Guid.NewGuid(), CorrectsEventId = correction.RequestId, MeasuredValue = 11, CorrectionReason = "Historical evidence verified" };
        await Expect(await f.Manager.PostAsJsonAsync(Path(f.Plan, goal.RequestId), historicalCorrection), HttpStatusCode.Created);
        Assert.Equal(3, (await View(f.Manager, revised)).GetProperty("progress").GetArrayLength());
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("update care_goal_progress set measured_value=999 where id={0}", progress.RequestId));
        Assert.True(await db.AuditEvents.AnyAsync(a => a.EntityId == correction.RequestId && a.Action == "care_goal.progress_corrected"));
    }

    [Fact]
    public async Task GoalAndProgressAccessRejectForeignBranchAndUnassignedWorkers()
    {
        var f = await Setup(); var goal = Goal(f.Owner, 1);
        await Expect(await f.OtherBranch.GetAsync(Root(f.Plan)), HttpStatusCode.NotFound);
        var foreign = await ForeignManager();
        await Expect(await foreign.GetAsync(Root(f.Plan)), HttpStatusCode.NotFound);
        await Expect(await foreign.PostAsJsonAsync(Root(f.Plan), goal), HttpStatusCode.NotFound);
        await Expect(await f.Unassigned.GetAsync(Root(f.Plan)), HttpStatusCode.NotFound);
        await Expect(await f.Worker.PostAsJsonAsync(Root(f.Plan), goal), HttpStatusCode.Forbidden);
        await Expect(await f.Manager.PostAsJsonAsync(Root(f.Plan), goal with { OwnerUserId = Guid.NewGuid() }), HttpStatusCode.BadRequest);
        await Expect(await f.Manager.PostAsJsonAsync(Root(f.Plan), goal), HttpStatusCode.Created);
        await Activate(f, f.Plan, seed: true);
        await Expect(await f.Worker.GetAsync(Root(f.Plan)), HttpStatusCode.OK);
        await Expect(await f.OtherBranch.PostAsJsonAsync(Path(f.Plan, goal.RequestId), Progress()), HttpStatusCode.NotFound);
        await Expect(await f.Unassigned.PostAsJsonAsync(Path(f.Plan, goal.RequestId), Progress()), HttpStatusCode.NotFound);
        await Expect(await f.Manager.PostAsJsonAsync(Path(f.Plan, goal.RequestId), Progress() with { MeasuredValue = null, Status = "Achieved" }), HttpStatusCode.BadRequest);
    }

    private static string Root(Guid plan) => $"/api/phase1/care-plans/{plan}/measurable-goals";
    private static string Path(Guid plan, Guid goal) => $"{Root(plan)}/{goal}/progress";
    private static CareGoalRequest Goal(Guid owner, long revision)
    {
        var day = AppointmentWorkflow.UkDate(DateTimeOffset.UtcNow);
        return new(Guid.NewGuid(), revision, "Walk to the dining room", 10, 30, "metres", day, day.AddDays(7), day.AddDays(30), owner, "Agreed supported practice", "Goal agreed with the person");
    }
    private static CareGoalProgressRequest Progress() => new(Guid.NewGuid(), 15, "InProgress", "Observed walking with agreed support", DateTimeOffset.UtcNow);
    private static Task<JsonElement> View(HttpClient client, Guid plan) => client.GetFromJsonAsync<JsonElement>(Root(plan));
    private static async Task<long> Revision(HttpClient client, Guid plan) => (await View(client, plan)).GetProperty("revision").GetInt64();
    private static async Task Submit(Fixture f, Guid plan) => await Expect(await f.Manager.PostAsJsonAsync($"/api/phase1/care-plans/{plan}/submit-review", new { expectedRevision = await Revision(f.Manager, plan), comment = "Goal reviewed" }), HttpStatusCode.OK);
    private async Task Activate(Fixture f, Guid plan, bool seed)
    {
        await Submit(f, plan);
        await Expect(await f.Manager.PostAsJsonAsync($"/api/phase1/care-plans/{plan}/lifecycle/approve", new { expectedRevision = await Revision(f.Manager, plan), comment = "Reviewed baseline, target and owner" }), HttpStatusCode.OK);
        foreach (var signer in new[] { "CareManager", "Representative" })
            await Expect(await f.Manager.PostAsJsonAsync($"/api/phase1/care-plans/{plan}/signatures", new { expectedRevision = await Revision(f.Manager, plan), signerType = signer, signerName = "Regression signer", relationship = "Authorised representative", declaration = "Reviewed this exact plan version", signatureMethod = "AuthenticatedConfirmation" }), HttpStatusCode.OK);
        if (seed) await factory.SeedCarePlanActivationPrerequisitesAsync(f.Person);
        await Expect(await f.Manager.PostAsJsonAsync($"/api/phase1/care-plans/{plan}/activate", new { expectedRevision = await Revision(f.Manager, plan) }), HttpStatusCode.OK);
    }
    private async Task<Fixture> Setup()
    {
        await factory.EnsureClinicalSeedAsync(); var person = Guid.NewGuid(); var worker = Guid.NewGuid(); var unassigned = Guid.NewGuid(); var owner = Guid.NewGuid(); var branch = Guid.NewGuid(); var prefix = $"goals.{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Branches.Add(new Branch(branch, TenantDefaults.OrganizationId, "Other goal branch", "Test", "Active"));
            db.ServiceUsers.Add(new ServiceUser(person, "Goal person", new DateOnly(1950, 1, 1), "1", "Care", "Contact", "", RiskLevel.Low, "Active", "Address", "None", "None", "Private", "", "", "Independent", "Independent", "Verbal", "Routine", "Standard", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            foreach (var id in new[] { worker, unassigned }) db.CareWorkers.Add(new CareWorker(id, "Goal worker", "Care", "Flexible", 0, 0, "Valid", "Compliant", "10", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.AppUsers.Add(new AppUser(owner, prefix + "manager", prefix + "manager@test.invalid", PasswordHasher.HashPassword("Admin123!"), UserRole.CareManager, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.AppUsers.Add(new AppUser(Guid.NewGuid(), prefix + "worker", prefix + "worker@test.invalid", PasswordHasher.HashPassword("Admin123!"), UserRole.CareWorker, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId, worker));
            db.AppUsers.Add(new AppUser(Guid.NewGuid(), prefix + "unassigned", prefix + "unassigned@test.invalid", PasswordHasher.HashPassword("Admin123!"), UserRole.CareWorker, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId, unassigned));
            db.AppUsers.Add(new AppUser(Guid.NewGuid(), prefix + "other", prefix + "other@test.invalid", PasswordHasher.HashPassword("Admin123!"), UserRole.CareManager, true, TenantDefaults.OrganizationId, branch));
            var start = DateTimeOffset.UtcNow.AddHours(-2);
            db.Visits.Add(new Visit(Guid.NewGuid(), person, worker, start, "Care", 60, "Care", VisitStatus.Completed, start, start.AddMinutes(60), null, null, null, null, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await db.SaveChangesAsync();
        }
        var manager = await Client(prefix + "manager");
        var created = await manager.PostAsJsonAsync("/api/phase1/care-plans", new { serviceUserId = person, personalCare = "Agreed care", medicationSupport = "Reviewed support", mobilityAndTransfers = "Agreed mobility", nutrition = "Agreed nutrition", reviewDueAt = DateTimeOffset.UtcNow.AddMonths(2) });
        await Expect(created, HttpStatusCode.Created); var plan = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        return new(manager, await Client(prefix + "worker"), await Client(prefix + "unassigned"), await Client(prefix + "other"), plan, person, owner);
    }
    private async Task<HttpClient> Client(string name)
    {
        var c = factory.CreateClient(); var login = await c.PostAsJsonAsync("/api/auth/login", new { userName = name, password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode(); c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<Login>())!.Token); return c;
    }
    private async Task<HttpClient> ForeignManager()
    {
        var org = Guid.NewGuid(); var branch = Guid.NewGuid(); var name = $"foreign.goals.{Guid.NewGuid():N}";
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        db.Organizations.Add(new Organization(org, "Other goal tenant", "Trial", "Active"));
        db.Branches.Add(new Branch(branch, org, "Other", "Test", "Active"));
        db.AppUsers.Add(new AppUser(Guid.NewGuid(), name, name + "@test.invalid", PasswordHasher.HashPassword("Admin123!"), UserRole.CareManager, true, org, branch));
        await db.SaveChangesAsync(); return await Client(name);
    }
    private static async Task Expect(HttpResponseMessage r, HttpStatusCode code) => Assert.True(r.StatusCode == code, $"Expected {code}, received {r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
    private sealed record Fixture(HttpClient Manager, HttpClient Worker, HttpClient Unassigned, HttpClient OtherBranch, Guid Plan, Guid Person, Guid Owner);
    private sealed record Login(string Token);
}
