using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed class MfaLifecycleRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task EnrollmentRecoveryLockoutPolicyAndAdminResetAreGoverned()
    {
        await factory.EnsureClinicalSeedAsync();
        var managerName = $"mfa.manager.{Guid.NewGuid():N}";
        var adminName = $"mfa.admin.{Guid.NewGuid():N}";
        var targetName = $"mfa.target.{Guid.NewGuid():N}";
        Guid managerId, targetId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            var managerUser = User(managerName, UserRole.CareManager); var adminUser = User(adminName, UserRole.Administrator); var target = User(targetName, UserRole.CareCoordinator);
            managerId = managerUser.Id; targetId = target.Id; db.AppUsers.AddRange(managerUser, adminUser, target); await db.SaveChangesAsync();
        }

        var managerLogin = await PasswordLogin(managerName); var manager = Auth(managerLogin);
        var enrollment = await manager.PostAsync("/api/security/mfa/enroll", null); Assert.Equal(HttpStatusCode.OK, enrollment.StatusCode);
        var secret = JsonDocument.Parse(await enrollment.Content.ReadAsStringAsync()).RootElement.GetProperty("secret").GetString()!;
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync("/api/security/mfa/enroll/verify", new { code = "000000" })).StatusCode);
        var verified = await manager.PostAsJsonAsync("/api/security/mfa/enroll/verify", new { code = Totp(secret) }); Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        var recovery = JsonDocument.Parse(await verified.Content.ReadAsStringAsync()).RootElement.GetProperty("recoveryCodes")[0].GetString()!;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            var stored = await db.Database.SqlQuery<string>($"select mfa_secret as \"Value\" from auth_user_security where user_id={managerId}").SingleAsync();
            Assert.StartsWith("v1:", stored); Assert.DoesNotContain(secret, stored);
        }
        var recoveryLogin = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { userName = managerName, password = "Admin123!", mfaCode = recovery }); Assert.Equal(HttpStatusCode.OK, recoveryLogin.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { userName = managerName, password = "Admin123!", mfaCode = recovery })).StatusCode);
        for (var attempt = 0; attempt < 4; attempt++) await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { userName = managerName, password = "Admin123!", mfaCode = "111111" });
        Assert.Equal((HttpStatusCode)423, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { userName = managerName, password = "Admin123!", mfaCode = Totp(secret) })).StatusCode);

        var adminLogin = await PasswordLogin(adminName); var admin = Auth(adminLogin); var adminEnrollment = await admin.PostAsync("/api/security/mfa/enroll", null); var adminSecret = JsonDocument.Parse(await adminEnrollment.Content.ReadAsStringAsync()).RootElement.GetProperty("secret").GetString()!; Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/security/mfa/enroll/verify", new { code = Totp(adminSecret) })).StatusCode);
        var targetLogin = await PasswordLogin(targetName);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/security/mfa/policy", new { requiredRoles = new[] { "CareCoordinator" }, gracePeriodDays = 0, enabled = true, effectiveAt = DateTimeOffset.UtcNow.AddMinutes(-1), organizationWide = true })).StatusCode);
        var restrictedLogin = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { userName = targetName, password = "Admin123!", mfaCode = (string?)null }); Assert.Equal(HttpStatusCode.OK, restrictedLogin.StatusCode);
        var restrictedJson = JsonDocument.Parse(await restrictedLogin.Content.ReadAsStringAsync()).RootElement; Assert.True(restrictedJson.GetProperty("mfaEnrollmentRequired").GetBoolean()); var restricted = factory.CreateClient(); restricted.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", restrictedJson.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await restricted.GetAsync("/api/phase1/service-users")).StatusCode); Assert.Equal(HttpStatusCode.OK, (await restricted.GetAsync("/api/security/mfa/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.PostAsJsonAsync($"/api/security/mfa/users/{targetId}/reset", new { identityEvidence = "ID checked", reason = "Lost device", adminMfaCode = "000000" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/security/mfa/users/{targetId}/reset", new { identityEvidence = "Photo ID and manager confirmation", reason = "Lost device", adminMfaCode = Totp(adminSecret) })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/auth/refresh-token", new { refreshToken = targetLogin.RefreshToken })).StatusCode);

        using var verifyScope = factory.Services.CreateScope(); var verifyDb = verifyScope.ServiceProvider.GetRequiredService<CareDbContext>(); Assert.True(await verifyDb.AuditEvents.AnyAsync(x => x.Action == "security.mfa_admin_reset" && x.EntityId == targetId));
        await Assert.ThrowsAnyAsync<Exception>(() => verifyDb.Database.ExecuteSqlInterpolatedAsync($"update auth_mfa_events set detail='changed' where user_id={targetId}"));
    }

    private async Task<Login> PasswordLogin(string name) { var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { userName = name, password = "Admin123!", mfaCode = (string?)null }); response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<Login>())!; }
    private HttpClient Auth(Login login) { var client = factory.CreateClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token); return client; }
    private static AppUser User(string name, UserRole role) => new(Guid.NewGuid(), name, $"{name}@aicare.local", PasswordHasher.HashPassword("Admin123!"), role, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId);
    private static string Totp(string secret) { const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"; var output = new List<byte>(); int buffer = 0, bits = 0; foreach (var ch in secret) { buffer = (buffer << 5) | alphabet.IndexOf(ch); bits += 5; if (bits >= 8) { output.Add((byte)(buffer >> (bits - 8))); bits -= 8; buffer &= (1 << bits) - 1; } } var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30; var bytes = BitConverter.GetBytes(counter); if (BitConverter.IsLittleEndian) Array.Reverse(bytes); using var hmac = new HMACSHA1(output.ToArray()); var hash = hmac.ComputeHash(bytes); var index = hash[^1] & 15; var value = (((hash[index] & 127) << 24) | ((hash[index + 1] & 255) << 16) | ((hash[index + 2] & 255) << 8) | (hash[index + 3] & 255)) % 1_000_000; return value.ToString("D6"); }
    private sealed record Login(string Token, string RefreshToken, int ExpiresInMinutes);
}
