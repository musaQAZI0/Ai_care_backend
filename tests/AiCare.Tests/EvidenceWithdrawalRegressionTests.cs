using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiCare.Api;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiCare.Tests;
public sealed partial class MeasurableCareGoalsRegressionTests
{
    [Fact]
    public async Task EvidenceWithdrawalsAreScopedImmutableAndRetrySafeAndPermitCorrectedRelinking()
    {
        var f = await Setup(); await factory.SeedCarePlanActivationPrerequisitesAsync(f.Person);
        var root = $"/api/phase1/care-plans/{f.Plan}/evidence";
        var source = (await f.Manager.GetFromJsonAsync<JsonElement>(root)).GetProperty("sources").EnumerateArray().First();
        var link = new CarePlanEvidenceRequest(Guid.NewGuid(), 1, source.GetProperty("kind").GetString()!, source.GetProperty("id").GetGuid(), "Personal care", "Original rationale");
        await Expect(await f.Manager.PostAsJsonAsync(root, link), HttpStatusCode.Created);
        var path = $"{root}/{link.RequestId}/withdrawals";
        var withdrawal = new EvidenceWithdrawalRequest(Guid.NewGuid(), 2, "Rationale recorded against the wrong care need");
        await Expect(await f.OtherBranch.PostAsJsonAsync(path, withdrawal), HttpStatusCode.NotFound);
        await Expect(await (await ForeignManager()).PostAsJsonAsync(path, withdrawal), HttpStatusCode.NotFound);
        await Expect(await f.Worker.PostAsJsonAsync(path, withdrawal), HttpStatusCode.Forbidden);
        await Expect(await f.Manager.PostAsJsonAsync(path, withdrawal with { Reason = " " }), HttpStatusCode.BadRequest);
        await Expect(await f.Manager.PostAsJsonAsync(path, withdrawal with { ExpectedPlanRevision = 1 }), HttpStatusCode.Conflict);
        await Expect(await f.Manager.PostAsJsonAsync($"{root}/{Guid.NewGuid()}/withdrawals", withdrawal), HttpStatusCode.NotFound);
        var attempts = await Task.WhenAll(f.Manager.PostAsJsonAsync(path, withdrawal), f.Manager.PostAsJsonAsync(path, withdrawal));
        Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.Created); Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.OK);
        await Expect(await f.Manager.PostAsJsonAsync(path, withdrawal with { Reason = "Changed replay" }), HttpStatusCode.Conflict);
        var state = await f.Manager.GetFromJsonAsync<JsonElement>(root); Assert.Equal(3, state.GetProperty("revision").GetInt64());
        var old = Assert.Single(state.GetProperty("links").EnumerateArray()); Assert.True(old.GetProperty("is_withdrawn").GetBoolean());
        Assert.Equal("Original rationale", old.GetProperty("rationale").GetString()); Assert.Equal(withdrawal.Reason, old.GetProperty("withdrawal_reason").GetString());
        await Expect(await f.Manager.PostAsJsonAsync(path, withdrawal with { RequestId = Guid.NewGuid(), ExpectedPlanRevision = 3 }), HttpStatusCode.Conflict);
        var corrected = link with { RequestId = Guid.NewGuid(), ExpectedPlanRevision = 3, Rationale = "Corrected rationale linked to the agreed care need" };
        await Expect(await f.Manager.PostAsJsonAsync(root, corrected), HttpStatusCode.Created);
        await Expect(await f.Manager.PostAsJsonAsync(root, corrected with { RequestId = Guid.NewGuid(), ExpectedPlanRevision = 4 }), HttpStatusCode.Conflict);
        state = await f.Manager.GetFromJsonAsync<JsonElement>(root);
        Assert.Equal(2, state.GetProperty("links").GetArrayLength());
        Assert.Equal(corrected.RequestId, Assert.Single(state.GetProperty("links").EnumerateArray(), e => !e.GetProperty("is_withdrawn").GetBoolean()).GetProperty("id").GetGuid());
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("update care_plan_evidence_withdrawals set reason='overwrite' where id={0}", withdrawal.RequestId));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("delete from care_plan_evidence_withdrawals where id={0}", withdrawal.RequestId));
        Assert.Single(await db.AuditEvents.Where(e => e.EntityId == withdrawal.RequestId && e.Action == "care_plan.evidence_withdrawn").ToListAsync());
        await Activate(f, f.Plan, seed: false);
        await Expect(await f.Manager.PostAsJsonAsync(path, withdrawal), HttpStatusCode.OK);
        await Expect(await f.Manager.PostAsJsonAsync($"{root}/{corrected.RequestId}/withdrawals", new EvidenceWithdrawalRequest(Guid.NewGuid(), await Revision(f.Manager, f.Plan), "Cannot change a signed plan")), HttpStatusCode.Conflict);
        var revision = await f.Manager.PostAsJsonAsync($"/api/phase1/care-plans/{f.Plan}/revisions", new { changeReason = "Review evidence", expectedRevision = await Revision(f.Manager, f.Plan) });
        await Expect(revision, HttpStatusCode.Created);
        var next = (await revision.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("carePlan").GetProperty("id").GetGuid();
        var copied = Assert.Single((await f.Manager.GetFromJsonAsync<JsonElement>($"/api/phase1/care-plans/{next}/evidence")).GetProperty("links").EnumerateArray());
        Assert.Equal(corrected.RequestId, copied.GetProperty("copied_from_id").GetGuid());
        Assert.False(copied.GetProperty("is_withdrawn").GetBoolean());
        Assert.Equal(corrected.Rationale, copied.GetProperty("rationale").GetString());
    }
}
