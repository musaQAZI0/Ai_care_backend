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
    private static string ReviewRoot(Guid person) => $"/api/phase1/service-users/{person}/plan-reviews";
    private static CarePlanReviewRuleRequest Rule(Guid owner, string kind = "AssessmentApproved") =>
        new(Guid.NewGuid(), 0, kind, true, owner, 24, "Review the plan when this person's needs change");

    [Fact]
    public async Task ReviewRulesVersionRetryAndGenerateAssignedNeedsAndRiskTasks()
    {
        var f = await Setup(); var root = ReviewRoot(f.Person); var rule = Rule(f.Owner);
        await Expect(await f.OtherBranch.GetAsync(root), HttpStatusCode.NotFound);
        await Expect(await (await ForeignManager()).PostAsJsonAsync(root + "/rules", rule), HttpStatusCode.NotFound);
        await Expect(await f.Worker.GetAsync(root), HttpStatusCode.Forbidden);
        await Expect(await f.Manager.PostAsJsonAsync(root + "/rules", rule with { ReviewerId = Guid.NewGuid() }), HttpStatusCode.BadRequest);
        var attempts = await Task.WhenAll(f.Manager.PostAsJsonAsync(root + "/rules", rule), f.Manager.PostAsJsonAsync(root + "/rules", rule));
        Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.Created); Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.OK);
        await Expect(await f.Manager.PostAsJsonAsync(root + "/rules", rule with { Reason = "Changed replay" }), HttpStatusCode.Conflict);
        await Expect(await f.Manager.PostAsJsonAsync(root + "/rules", rule with { RequestId = Guid.NewGuid() }), HttpStatusCode.Conflict);
        await Expect(await f.Manager.PostAsJsonAsync(root + "/rules", Rule(f.Owner, "RiskApproved")), HttpStatusCode.Created);
        await factory.SeedCarePlanActivationPrerequisitesAsync(f.Person);
        var state = await f.Manager.GetFromJsonAsync<JsonElement>(root);
        var items = state.GetProperty("items").EnumerateArray().ToArray(); Assert.Equal(2, items.Length);
        Assert.All(items, i => { Assert.Equal(f.Owner, i.GetProperty("current_reviewer_id").GetGuid()); Assert.Equal("Open", i.GetProperty("status").GetString());
            Assert.Equal(1, i.GetProperty("source_version").GetInt32());
            Assert.Equal(TimeSpan.FromHours(24), i.GetProperty("due_at").GetDateTimeOffset() - i.GetProperty("created_at").GetDateTimeOffset()); });
        var revision = await Revision(f.Manager, f.Plan); Assert.Equal(1, revision);
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        await db.Database.ExecuteSqlRawAsync("update governed_risk_assessments set status=status where service_user_id={0}", f.Person);
        Assert.Equal(2, (await f.Manager.GetFromJsonAsync<JsonElement>(root)).GetProperty("items").GetArrayLength());
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("delete from care_plan_review_rules where id={0}", rule.RequestId));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("update care_plan_review_items set reviewer_name='overwrite' where id={0}", items[0].GetProperty("id").GetGuid()));
        Assert.True(await db.AuditEvents.AnyAsync(e => e.EntityId == rule.RequestId && e.Action == "care_plan.review_rule_configured"));
    }

    [Fact]
    public async Task IncidentRulesApplyProspectivelyAndDisablingPreservesOpenReviews()
    {
        var f = await Setup(); var root = ReviewRoot(f.Person);
        async Task Incident() => await Expect(await f.Manager.PostAsJsonAsync("/api/phase1/incidents", new {
            serviceUserId = f.Person, visitId = (Guid?)null, category = "Fall", severity = "High", description = "Regression review trigger incident"
        }), HttpStatusCode.Created);
        await Incident();
        var rule = Rule(f.Owner, "IncidentReported");
        await Expect(await f.Manager.PostAsJsonAsync(root + "/rules", rule), HttpStatusCode.Created);
        Assert.Empty((await f.Manager.GetFromJsonAsync<JsonElement>(root)).GetProperty("items").EnumerateArray());
        await Incident();
        var state = await f.Manager.GetFromJsonAsync<JsonElement>(root);
        Assert.Single(state.GetProperty("items").EnumerateArray());
        await Expect(await f.Manager.PostAsJsonAsync(root + "/rules", rule with { RequestId = Guid.NewGuid(), ExpectedVersion = 1, Enabled = false, Reason = "Use the agreed manual pathway" }), HttpStatusCode.Created);
        await Incident();
        state = await f.Manager.GetFromJsonAsync<JsonElement>(root);
        Assert.Single(state.GetProperty("items").EnumerateArray());
        Assert.Equal(2, state.GetProperty("rules").GetArrayLength());
    }

    [Fact]
    public async Task ReviewResolutionRequiresDecisionEvidenceOrSubsequentlyActivatedPlanAndKeepsHistory()
    {
        var f = await Setup(); var root = ReviewRoot(f.Person);
        await Expect(await f.Manager.PostAsJsonAsync(root + "/rules", Rule(f.Owner)), HttpStatusCode.Created);
        await factory.SeedCarePlanActivationPrerequisitesAsync(f.Person);
        var state = await f.Manager.GetFromJsonAsync<JsonElement>(root);
        var item = Assert.Single(state.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid();
        var path = $"{root}/{item}/events";
        var decision = new CarePlanReviewEventRequest(Guid.NewGuid(), 0, "RevisedPlanActivated", f.Owner, "Revised the agreed care", f.Plan);
        await Expect(await f.Manager.PostAsJsonAsync(path, decision), HttpStatusCode.Conflict); // Draft is not evidence of activation.
        await Expect(await f.Manager.PostAsJsonAsync(path, decision with { Action = "NoChangeRequired", ResolvedPlanId = null, Reason = "" }), HttpStatusCode.BadRequest);
        var reassignment = new CarePlanReviewEventRequest(Guid.NewGuid(), 0, "Reassigned", f.Owner, "Manager accepted responsibility");
        await Expect(await f.Manager.PostAsJsonAsync(path, reassignment), HttpStatusCode.Created);
        await Expect(await f.Manager.PostAsJsonAsync(path, reassignment), HttpStatusCode.OK);
        await Expect(await f.Manager.PostAsJsonAsync(path, decision with { RequestId = Guid.NewGuid(), Action = "NoChangeRequired", ResolvedPlanId = null }), HttpStatusCode.Conflict);
        await Activate(f, f.Plan, seed: false);
        decision = decision with { RequestId = Guid.NewGuid(), ExpectedRevision = 1 };
        var attempts = await Task.WhenAll(f.Manager.PostAsJsonAsync(path, decision), f.Manager.PostAsJsonAsync(path, decision));
        Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.Created); Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.OK);
        await Expect(await f.Manager.PostAsJsonAsync(path, decision with { RequestId = Guid.NewGuid(), ExpectedRevision = 2 }), HttpStatusCode.Conflict);
        state = await f.Manager.GetFromJsonAsync<JsonElement>(root);
        Assert.Equal("RevisedPlanActivated", Assert.Single(state.GetProperty("items").EnumerateArray()).GetProperty("status").GetString());
        Assert.Equal(2, state.GetProperty("history").GetArrayLength());
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("delete from care_plan_review_events where id={0}", decision.RequestId));
    }

    [Fact]
    public async Task InactiveReviewerDoesNotLoseTasksAndAnotherManagerCanAcceptAndResolveTheReview()
    {
        var f = await Setup(); var root = ReviewRoot(f.Person); var reviewer = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>(); var name = $"reviewer.{Guid.NewGuid():N}";
            db.AppUsers.Add(new AppUser(reviewer, name, name + "@test.invalid", PasswordHasher.HashPassword("Admin123!"),
                UserRole.CareManager, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await db.SaveChangesAsync();
        }
        await Expect(await f.Manager.PostAsJsonAsync(root + "/rules", Rule(reviewer)), HttpStatusCode.Created);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            await db.Database.ExecuteSqlRawAsync("update \"AppUsers\" set \"IsActive\"=false where \"Id\"={0}", reviewer);
        }
        await factory.SeedCarePlanActivationPrerequisitesAsync(f.Person);
        var state = await f.Manager.GetFromJsonAsync<JsonElement>(root);
        var item = Assert.Single(state.GetProperty("items").EnumerateArray());
        Assert.False(item.GetProperty("reviewer_is_current").GetBoolean());
        var path = $"{root}/{item.GetProperty("id").GetGuid()}/events";
        var reassign = new CarePlanReviewEventRequest(Guid.NewGuid(), 0, "Reassigned", f.Owner, "Covering the departed reviewer's work");
        await Expect(await f.Manager.PostAsJsonAsync(path, reassign), HttpStatusCode.Created);
        var decision = new CarePlanReviewEventRequest(Guid.NewGuid(), 1, "NoChangeRequired", f.Owner, "Current plan already addresses all assessed needs");
        await Expect(await f.Manager.PostAsJsonAsync(path, decision), HttpStatusCode.Created);
        Assert.Equal(1, await Revision(f.Manager, f.Plan)); // The signed/draft plan is not rewritten by a review decision.
        state = await f.Manager.GetFromJsonAsync<JsonElement>(root);
        Assert.Equal("NoChangeRequired", Assert.Single(state.GetProperty("items").EnumerateArray()).GetProperty("status").GetString());
        Assert.Equal(2, state.GetProperty("history").GetArrayLength());
    }

    [Fact]
    public async Task ReviewAgainstAnActivePlanRequiresALaterActivatedVersion()
    {
        var f = await Setup(); await Activate(f, f.Plan, seed: true); var root = ReviewRoot(f.Person);
        await Expect(await f.Manager.PostAsJsonAsync(root + "/rules", Rule(f.Owner, "IncidentReported")), HttpStatusCode.Created);
        await Expect(await f.Manager.PostAsJsonAsync("/api/phase1/incidents", new {
            serviceUserId = f.Person, visitId = (Guid?)null, category = "Fall", severity = "High", description = "Review after activation"
        }), HttpStatusCode.Created);
        var item = Assert.Single((await f.Manager.GetFromJsonAsync<JsonElement>(root)).GetProperty("items").EnumerateArray());
        Assert.Equal(f.Plan, item.GetProperty("care_plan_id").GetGuid());
        var path = $"{root}/{item.GetProperty("id").GetGuid()}/events";
        var decision = new CarePlanReviewEventRequest(Guid.NewGuid(), 0, "RevisedPlanActivated", f.Owner, "Reviewed activated instructions", f.Plan);
        await Expect(await f.Manager.PostAsJsonAsync(path, decision), HttpStatusCode.Conflict);
        var response = await f.Manager.PostAsJsonAsync($"/api/phase1/care-plans/{f.Plan}/revisions", new { expectedRevision = await Revision(f.Manager, f.Plan), changeReason = "Respond to incident review" });
        await Expect(response, HttpStatusCode.Created);
        var revised = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("carePlan").GetProperty("id").GetGuid();
        await Expect(await f.Manager.PostAsJsonAsync(path, decision with { ResolvedPlanId = revised }), HttpStatusCode.Conflict);
        await Activate(f, revised, seed: false);
        await Expect(await f.Manager.PostAsJsonAsync(path, decision with { ResolvedPlanId = revised }), HttpStatusCode.Created);
    }

    [Fact]
    public async Task RiskReviewIsGeneratedOnlyWhenTheExactDraftVersionIsApproved()
    {
        var f = await Setup(); var root = ReviewRoot(f.Person);
        await Expect(await f.Manager.PostAsJsonAsync(root + "/rules", Rule(f.Owner, "RiskApproved")), HttpStatusCode.Created);
        var clinical = $"/api/phase1/service-users/{f.Person}/clinical-governance";
        var request = new RiskDraftRequest("Falls", "Unsteady when transferring", 3, 3, "Agreed supervised transfers", "Stop and escalate", "Care manager", DateTimeOffset.UtcNow.AddDays(30), null);
        var created = await f.Manager.PostAsJsonAsync(clinical + "/risks", request); await Expect(created, HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await Expect(await f.Manager.PostAsJsonAsync($"{clinical}/risks/{id}/submit", new { }), HttpStatusCode.NoContent);
        Assert.Empty((await f.Manager.GetFromJsonAsync<JsonElement>(root)).GetProperty("items").EnumerateArray());
        await Expect(await f.Manager.PostAsJsonAsync($"{clinical}/risks/{id}/approve", new { signerName = "Regression manager", declaration = "Reviewed exact risk version" }), HttpStatusCode.NoContent);
        var item = Assert.Single((await f.Manager.GetFromJsonAsync<JsonElement>(root)).GetProperty("items").EnumerateArray());
        Assert.Equal(id, item.GetProperty("source_id").GetGuid()); Assert.Equal(1, item.GetProperty("source_version").GetInt32());
        await Expect(await f.Manager.PostAsJsonAsync($"{clinical}/risks/{id}/approve", new { signerName = "Regression manager", declaration = "Retry approval" }), HttpStatusCode.Conflict);
        Assert.Single((await f.Manager.GetFromJsonAsync<JsonElement>(root)).GetProperty("items").EnumerateArray());
    }
}
