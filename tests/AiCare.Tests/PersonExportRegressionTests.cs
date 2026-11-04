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
public sealed class PersonExportRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Theory]
    [InlineData(UserRole.Administrator)]
    [InlineData(UserRole.CareManager)]
    public async Task AuthorizedLocalExportContainsSelectedPersonAndRecordsActor(UserRole role)
    {
        var data = await Seed(role);
        using var client = await Login(data.Actor);
        var response = await client.GetAsync(ExportPath(data.Local.Id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = (await response.Content.ReadFromJsonAsync<JsonElement>());
        Assert.Equal(data.Local.Id, payload.GetProperty("serviceUser").GetProperty("id").GetGuid());
        Assert.Equal(data.Local.FullName, payload.GetProperty("serviceUser").GetProperty("fullName").GetString());
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(data.OtherBranch.FullName, body);
        Assert.DoesNotContain(data.Foreign.FullName, body);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        var request = await db.DataGovernanceRequests.SingleAsync(item => item.Id == payload.GetProperty("requestId").GetGuid());
        Assert.Equal(data.Local.Id, request.ServiceUserId);
        Assert.Equal("SubjectAccessExport", request.RequestType);
        Assert.Equal("Completed", request.Status);
        Assert.Equal(TenantDefaults.OrganizationId, request.OrganizationId);
        Assert.True(await db.AuditEvents.AnyAsync(item => item.Action == "governance.subject_exported"
            && item.EntityId == data.Local.Id && item.Actor == data.Actor
            && item.OrganizationId == TenantDefaults.OrganizationId));
    }

    [Theory]
    [InlineData(UserRole.ServiceUser)]
    [InlineData(UserRole.FamilyMember)]
    [InlineData(UserRole.CareWorker)]
    [InlineData(UserRole.CareCoordinator)]
    [InlineData(UserRole.BackOffice)]
    public async Task ForbiddenRoleCannotExportOrCreateSuccessfulDisclosureEvidence(UserRole role)
    {
        var data = await Seed(role);
        using var client = await Login(data.Actor);
        var response = await client.GetAsync(ExportPath(data.Local.Id));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain(data.Local.FullName, await response.Content.ReadAsStringAsync());
        await AssertNoExport(data.Local.Id);
    }

    [Theory]
    [InlineData(UserRole.CareManager)]
    [InlineData(UserRole.Administrator)]
    public async Task ExportHonorsBranchAndTenantBoundaries(UserRole role)
    {
        var data = await Seed(role);
        using var client = await Login(data.Actor);
        var foreign = await client.GetAsync(ExportPath(data.Foreign.Id));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.DoesNotContain(data.Foreign.FullName, await foreign.Content.ReadAsStringAsync());
        await AssertNoExport(data.Foreign.Id);

        var otherBranch = await client.GetAsync(ExportPath(data.OtherBranch.Id));
        if (role == UserRole.Administrator)
        {
            Assert.Equal(HttpStatusCode.OK, otherBranch.StatusCode);
            Assert.Contains(data.OtherBranch.FullName, await otherBranch.Content.ReadAsStringAsync());
        }
        else
        {
            Assert.Equal(HttpStatusCode.NotFound, otherBranch.StatusCode);
            Assert.DoesNotContain(data.OtherBranch.FullName, await otherBranch.Content.ReadAsStringAsync());
            await AssertNoExport(data.OtherBranch.Id);
        }
    }

    [Theory]
    [InlineData(UserRole.Administrator, HttpStatusCode.OK)]
    [InlineData(UserRole.CareManager, HttpStatusCode.OK)]
    [InlineData(UserRole.CareCoordinator, HttpStatusCode.OK)]
    [InlineData(UserRole.BackOffice, HttpStatusCode.Forbidden)]
    [InlineData(UserRole.CareWorker, HttpStatusCode.Forbidden)]
    [InlineData(UserRole.FamilyMember, HttpStatusCode.Forbidden)]
    [InlineData(UserRole.ServiceUser, HttpStatusCode.Forbidden)]
    public async Task PersonHistoryHasIndependentRoleBranchAndTenantBoundaries(UserRole role, HttpStatusCode expected)
    {
        var data = await Seed(role);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            foreach (var person in new[] { data.Local, data.OtherBranch, data.Foreign })
                db.PersonRecordRevisions.Add(new PersonRecordRevision(Guid.NewGuid(), person.Id, Guid.NewGuid(), "History author",
                    DateTimeOffset.UtcNow, null, "{\"PreferredName\":\"Sensitive fictional history\"}", person.OrganizationId!.Value, person.BranchId));
            await db.SaveChangesAsync();
        }
        using var client = await Login(data.Actor);
        var local = await client.GetAsync(HistoryPath(data.Local.Id));
        Assert.Equal(expected, local.StatusCode);
        var localBody = await local.Content.ReadAsStringAsync();
        if (expected == HttpStatusCode.OK) Assert.Contains("Sensitive fictional history", localBody);
        else Assert.DoesNotContain("Sensitive fictional history", localBody);

        var foreign = await client.GetAsync(HistoryPath(data.Foreign.Id));
        Assert.Equal(expected == HttpStatusCode.OK ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.DoesNotContain("Sensitive fictional history", await foreign.Content.ReadAsStringAsync());
        var other = await client.GetAsync(HistoryPath(data.OtherBranch.Id));
        Assert.Equal(role == UserRole.Administrator ? HttpStatusCode.OK
            : expected == HttpStatusCode.OK ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden, other.StatusCode);
        if (role != UserRole.Administrator) Assert.DoesNotContain("Sensitive fictional history", await other.Content.ReadAsStringAsync());

        if (role is not (UserRole.Administrator or UserRole.CareManager))
        {
            var export = await client.GetAsync(ExportPath(data.Local.Id));
            Assert.Equal(HttpStatusCode.Forbidden, export.StatusCode);
            Assert.DoesNotContain("Sensitive fictional history", await export.Content.ReadAsStringAsync());
        }

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.False(await verifyDb.AuditEvents.AnyAsync(item => item.Action == "person_record.history_viewed" && item.EntityId == data.Foreign.Id));
        if (expected != HttpStatusCode.OK)
            Assert.False(await verifyDb.AuditEvents.AnyAsync(item => item.Action == "person_record.history_viewed" && item.EntityId == data.Local.Id));
    }

    [Fact]
    public async Task GovernedAnonymizationClearsOnlySelectedPersonsHistoricalSnapshots()
    {
        var data = await Seed(UserRole.Administrator);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            var local = await db.ServiceUsers.SingleAsync(item => item.Id == data.Local.Id);
            db.Entry(local).CurrentValues.SetValues(local with { Status = "Discharged" });
            foreach (var person in new[] { data.Local, data.Foreign })
                db.PersonRecordRevisions.Add(new PersonRecordRevision(Guid.NewGuid(), person.Id, Guid.NewGuid(), data.Actor,
                    DateTimeOffset.UtcNow, "{\"PreferredName\":\"Old sensitive name\"}", "{\"PreferredName\":\"New sensitive name\"}",
                    person.OrganizationId!.Value, person.BranchId));
            await db.SaveChangesAsync();
        }
        using var client = await Login(data.Actor);
        await StepUpTestGrants.GrantAsync(factory, client, "privacy");
        var response = await client.PostAsJsonAsync($"/api/phase1/data-governance/service-users/{data.Local.Id}/anonymize",
            new { confirmation = "ANONYMIZE", reason = "Synthetic retention disposal regression" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        var cleared = await verifyDb.PersonRecordRevisions.SingleAsync(item => item.ServiceUserId == data.Local.Id);
        Assert.Null(cleared.BeforeJson);
        using var clearedJson = JsonDocument.Parse(cleared.AfterJson);
        Assert.Empty(clearedJson.RootElement.EnumerateObject());
        var foreign = await verifyDb.PersonRecordRevisions.SingleAsync(item => item.ServiceUserId == data.Foreign.Id);
        Assert.Contains("Old sensitive name", foreign.BeforeJson!);
        Assert.Contains("New sensitive name", foreign.AfterJson);
        var history = await client.GetStringAsync(HistoryPath(data.Local.Id));
        Assert.DoesNotContain("sensitive name", history);
    }

    private static string HistoryPath(Guid id) => $"/api/phase1/service-users/{id}/person-record/history";

    private async Task AssertNoExport(Guid personId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.False(await db.DataGovernanceRequests.AnyAsync(item => item.ServiceUserId == personId && item.RequestType == "SubjectAccessExport"));
        Assert.False(await db.AuditEvents.AnyAsync(item => item.EntityId == personId && item.Action == "governance.subject_exported"));
    }

    private async Task<Scenario> Seed(UserRole role)
    {
        await factory.EnsureClinicalSeedAsync();
        var foreignOrg = Guid.NewGuid();
        var foreignBranch = Guid.NewGuid();
        var otherBranch = Guid.NewGuid();
        var actor = $"person.export.{Guid.NewGuid():N}";
        var local = Person(TenantDefaults.OrganizationId, TenantDefaults.BranchId);
        var other = Person(TenantDefaults.OrganizationId, otherBranch);
        var foreign = Person(foreignOrg, foreignBranch);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        db.Organizations.Add(new Organization(foreignOrg, "Foreign export organisation", "Pilot", "Active"));
        db.Branches.AddRange(new Branch(otherBranch, TenantDefaults.OrganizationId, "Other export branch", "North", "Active"),
            new Branch(foreignBranch, foreignOrg, "Foreign export branch", "North", "Active"));
        db.ServiceUsers.AddRange(local, other, foreign);
        db.AppUsers.Add(new AppUser(Guid.NewGuid(), actor, $"{actor}@example.test", PasswordHasher.HashPassword("Admin123!"),
            role, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId, null, null));
        await db.SaveChangesAsync();
        return new Scenario(actor, local, other, foreign);
    }

    private async Task<HttpClient> Login(string actor)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { userName = actor, password = "Admin123!", mfaCode = (string?)null });
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<LoginResponse>())!.Token);
        return client;
    }

    private static string ExportPath(Guid id) => $"/api/phase1/data-governance/service-users/{id}/export";
    private static ServiceUser Person(Guid organization, Guid branch) => new(Guid.NewGuid(), $"Fictional export person {Guid.NewGuid():N}",
        new DateOnly(1945, 1, 1), "+441234567890", "Personal care", "Fictional contact", "", RiskLevel.Low, "Active",
        "Fictional address", "None", "None", "Private", "", "", "Independent", "Independent", "Verbal", "Routine", "Standard", organization, branch);
    private sealed record Scenario(string Actor, ServiceUser Local, ServiceUser OtherBranch, ServiceUser Foreign);
    private sealed record LoginResponse(string Token);
}
