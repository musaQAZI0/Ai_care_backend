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
