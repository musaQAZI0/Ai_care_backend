using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed class AuthorizationBoundaryRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task BranchAManagerCannotReadOrUpdateBranchBVisit()
    {
        var data = await SeedBranchScenario(UserRole.CareManager);
        var client = await Login(data.UserName);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/phase1/visit-operations/visits/{data.VisitId}")).StatusCode);
        var update = await client.PostAsJsonAsync($"/api/phase1/visit-operations/visits/{data.VisitId}/exceptions", new { category = "Boundary", severity = "Low", description = "Boundary" });
        Assert.False((int)update.StatusCode is >= 200 and < 300);
    }

    [Fact]
    public async Task BranchACoordinatorCannotManageBranchBAbsence()
    {
        var data = await SeedBranchScenario(UserRole.CareCoordinator);
        var client = await Login(data.UserName);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/phase1/scheduling/care-workers/{data.WorkerId}/absences")).StatusCode);
        var response = await client.PostAsJsonAsync($"/api/phase1/scheduling/care-workers/{data.WorkerId}/absences", new { absenceType = "Leave", startsAt = DateTimeOffset.UtcNow.AddDays(1), endsAt = DateTimeOffset.UtcNow.AddDays(2), status = "Approved", notes = "Boundary" });
        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CareWorkerCannotAcknowledgeAnotherWorkersHandover()
    {
        var data = await SeedWorkerScenario();
        var owner = await Login(data.OwnerName);
        var other = await Login(data.OtherName);
        var handoverId = Guid.NewGuid(); var summary = "Boundary handover"; var actions = "Follow up";
        using (var scope = factory.Services.CreateScope()) { var db0 = scope.ServiceProvider.GetRequiredService<CareDbContext>(); var visit = await db0.Visits.SingleAsync(x => x.Id == data.VisitId); db0.Database.ExecuteSqlInterpolated($"insert into visit_handovers(id,visit_id,service_user_id,organization_id,branch_id,summary,outstanding_actions,urgent,attachment_reference,created_by) values ({handoverId},{visit.Id},{visit.ServiceUserId},{visit.OrganizationId},{visit.BranchId},{summary},{actions},{true},{string.Empty},{data.WorkerId})"); }
        var response = await other.PostAsync($"/api/phase1/visit-operations/handovers/{handoverId}/acknowledge", null);
        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task BackOfficeCrossOrganizationScopeIsExplicitlyDeniedByDefault()
    {
        var data = await SeedBranchScenario(UserRole.BackOffice, separateOrganization: true);
        var client = await Login(data.UserName);
        var response = await client.GetAsync($"/api/phase1/service-users/{RegressionIds.ServiceUserId}/complete-record");
        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MissingOrganizationClaimCannotReadCareData()
    {
        await factory.EnsureClinicalSeedAsync();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(UserRole.CareManager, null, TenantDefaults.BranchId));
        var response = await client.GetAsync($"/api/phase1/service-users/{RegressionIds.ServiceUserId}/complete-record");
        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task InvalidBranchClaimCannotReadCareData()
    {
        await factory.EnsureClinicalSeedAsync();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(UserRole.CareManager, TenantDefaults.OrganizationId, Guid.NewGuid()));
        var response = await client.GetAsync($"/api/phase1/service-users/{RegressionIds.ServiceUserId}/complete-record");
        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
    }

    private async Task<(Guid PersonId, Guid WorkerId, Guid VisitId, string UserName)> SeedBranchScenario(UserRole role, bool separateOrganization = false)
    {
        await factory.EnsureClinicalSeedAsync();
        var organization = separateOrganization ? Guid.NewGuid() : TenantDefaults.OrganizationId;
        var branch = Guid.NewGuid(); var person = Guid.NewGuid(); var worker = Guid.NewGuid(); var visit = Guid.NewGuid(); var user = $"boundary.{Guid.NewGuid():N}";
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        if (separateOrganization) db.Organizations.Add(new Organization(organization, "Boundary Org", "Trial", "Active"));
        db.Branches.Add(new Branch(branch, organization, "Boundary Branch", "Test", "Active"));
        db.ServiceUsers.Add(new ServiceUser(person, "Boundary Person", new DateOnly(1980,1,1), "+19999990001", "Care", "Contact", "", RiskLevel.Low, "Active", "Address", "None", "None", "Private", "Test", "", "Independent", "Independent", "Verbal", "None", "Standard", organization, branch));
        db.CareWorkers.Add(new CareWorker(worker, "Boundary Worker", "Care", "Available", 0, 0, "Valid", "Compliant", "10 miles", organization, branch));
        db.Visits.Add(new Visit(visit, person, worker, DateTimeOffset.UtcNow.AddHours(1), "Boundary visit", 30, "Care", VisitStatus.Scheduled, null, null, null, null, null, null, organization, branch));
        db.AppUsers.Add(new AppUser(Guid.NewGuid(), user, $"{user}@aicare.local", PasswordHasher.HashPassword("Admin123!"), role, true, organization, TenantDefaults.BranchId, worker, null));
        await db.SaveChangesAsync();
        return (person, worker, visit, user);
    }

    private async Task<(Guid PersonId, Guid WorkerId, Guid VisitId, string OwnerName, string OtherName)> SeedWorkerScenario()
    {
        var data = await SeedBranchScenario(UserRole.CareWorker);
        var other = $"boundary.other.{Guid.NewGuid():N}";
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        db.AppUsers.Add(new AppUser(Guid.NewGuid(), other, $"{other}@aicare.local", PasswordHasher.HashPassword("Admin123!"), UserRole.CareWorker, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId, Guid.NewGuid(), null));
        await db.SaveChangesAsync();
        return (data.PersonId, data.WorkerId, data.VisitId, data.UserName, other);
    }

    private async Task<HttpClient> Login(string userName)
    {
        var client = factory.CreateClient(); var response = await client.PostAsJsonAsync("/api/auth/login", new { userName, password = "Admin123!", mfaCode = (string?)null });
        response.EnsureSuccessStatusCode(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<LoginResponse>())!.Token); return client;
    }

    private static string Token(UserRole role, Guid? organization, Guid? branch)
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, role.ToString()), new(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()), new(JwtRegisteredClaimNames.UniqueName, "boundary") };
        if (organization is not null) claims.Add(new Claim("organization_id", organization.Value.ToString()));
        if (branch is not null) claims.Add(new Claim("branch_id", branch.Value.ToString()));
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("regression-signing-key-with-enough-length-for-hmac-2026"));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("AiCare", "AiCareClient", claims, expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)));
    }

    private sealed record LoginResponse(string Token);
    private sealed record CreatedId(Guid Id);
}










