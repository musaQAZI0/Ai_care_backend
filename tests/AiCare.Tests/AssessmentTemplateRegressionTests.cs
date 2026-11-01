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
public sealed class AssessmentTemplateRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task PublishedVersionsValidateAnswersAndPreserveHistoricalQuestionsAndSignatures()
    {
        var f = await Setup();
        var request = Template();
        var created = await f.Author.PostAsJsonAsync(Templates, request);
        await Expect(created, HttpStatusCode.Created);
        await Expect(await f.Author.PostAsJsonAsync(Templates, request), HttpStatusCode.OK);
        await Expect(await f.Author.PostAsJsonAsync(Templates, request with { Title = "Changed replay" }), HttpStatusCode.Conflict);
        var root = $"/api/phase1/service-users/{f.Person}/clinical-governance";
        var assessment = Assessment(request, "{\"help\":true,\"details\":\"Two-person transfer\"}");
        await Expect(await f.Author.PostAsJsonAsync(root + "/assessments", assessment), HttpStatusCode.BadRequest);
        await Expect(await f.Author.PostAsync($"{Templates}/{request.RequestId}/submit", null), HttpStatusCode.OK);
        await Expect(await f.Author.PostAsJsonAsync($"{Templates}/{request.RequestId}/approve", new { reason = "Self approval" }), HttpStatusCode.Forbidden);
        await Expect(await f.Manager.PostAsJsonAsync($"{Templates}/{request.RequestId}/approve", new { reason = "Reviewed question definitions and conditions" }), HttpStatusCode.OK);
        await Expect(await f.Author.PostAsJsonAsync(root + "/assessments", assessment with { AnswersJson = "{\"help\":true}" }), HttpStatusCode.BadRequest);
        await Expect(await f.Author.PostAsJsonAsync(root + "/assessments", assessment with { TemplateVersionId = null }), HttpStatusCode.BadRequest);
        var saved = await f.Author.PostAsJsonAsync(root + "/assessments", assessment); await Expect(saved, HttpStatusCode.Created);
        var id = (await saved.Content.ReadFromJsonAsync<Created>())!.Id;
        await Expect(await f.Author.PostAsync($"{root}/assessments/{id}/submit", null), HttpStatusCode.NoContent);
        await Expect(await f.Manager.PostAsJsonAsync($"{root}/assessments/{id}/approve", new { signerName = "Manager", declaration = "Reviewed this exact assessment" }), HttpStatusCode.NoContent);

        var second = request with { RequestId = Guid.NewGuid(), SourceVersionId = request.RequestId,
            ChangeReason = "Updated label", ImpactReview = "Old responses keep their original label", Fields = [request.Fields[0] with { Label = "Needs assistance now" }, request.Fields[1]] };
        await Expect(await f.Author.PostAsJsonAsync(Templates, second), HttpStatusCode.Created);
        await Expect(await f.Author.PostAsync($"{Templates}/{second.RequestId}/submit", null), HttpStatusCode.OK);
        await Expect(await f.Manager.PostAsJsonAsync($"{Templates}/{second.RequestId}/approve", new { reason = "Reviewed impact on previous assessments" }), HttpStatusCode.OK);
        var history = await f.Manager.GetFromJsonAsync<JsonElement>(root);
        var original = Assert.Single(history.GetProperty("assessments").EnumerateArray());
        Assert.Equal(request.RequestId, original.GetProperty("templateVersionId").GetGuid());
        Assert.Equal("Needs help", original.GetProperty("fields")[0].GetProperty("label").GetString());
        Assert.Equal("Two-person transfer", original.GetProperty("answers").GetProperty("details").GetString());
        Assert.Equal("Retired", (await f.Manager.GetFromJsonAsync<JsonElement>($"{Templates}/{request.RequestId}")).GetProperty("status").GetString());
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("update assessment_template_versions set title='Rewritten' where id={0}", request.RequestId));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("update governed_assessments set answers_json='{}'::jsonb where id={0}", id));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("update governed_assessments set status='Draft' where id={0}", id));
        Assert.True(await db.AuditEvents.AnyAsync(a => a.EntityId == request.RequestId && a.Action == "assessment_template.published"));
    }

    [Fact]
    public async Task TemplateLibraryAndCaptureDenyForeignTenantsBranchesAndUnapprovedUsers()
    {
        var f = await Setup(); var foreign = await Setup(); var request = Template();
        await Expect(await f.Author.PostAsJsonAsync(Templates, request), HttpStatusCode.Created);
        Assert.Empty((await foreign.Author.GetFromJsonAsync<JsonElement>(Templates)).EnumerateArray());
        await Expect(await foreign.Author.GetAsync($"{Templates}/{request.RequestId}"), HttpStatusCode.NotFound);
        await Expect(await f.OtherBranch.GetAsync($"{Templates}/{request.RequestId}"), HttpStatusCode.NotFound);
        await Expect(await f.Worker.GetAsync(Templates), HttpStatusCode.Forbidden);
        await Expect(await f.Author.PostAsJsonAsync(Templates, request with { RequestId = Guid.NewGuid(), OrganizationWide = true }), HttpStatusCode.Forbidden);
        await Expect(await foreign.Author.PostAsJsonAsync(Templates, request with { RequestId = Guid.NewGuid(), SourceVersionId = request.RequestId }), HttpStatusCode.NotFound);
        await Expect(await f.Author.PostAsync($"{Templates}/{request.RequestId}/submit", null), HttpStatusCode.OK);
        await Expect(await f.OtherBranch.PostAsJsonAsync($"{Templates}/{request.RequestId}/approve", new { reason = "Wrong branch" }), HttpStatusCode.NotFound);
        await Expect(await f.Manager.PostAsJsonAsync($"{Templates}/{request.RequestId}/approve", new { reason = "Approved for this branch" }), HttpStatusCode.OK);
        await Expect(await foreign.Author.PostAsJsonAsync($"/api/phase1/service-users/{foreign.Person}/clinical-governance/assessments", Assessment(request, "{\"help\":false}")), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ConcurrentDraftVersionsAreDistinctAndAuthorCannotApproveTheirOwnTemplate()
    {
        var f = await Setup(); var request = Template();
        var authorManagerRequest = request with { RequestId = Guid.NewGuid() };
        await Expect(await f.Manager.PostAsJsonAsync(Templates, authorManagerRequest), HttpStatusCode.Created);
        await Expect(await f.Manager.PostAsync($"{Templates}/{authorManagerRequest.RequestId}/submit", null), HttpStatusCode.OK);
        await Expect(await f.Manager.PostAsJsonAsync($"{Templates}/{authorManagerRequest.RequestId}/approve", new { reason = "Self approval" }), HttpStatusCode.Conflict);
        var versions = await Task.WhenAll(f.Author.PostAsJsonAsync(Templates, request), f.Author.PostAsJsonAsync(Templates, request with { RequestId = Guid.NewGuid() }));
        foreach (var result in versions) await Expect(result, HttpStatusCode.Created);
        var a = (await versions[0].Content.ReadFromJsonAsync<Created>())!;
        var b = (await versions[1].Content.ReadFromJsonAsync<Created>())!;
        Assert.NotEqual(a.Version, b.Version);
    }

    private const string Templates = "/api/phase1/assessment-templates";
    private static AssessmentTemplateRequest Template() => new(Guid.NewGuid(), "mobility", "Mobility assessment", "Mobility",
        [new("help", "Needs help", "YesNo", true), new("details", "Support details", "Text", true, VisibleWhen: new("help", "true"))],
        "Initial version", "Staff-owned assessment; no calculated clinical advice");
    private static AssessmentDraftRequest Assessment(AssessmentTemplateRequest t, string answers) => new(t.AssessmentType, t.Key, "1", answers,
        0, "Low", "Mobility reviewed", "Review care plan", "Coordinator", "Care coordinator", DateTimeOffset.UtcNow.AddMonths(1), null, t.RequestId);
    private async Task<Fixture> Setup()
    {
        await factory.EnsureClinicalSeedAsync(); var org = Guid.NewGuid(); var branch = Guid.NewGuid(); var other = Guid.NewGuid(); var person = Guid.NewGuid(); var prefix = $"template.{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Organizations.Add(new Organization(org, "Template test", "Trial", "Active"));
            db.Branches.AddRange(new Branch(branch, org, "Main", "Test", "Active"), new Branch(other, org, "Other", "Test", "Active"));
            db.ServiceUsers.Add(new ServiceUser(person, "Assessment person", new DateOnly(1950, 1, 1), "1", "Care", "Contact", "", RiskLevel.Low, "Active", "Address", "None", "None", "Private", "", "", "Independent", "Independent", "Verbal", "Routine", "Standard", org, branch));
            foreach (var actor in new[] { ("author", UserRole.CareCoordinator, branch), ("manager", UserRole.CareManager, branch), ("other", UserRole.CareManager, other), ("worker", UserRole.CareWorker, branch) })
                db.AppUsers.Add(new AppUser(Guid.NewGuid(), prefix + actor.Item1, prefix + actor.Item1 + "@test.invalid", PasswordHasher.HashPassword("Admin123!"), actor.Item2, true, org, actor.Item3));
            await db.SaveChangesAsync();
        }
        return new(await Client(prefix + "author"), await Client(prefix + "manager"), await Client(prefix + "other"), await Client(prefix + "worker"), person);
    }
    private async Task<HttpClient> Client(string name)
    {
        var client = factory.CreateClient(); var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = name, password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<Login>())!.Token); return client;
    }
    private static async Task Expect(HttpResponseMessage result, HttpStatusCode status) => Assert.True(result.StatusCode == status, $"Expected {status}, received {result.StatusCode}: {await result.Content.ReadAsStringAsync()}");
    private sealed record Fixture(HttpClient Author, HttpClient Manager, HttpClient OtherBranch, HttpClient Worker, Guid Person);
    private sealed record Created(Guid Id, int Version); private sealed record Login(string Token);
}
