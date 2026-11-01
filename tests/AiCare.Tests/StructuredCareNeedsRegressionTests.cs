using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiCare.Api;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiCare.Tests;
public sealed partial class MeasurableCareGoalsRegressionTests
{
    private static string NeedsRoot(Guid plan) => $"/api/phase1/care-plans/{plan}/needs";
    private static StructuredCareNeedRequest Need(Guid owner) => new(Guid.NewGuid(), 1, "Personal care",
        "Needs support to wash while preserving independence", "Hands-on support as assessed", "Comfort and agreed personal hygiene",
        "Offer agreed choices, assist only where needed and record concerns", owner, "Active", "Need agreed with the person");

    [Fact]
    public async Task StructuredNeedsUseDraftRevisionsExactRetriesAndImmutableCorrections()
    {
        var f = await Setup(); var root = NeedsRoot(f.Plan); var need = Need(f.Owner);
        await Expect(await f.Manager.PostAsJsonAsync(root, need with { IdentifiedNeed = " " }), HttpStatusCode.BadRequest);
        await Expect(await f.Manager.PostAsJsonAsync(root, need with { OwnerUserId = Guid.NewGuid() }), HttpStatusCode.BadRequest);
        var attempts = await Task.WhenAll(f.Manager.PostAsJsonAsync(root, need), f.Manager.PostAsJsonAsync(root, need));
        Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.Created); Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.OK);
        await Expect(await f.Manager.PostAsJsonAsync(root, need with { Actions = "Changed replay" }), HttpStatusCode.Conflict);
        await Expect(await f.Manager.PostAsJsonAsync(root, need with { RequestId = Guid.NewGuid() }), HttpStatusCode.Conflict);
        var correction = need with { RequestId = Guid.NewGuid(), ExpectedPlanRevision = 2, SupersedesNeedId = need.RequestId,
            Status = "NoLongerRequired", ChangeReason = "Reassessment confirms this support is no longer required" };
        await Expect(await f.Manager.PostAsJsonAsync(root, correction), HttpStatusCode.Created);
        var state = await f.Manager.GetFromJsonAsync<JsonElement>(root); Assert.Equal(3, state.GetProperty("revision").GetInt64());
        Assert.Equal(2, state.GetProperty("needs").GetArrayLength());
        var latest = Assert.Single(state.GetProperty("needs").EnumerateArray(), n => n.GetProperty("is_current").GetBoolean());
        Assert.Equal("NoLongerRequired", latest.GetProperty("status").GetString());
        Assert.Equal(need.RequestId, latest.GetProperty("root_need_id").GetGuid());
        await Expect(await f.Manager.PostAsJsonAsync(root, correction with { RequestId = Guid.NewGuid(), ExpectedPlanRevision = 3 }), HttpStatusCode.Conflict);
        await Submit(f, f.Plan);
        await Expect(await f.Manager.PostAsJsonAsync(root, need with { RequestId = Guid.NewGuid(), ExpectedPlanRevision = await Revision(f.Manager, f.Plan) }), HttpStatusCode.Conflict);
        await Expect(await f.Manager.PostAsJsonAsync(root, need), HttpStatusCode.OK); // Replay remains safe after submission.
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("update care_plan_needs set actions='overwrite' where id={0}", need.RequestId));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("delete from care_plan_needs where id={0}", correction.RequestId));
        Assert.Equal(2, await db.AuditEvents.CountAsync(e => e.Action == "care_plan.need_recorded" && (e.EntityId == need.RequestId || e.EntityId == correction.RequestId)));
    }

    [Fact]
    public async Task StructuredNeedsCopyTheLatestDefinitionAndRestrictWorkersToAssignedActivePlans()
    {
        var f = await Setup(); var root = NeedsRoot(f.Plan); var need = Need(f.Owner);
        await Expect(await f.OtherBranch.GetAsync(root), HttpStatusCode.NotFound);
        await Expect(await (await ForeignManager()).PostAsJsonAsync(root, need), HttpStatusCode.NotFound);
        await Expect(await f.Worker.GetAsync(root), HttpStatusCode.NotFound);
        await Expect(await f.Worker.PostAsJsonAsync(root, need), HttpStatusCode.Forbidden);
        await Expect(await f.Manager.PostAsJsonAsync(root, need), HttpStatusCode.Created);
        var correction = need with { RequestId = Guid.NewGuid(), ExpectedPlanRevision = 2, SupersedesNeedId = need.RequestId,
            Actions = "Updated agreed assistance after assessment", ChangeReason = "Clarified the assistance required" };
        await Expect(await f.Manager.PostAsJsonAsync(root, correction), HttpStatusCode.Created);
        await Activate(f, f.Plan, seed: true);
        var worker = await f.Worker.GetFromJsonAsync<JsonElement>(root);
        Assert.Equal(correction.RequestId, Assert.Single(worker.GetProperty("needs").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Empty(worker.GetProperty("owners").EnumerateArray());
        await Expect(await f.Unassigned.GetAsync(root), HttpStatusCode.NotFound);
        var response = await f.Manager.PostAsJsonAsync($"/api/phase1/care-plans/{f.Plan}/revisions", new { expectedRevision = await Revision(f.Manager, f.Plan), changeReason = "Review personal care needs" });
        await Expect(response, HttpStatusCode.Created);
        var revised = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("carePlan").GetProperty("id").GetGuid();
        var copyView = await f.Manager.GetFromJsonAsync<JsonElement>(NeedsRoot(revised));
        var copied = Assert.Single(copyView.GetProperty("needs").EnumerateArray());
        Assert.Equal(correction.RequestId, copied.GetProperty("source_need_id").GetGuid());
        Assert.Equal(need.RequestId, copied.GetProperty("root_need_id").GetGuid());
        Assert.Equal(correction.Actions, copied.GetProperty("actions").GetString());
        await Expect(await f.Worker.GetAsync(NeedsRoot(revised)), HttpStatusCode.NotFound);
        await Expect(await f.Manager.PostAsJsonAsync(NeedsRoot(revised), correction with { RequestId = Guid.NewGuid(),
            ExpectedPlanRevision = copyView.GetProperty("revision").GetInt64(), SupersedesNeedId = copied.GetProperty("id").GetGuid(),
            Actions = "New plan instructions", ChangeReason = "Agreed at review" }), HttpStatusCode.Created);
        var original = await f.Manager.GetFromJsonAsync<JsonElement>(root);
        Assert.Equal(correction.Actions, Assert.Single(original.GetProperty("needs").EnumerateArray(), n => n.GetProperty("is_current").GetBoolean()).GetProperty("actions").GetString());
    }

    [Fact]
    public async Task StructuredNeedsRequireAnEligibleOwnerBeforePlanSubmission()
    {
        var f = await Setup(); var owner = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>(); var name = $"need.owner.{Guid.NewGuid():N}";
            db.AppUsers.Add(new AppUser(owner, name, name + "@test.invalid", PasswordHasher.HashPassword("Admin123!"), UserRole.CareCoordinator,
                true, TenantDefaults.OrganizationId, TenantDefaults.BranchId)); await db.SaveChangesAsync();
        }
        var need = Need(owner); await Expect(await f.Manager.PostAsJsonAsync(NeedsRoot(f.Plan), need), HttpStatusCode.Created);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            await db.Database.ExecuteSqlRawAsync("update \"AppUsers\" set \"IsActive\"=false where \"Id\"={0}", owner);
        }
        await Expect(await f.Manager.PostAsJsonAsync($"/api/phase1/care-plans/{f.Plan}/submit-review", new { expectedRevision = 2, comment = "Review needs" }), HttpStatusCode.BadRequest);
        await Expect(await f.Manager.PostAsJsonAsync(NeedsRoot(f.Plan), need with { RequestId = Guid.NewGuid(), ExpectedPlanRevision = 2,
            SupersedesNeedId = need.RequestId, OwnerUserId = f.Owner, ChangeReason = "Assign an eligible care-team owner" }), HttpStatusCode.Created);
        await Submit(f, f.Plan);
    }
}
