using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiCare.Api;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiCare.Tests;
public sealed partial class MeasurableCareGoalsRegressionTests
{
    private static string EmergencyRoot(Guid plan) => $"/api/phase1/care-plans/{plan}/emergency-sections";
    [Fact]
    public async Task EmergencyDesignationsAreDraftVersionedAndHandoverCopiesAreScopedActiveAndAudited()
    {
        var f = await Setup(); var root = EmergencyRoot(f.Plan);
        var selection = new EmergencySectionsRequest(Guid.NewGuid(), 1, ["PersonalCare", "Nutrition"], "Agreed emergency-critical sections");
        await Expect(await f.OtherBranch.GetAsync(root), HttpStatusCode.NotFound);
        await Expect(await (await ForeignManager()).PostAsJsonAsync(root, selection), HttpStatusCode.NotFound);
        await Expect(await f.Worker.GetAsync(root), HttpStatusCode.NotFound);
        await Expect(await f.Worker.PostAsJsonAsync(root, selection), HttpStatusCode.Forbidden);
        await Expect(await f.Manager.PostAsJsonAsync(root, selection with { Sections = ["PersonalCare", "PersonalCare"] }), HttpStatusCode.BadRequest);
        await Expect(await f.Manager.PostAsJsonAsync(root, selection with { Sections = ["Unknown"] }), HttpStatusCode.BadRequest);
        var attempts = await Task.WhenAll(f.Manager.PostAsJsonAsync(root, selection), f.Manager.PostAsJsonAsync(root, selection));
        Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.Created); Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.OK);
        await Expect(await f.Manager.PostAsJsonAsync(root, selection with { Reason = "Changed replay" }), HttpStatusCode.Conflict);
        await Expect(await f.Manager.PostAsJsonAsync(root, selection with { RequestId = Guid.NewGuid() }), HttpStatusCode.Conflict);
        var draftCopy = new EmergencyCopyRequest(Guid.NewGuid(), 2, "Hospital transfer discussion");
        await Expect(await f.Manager.PostAsJsonAsync(root + "/handover-copy", draftCopy), HttpStatusCode.Conflict);
        await Activate(f, f.Plan, seed: true);
        await Expect(await f.Manager.PostAsJsonAsync(root, selection with { RequestId = Guid.NewGuid(), ExpectedPlanRevision = await Revision(f.Manager, f.Plan) }), HttpStatusCode.Conflict);
        await Expect(await f.Manager.PostAsJsonAsync(root, selection), HttpStatusCode.OK);
        var view = await f.Worker.GetFromJsonAsync<JsonElement>(root);
        Assert.Empty(view.GetProperty("history").EnumerateArray()); Assert.Equal(2, view.GetProperty("selectedSections").GetArrayLength());
        var copy = new EmergencyCopyRequest(Guid.NewGuid(), view.GetProperty("revision").GetInt64(), "Hospital handover for assigned person");
        await Expect(await f.Unassigned.PostAsJsonAsync(root + "/handover-copy", copy), HttpStatusCode.NotFound);
        await Expect(await f.OtherBranch.PostAsJsonAsync(root + "/handover-copy", copy), HttpStatusCode.NotFound);
        await Expect(await f.Worker.PostAsJsonAsync(root + "/handover-copy", copy with { ExpectedPlanRevision = 1 }), HttpStatusCode.Conflict);
        var response = await f.Worker.PostAsJsonAsync(root + "/handover-copy", copy); await Expect(response, HttpStatusCode.OK);
        var file = await response.Content.ReadFromJsonAsync<JsonElement>(); var html = file.GetProperty("html").GetString()!;
        Assert.Contains("Goal person", html); Assert.Contains("Agreed care", html); Assert.Contains("Agreed nutrition", html);
        Assert.DoesNotContain("Reviewed support", html); Assert.DoesNotContain("Agreed mobility", html);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html))).ToLowerInvariant(), file.GetProperty("sha256").GetString());
        response = await f.Worker.PostAsJsonAsync(root + "/handover-copy", copy); await Expect(response, HttpStatusCode.OK);
        Assert.Equal(html, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("html").GetString());
        await Expect(await f.Manager.PostAsJsonAsync(root + "/handover-copy", copy), HttpStatusCode.Conflict); // Another author cannot replay the copy.
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal(2, await db.AuditEvents.CountAsync(e => e.EntityId == copy.RequestId && e.Action == "care_plan.handover_downloaded"));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("update care_plan_emergency_selections set reason='overwrite' where id={0}", selection.RequestId));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("delete from care_plan_handover_copies where id={0}", copy.RequestId));
    }

    [Fact]
    public async Task EmergencyDesignationCopiesToRevisionAndOldPlanCannotGenerateANewCopyAfterSupersession()
    {
        var f = await Setup(); var root = EmergencyRoot(f.Plan);
        var selection = new EmergencySectionsRequest(Guid.NewGuid(), 1, ["MobilityAndTransfers"], "Agreed transfer instructions for handover");
        await Expect(await f.Manager.PostAsJsonAsync(root, selection), HttpStatusCode.Created);
        await Activate(f, f.Plan, seed: true);
        var oldCopy = new EmergencyCopyRequest(Guid.NewGuid(), await Revision(f.Manager, f.Plan), "Initial handover");
        await Expect(await f.Manager.PostAsJsonAsync(root + "/handover-copy", oldCopy), HttpStatusCode.OK);
        var response = await f.Manager.PostAsJsonAsync($"/api/phase1/care-plans/{f.Plan}/revisions", new { expectedRevision = await Revision(f.Manager, f.Plan), changeReason = "Review emergency designation" });
        await Expect(response, HttpStatusCode.Created);
        var next = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("carePlan").GetProperty("id").GetGuid();
        var state = await f.Manager.GetFromJsonAsync<JsonElement>(EmergencyRoot(next));
        Assert.Equal("MobilityAndTransfers", Assert.Single(state.GetProperty("selectedSections").EnumerateArray()).GetString());
        Assert.Equal(selection.RequestId, Assert.Single(state.GetProperty("history").EnumerateArray()).GetProperty("source_selection_id").GetGuid());
        var clear = new EmergencySectionsRequest(Guid.NewGuid(), state.GetProperty("revision").GetInt64(), [], "No designated sections pending further review");
        await Expect(await f.Manager.PostAsJsonAsync(EmergencyRoot(next), clear), HttpStatusCode.Created);
        await Activate(f, next, seed: false);
        await Expect(await f.Manager.PostAsJsonAsync(EmergencyRoot(next) + "/handover-copy", new EmergencyCopyRequest(Guid.NewGuid(), await Revision(f.Manager, next), "No content must not produce a misleading pack")), HttpStatusCode.Conflict);
        await Expect(await f.Manager.PostAsJsonAsync(root + "/handover-copy", oldCopy), HttpStatusCode.Conflict);
        await Expect(await f.Worker.GetAsync(root), HttpStatusCode.NotFound);
    }
}
