using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed class AccountInvitationRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task AdministratorCanRevokeOnlyAnInvitationInTheirTenantAndRevokedTokenCannotActivate()
    {
        await factory.EnsureClinicalSeedAsync();
        var invitationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var rawToken = Guid.NewGuid().ToString("N");
        var outsideOrganizationId = Guid.NewGuid();
        var outsideInvitationId = Guid.NewGuid();
        var outsideUserId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            var adminId = await db.AppUsers.Where(user => user.UserName == "admin").Select(user => user.Id).SingleAsync();
            db.Organizations.Add(new Organization(outsideOrganizationId, "Invitation boundary organisation", "Trial", "Active"));
            db.AppUsers.Add(new AppUser(userId, $"invite.{userId:N}", $"invite.{userId:N}@example.test", PasswordHasher.HashPassword("Unused123!Password"), UserRole.CareCoordinator, false, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.AppUsers.Add(new AppUser(outsideUserId, $"outside.{outsideUserId:N}", $"outside.{outsideUserId:N}@example.test", PasswordHasher.HashPassword("Unused123!Password"), UserRole.CareCoordinator, false, outsideOrganizationId));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""insert into account_invitations(id,user_id,token_hash,email,role,status,expires_at,created_by_user_id,created_by,organization_id,branch_id) values ({invitationId},{userId},{Hash(rawToken)},{$"invite.{userId:N}@example.test"},'CareCoordinator','Sent',{DateTimeOffset.UtcNow.AddHours(1)},{adminId},'admin',{TenantDefaults.OrganizationId},{TenantDefaults.BranchId})""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""insert into account_invitations(id,user_id,token_hash,email,role,status,expires_at,created_by_user_id,created_by,organization_id,branch_id) values ({outsideInvitationId},{outsideUserId},{Hash(Guid.NewGuid().ToString("N"))},{$"outside.{outsideUserId:N}@example.test"},'CareCoordinator','Sent',{DateTimeOffset.UtcNow.AddHours(1)},{adminId},'admin',{outsideOrganizationId},null)""");
        }

        using var admin = factory.CreateClient();
        var login = await admin.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<Login>())!.Token);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"/api/phase1/account-invitations/{outsideInvitationId}/revoke", new { reason = "Cross-tenant revocation attempt" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/phase1/account-invitations/{invitationId}/revoke", new { reason = "Invitation no longer required" })).StatusCode);

        using var anonymous = factory.CreateClient();
        var validation = await anonymous.PostAsJsonAsync("/api/phase1/account-invitations/validate", new { token = rawToken });
        validation.EnsureSuccessStatusCode();
        var validationBody = (await validation.Content.ReadFromJsonAsync<Validation>())!;
        Assert.False(validationBody.Valid);
        Assert.Equal("Revoked", validationBody.Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsJsonAsync("/api/phase1/account-invitations/accept", new { token = rawToken, password = "StrongPassword1!", acceptTerms = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/phase1/account-invitations/{invitationId}/revoke", new { reason = "Second revocation must be rejected" })).StatusCode);

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.False(await verifyDb.AppUsers.Where(user => user.Id == userId).Select(user => user.IsActive).SingleAsync());
        Assert.True(await verifyDb.AuditEvents.AnyAsync(item => item.OrganizationId == TenantDefaults.OrganizationId && item.EntityId == userId && item.Action == "account.invitation_revoked"));
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private sealed record Login(string Token);
    private sealed record Validation(bool Valid, string Status);
}
