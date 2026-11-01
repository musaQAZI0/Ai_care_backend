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
    public async Task EvidencePinsSignedSourcesAndPreservesScopeRevisionAndHistory()
    {
        var f = await Setup(); await factory.SeedCarePlanActivationPrerequisitesAsync(f.Person);
        var root = $"/api/phase1/care-plans/{f.Plan}/evidence";
        var view = await f.Manager.GetFromJsonAsync<JsonElement>(root);
        var source = view.GetProperty("sources").EnumerateArray().First(s => s.GetProperty("kind").GetString() == "Assessment");
        var request = new CarePlanEvidenceRequest(Guid.NewGuid(), 1, "Assessment", source.GetProperty("id").GetGuid(),
            "Personal care", "Support reflects the signed initial needs assessment");
        await Expect(await f.OtherBranch.GetAsync(root), HttpStatusCode.NotFound);
        await Expect(await (await ForeignManager()).PostAsJsonAsync(root, request), HttpStatusCode.NotFound);
        await Expect(await f.Worker.GetAsync(root), HttpStatusCode.Forbidden);
        await Expect(await f.Manager.PostAsJsonAsync(root, request with { SourceId = Guid.NewGuid() }), HttpStatusCode.Conflict);
        var attempts = await Task.WhenAll(f.Manager.PostAsJsonAsync(root, request), f.Manager.PostAsJsonAsync(root, request));
        Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.OK);
        await Expect(await f.Manager.PostAsJsonAsync(root, request with { Rationale = "Changed replay" }), HttpStatusCode.Conflict);
        await Expect(await f.Manager.PostAsJsonAsync(root, request with { RequestId = Guid.NewGuid(), Section = "Nutrition" }), HttpStatusCode.Conflict);
        view = await f.Manager.GetFromJsonAsync<JsonElement>(root);
        Assert.Equal(2, view.GetProperty("revision").GetInt64());
        var link = Assert.Single(view.GetProperty("links").EnumerateArray());
        Assert.Equal(request.SourceId, link.GetProperty("source_snapshot").GetProperty("id").GetGuid());
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("delete from care_plan_evidence where id={0}", request.RequestId));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("update care_plan_evidence set rationale='changed' where id={0}", request.RequestId));
        await Activate(f, f.Plan, seed: false);
        await Expect(await f.Manager.PostAsJsonAsync(root, request with { RequestId = Guid.NewGuid(), ExpectedPlanRevision = await Revision(f.Manager, f.Plan) }), HttpStatusCode.Conflict);
        var response = await f.Manager.PostAsJsonAsync($"/api/phase1/care-plans/{f.Plan}/revisions", new { changeReason = "Review source evidence", expectedRevision = await Revision(f.Manager, f.Plan) });
        await Expect(response, HttpStatusCode.Created);
        var revised = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("carePlan").GetProperty("id").GetGuid();
        var copiedView = await f.Manager.GetFromJsonAsync<JsonElement>($"/api/phase1/care-plans/{revised}/evidence");
        var copied = Assert.Single(copiedView.GetProperty("links").EnumerateArray());
        Assert.Equal(request.RequestId, copied.GetProperty("copied_from_id").GetGuid());
        Assert.Equal(link.GetProperty("source_snapshot").GetRawText(), copied.GetProperty("source_snapshot").GetRawText());
        Assert.True(await db.AuditEvents.AnyAsync(e => e.EntityId == request.RequestId && e.Action == "care_plan.evidence_linked"));
    }
}
