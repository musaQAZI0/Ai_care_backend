using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed class TenantSignupRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task TenantSignupCreatesIsolatedOrganizationBranchAndFirstAdmin()
    {
        await factory.EnsureClinicalSeedAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var organizationName = $"Regression Care Provider {suffix}";
        var email = $"tenant.admin.{suffix}@aicare.local";
        var password = "TenantAdmin123!";
        var client = factory.CreateClient();

        var signup = await client.PostAsJsonAsync("/api/auth/signup-tenant", new
        {
            organizationName,
            branchName = "North Branch",
            adminName = "Tenant Admin",
            adminEmail = email,
            password,
            region = "North"
        });

        Assert.Equal(HttpStatusCode.Created, signup.StatusCode);
        var created = (await signup.Content.ReadFromJsonAsync<TenantSignupCreated>())!;
        Assert.NotEqual(Guid.Empty, created.OrganizationId);
        Assert.NotEqual(Guid.Empty, created.BranchId);
        Assert.Equal("Setup", created.Status);
        Assert.Equal("Trial", created.Plan);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = email, password, mfaCode = (string?)null });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var loginPayload = (await login.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginPayload.Token);
        var me = await client.GetFromJsonAsync<AuthMeResponse>("/api/auth/me");
        Assert.Equal(created.OrganizationId, me!.OrganizationId);
        Assert.Equal(created.BranchId, me.BranchId);
        Assert.Equal("Administrator", me.Role);
        Assert.Equal("Setup", me.OrganizationStatus);

        var onboarding = await client.GetFromJsonAsync<TenantOnboardingStatus>("/api/phase1/tenant/onboarding");
        Assert.Equal(created.OrganizationId, onboarding!.Organization.Id);
        Assert.Contains(onboarding.Steps, step => step.Key == "people" && !step.Complete);

        var blockedActivation = await client.PostAsync("/api/phase1/tenant/activate", null);
        Assert.Equal(HttpStatusCode.Conflict, blockedActivation.StatusCode);

        using (var setupScope = factory.Services.CreateScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<CareDbContext>();
            var workerId = Guid.NewGuid();
            var personId = Guid.NewGuid();
            setupDb.CareWorkers.Add(new AiCare.Domain.CareWorker(workerId, "Tenant Worker", "Personal care", "Flexible", 0, 0, "Valid", "Compliant", "10 miles", created.OrganizationId, created.BranchId));
            setupDb.AppUsers.Add(new AppUser(Guid.NewGuid(), $"tenant.worker.{suffix}", $"tenant.worker.{suffix}@aicare.local", PasswordHasher.HashPassword("WorkerPassword123!"), AiCare.Domain.UserRole.CareWorker, true, created.OrganizationId, created.BranchId, workerId, null));
            setupDb.ServiceUsers.Add(new AiCare.Domain.ServiceUser(personId, "Tenant Person", new DateOnly(1970, 1, 1), "+10000009999", "Personal care", "Contact", "Worker", AiCare.Domain.RiskLevel.Low, "Active", "Address", "None", "None", "Private", "Not specified", "", "Independent", "None", "None", "None", "None", created.OrganizationId, created.BranchId));
            setupDb.CarePlans.Add(new AiCare.Domain.CarePlan(Guid.NewGuid(), personId, "v1", "Draft", "Personal care", "Medication", "Mobility", "Nutrition", DateTimeOffset.UtcNow.AddDays(30), created.OrganizationId, created.BranchId));
            await setupDb.SaveChangesAsync();
        }

        var activated = await client.PostAsync("/api/phase1/tenant/activate", null);
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        var activeMe = await client.GetFromJsonAsync<AuthMeResponse>("/api/auth/me");
        Assert.Equal("Active", activeMe!.OrganizationStatus);

        var duplicate = await client.PostAsJsonAsync("/api/auth/signup-tenant", new
        {
            organizationName,
            branchName = "North Branch",
            adminName = "Tenant Admin",
            adminEmail = email,
            password,
            region = "North"
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.True(await db.Organizations.AnyAsync(x => x.Id == created.OrganizationId && x.Name == organizationName && x.Status == "Active"));
        Assert.True(await db.Branches.AnyAsync(x => x.Id == created.BranchId && x.OrganizationId == created.OrganizationId && x.Status == "Active"));
        Assert.True(await db.AppUsers.AnyAsync(x => x.Id == created.AdminUserId && x.OrganizationId == created.OrganizationId && x.BranchId == created.BranchId && x.Role == AiCare.Domain.UserRole.Administrator));
        Assert.True(await db.AuditEvents.AnyAsync(x => x.Action == "tenant.signup_created" && x.OrganizationId == created.OrganizationId));
        Assert.True(await db.AuditEvents.AnyAsync(x => x.Action == "tenant.activated" && x.OrganizationId == created.OrganizationId));
        Assert.True(await db.Organizations.AnyAsync(x => x.Id == created.OrganizationId && x.Status == "Active"));
    }

    private sealed record TenantSignupCreated(Guid OrganizationId, Guid BranchId, Guid AdminUserId, string Status, string Plan);
    private sealed record LoginResponse(string Token);
    private sealed record AuthMeResponse(Guid OrganizationId, Guid? BranchId, string Role, string OrganizationStatus);
    private sealed record TenantOnboardingStatus(TenantOrganization Organization, List<TenantOnboardingStep> Steps);
    private sealed record TenantOrganization(Guid Id, string Name, string Plan, string Status);
    private sealed record TenantOnboardingStep(string Key, string Title, bool Complete, string ActionPath);
}
