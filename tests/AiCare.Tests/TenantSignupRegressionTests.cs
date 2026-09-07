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

        var onboarding = await client.GetFromJsonAsync<TenantOnboardingStatus>("/api/phase1/tenant/onboarding");
        Assert.Equal(created.OrganizationId, onboarding!.Organization.Id);
        Assert.Contains(onboarding.Steps, step => step.Key == "people" && !step.Complete);

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
        Assert.True(await db.Organizations.AnyAsync(x => x.Id == created.OrganizationId && x.Name == organizationName && x.Status == "Setup"));
        Assert.True(await db.Branches.AnyAsync(x => x.Id == created.BranchId && x.OrganizationId == created.OrganizationId && x.Status == "Setup"));
        Assert.True(await db.AppUsers.AnyAsync(x => x.Id == created.AdminUserId && x.OrganizationId == created.OrganizationId && x.BranchId == created.BranchId && x.Role == AiCare.Domain.UserRole.Administrator));
        Assert.True(await db.AuditEvents.AnyAsync(x => x.Action == "tenant.signup_created" && x.OrganizationId == created.OrganizationId));
    }

    private sealed record TenantSignupCreated(Guid OrganizationId, Guid BranchId, Guid AdminUserId, string Status, string Plan);
    private sealed record LoginResponse(string Token);
    private sealed record AuthMeResponse(Guid OrganizationId, Guid? BranchId, string Role);
    private sealed record TenantOnboardingStatus(TenantOrganization Organization, List<TenantOnboardingStep> Steps);
    private sealed record TenantOrganization(Guid Id, string Name, string Plan, string Status);
    private sealed record TenantOnboardingStep(string Key, string Title, bool Complete, string ActionPath);
}
