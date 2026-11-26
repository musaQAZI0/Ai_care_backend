using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AiCare.Application.Accounts;
using AiCare.Domain;
using AiCare.Infrastructure;
using AiCare.Infrastructure.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed class TenantAccountEmailIsolationTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task SharedEmailInvitationCreatesAnIndependentAccountAndResetNeverChoosesAnotherTenant()
    {
        await factory.EnsureClinicalSeedAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var email = $"shared.{suffix}@example.test";
        var originalId = Guid.NewGuid();
        var outsideOrg = Guid.NewGuid();
        var outsideBranch = Guid.NewGuid();
        var originalUsername = $"original.{suffix}";
        var invitedUsername = $"invited.{suffix}";
        const string originalPassword = "OriginalAccount123!";
        const string invitedPassword = "InvitedAccount123!";
        var rawToken = Guid.NewGuid().ToString("N");
        Guid invitedId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Organizations.Add(new Organization(outsideOrg, $"Original provider {suffix}", "Trial", "Active"));
            db.Branches.Add(new Branch(outsideBranch, outsideOrg, "Original branch", "North", "Active"));
            db.AppUsers.Add(new AppUser(originalId, originalUsername, email, PasswordHasher.HashPassword(originalPassword), UserRole.CareManager, true, outsideOrg, outsideBranch));
            await db.SaveChangesAsync();
            var actor = await db.AppUsers.SingleAsync(user => user.UserName == "admin");
            var store = new AccountInvitationStore(db);
            var invitation = await store.CreateAsync(TenantDefaults.OrganizationId, TenantDefaults.BranchId, actor.Id, actor.UserName,
                new CreateAccountInvitationCommand(invitedUsername, email, UserRole.CareCoordinator, TenantDefaults.BranchId, null), Hash(rawToken), DateTimeOffset.UtcNow.AddHours(1), default);
            invitedId = invitation.UserId;
            Assert.NotEqual(originalId, invitedId);
            Assert.False((await db.AppUsers.SingleAsync(user => user.Id == invitedId)).IsActive);
            var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(TenantDefaults.OrganizationId, TenantDefaults.BranchId, actor.Id, actor.UserName,
                new CreateAccountInvitationCommand($"duplicate.{suffix}", email.ToUpperInvariant(), UserRole.CareManager, TenantDefaults.BranchId, null), Hash(Guid.NewGuid().ToString()), DateTimeOffset.UtcNow.AddHours(1), default));
            Assert.Contains("in this organization", duplicate.Message);
            var usernameCollision = await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(TenantDefaults.OrganizationId, TenantDefaults.BranchId, actor.Id, actor.UserName,
                new CreateAccountInvitationCommand(originalUsername, $"different.{suffix}@example.test", UserRole.CareManager, TenantDefaults.BranchId, null), Hash(Guid.NewGuid().ToString()), DateTimeOffset.UtcNow.AddHours(1), default));
            Assert.Contains("username is unavailable", usernameCollision.Message);
        }

        using var client = factory.CreateClient();
        var accepted = await client.PostAsJsonAsync("/api/phase1/account-invitations/accept", new { token = rawToken, password = invitedPassword, acceptTerms = true });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(outsideOrg, await LoginOrganization(originalUsername, originalPassword));
        Assert.Equal(TenantDefaults.OrganizationId, await LoginOrganization(invitedUsername, invitedPassword));

        var ambiguous = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email });
        Assert.Equal(HttpStatusCode.Accepted, ambiguous.StatusCode);
        var ambiguousBody = (await ambiguous.Content.ReadFromJsonAsync<ResetReply>())!;
        Assert.Null(ambiguousBody.DevelopmentResetToken);
        var selected = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email, userName = invitedUsername });
        Assert.Equal(HttpStatusCode.Accepted, selected.StatusCode);
        var selectedBody = (await selected.Content.ReadFromJsonAsync<ResetReply>())!;
        Assert.Equal(ambiguousBody.Message, selectedBody.Message);
        Assert.False(string.IsNullOrWhiteSpace(selectedBody.DevelopmentResetToken));
        var wrongIdentity = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = $"wrong.{suffix}@example.test", userName = originalUsername });
        Assert.Null((await wrongIdentity.Content.ReadFromJsonAsync<ResetReply>())!.DevelopmentResetToken);
        const string replacementPassword = "ReplacementAccount123!";
        var reset = await client.PostAsJsonAsync("/api/auth/reset-password", new { resetToken = selectedBody.DevelopmentResetToken, newPassword = replacementPassword });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(TenantDefaults.OrganizationId, await LoginOrganization(invitedUsername, replacementPassword));
        Assert.Equal(outsideOrg, await LoginOrganization(originalUsername, originalPassword));

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        var original = await verifyDb.AppUsers.SingleAsync(user => user.Id == originalId);
        Assert.Equal(outsideOrg, original.OrganizationId);
        Assert.Equal(UserRole.CareManager, original.Role);
        Assert.True(PasswordHasher.VerifyPassword(originalPassword, original.PasswordHash));
        Assert.Equal(TenantDefaults.OrganizationId, (await verifyDb.AppUsers.SingleAsync(user => user.Id == invitedId)).OrganizationId);
    }

    [Fact]
    public async Task NewTenantCanUseAnExistingRecipientEmailWithADistinctAdministratorUsername()
    {
        await factory.EnsureClinicalSeedAsync();
        using var client = factory.CreateClient();
        var suffix = Guid.NewGuid().ToString("N");
        var first = await client.PostAsJsonAsync("/api/auth/signup-tenant", new { organizationName = $"First {suffix}", branchName = "Test branch", adminName = "Test admin", adminEmail = $"shared.{suffix}@example.test", adminUserName = $"first.{suffix}", password = "StrongTenant123!" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var second = await client.PostAsJsonAsync("/api/auth/signup-tenant", new { organizationName = $"Second {suffix}", branchName = "Test branch", adminName = "Test admin", adminEmail = $"shared.{suffix}@example.test", adminUserName = $"second.{suffix}", password = "OtherTenant123!" });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var firstOrg = await LoginOrganization($"first.{suffix}", "StrongTenant123!");
        var secondOrg = await LoginOrganization($"second.{suffix}", "OtherTenant123!");
        Assert.NotEqual(firstOrg, secondOrg);
    }

    private async Task<Guid> LoginOrganization(string username, string password)
    {
        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = username, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<Login>())!.Token);
        return (await client.GetFromJsonAsync<Me>("/api/auth/me"))!.OrganizationId;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private sealed record Login(string Token);
    private sealed record Me(Guid OrganizationId);
    private sealed record ResetReply(string Message, string? DevelopmentResetToken);
}
