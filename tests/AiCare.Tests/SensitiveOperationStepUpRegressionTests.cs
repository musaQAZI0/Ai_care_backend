using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed class SensitiveOperationStepUpRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task SensitiveActionRequiresSingleUseSessionBoundTotpGrantAndImmutableEvents()
    {
        await factory.EnsureClinicalSeedAsync();
        var name = $"stepup.{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.AppUsers.Add(new AppUser(Guid.NewGuid(), name, $"{name}@aicare.local", PasswordHasher.HashPassword("Admin123!"), UserRole.CareManager, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await db.SaveChangesAsync();
        }

        var login = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { userName = name, password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode();
        var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var enroll = await client.PostAsync("/api/security/mfa/enroll", null);
        enroll.EnsureSuccessStatusCode();
        var secret = JsonDocument.Parse(await enroll.Content.ReadAsStringAsync()).RootElement.GetProperty("secret").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/security/mfa/enroll/verify", new { code = Totp(secret) })).StatusCode);

        var report = new { name = "Step-up regression", format = "CSV" };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/phase1/reports/generate", report)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/security/step-up/challenge", new { purpose = "export", operationScope = "report.generate", code = "000000", evidence = "Regression" })).StatusCode);

        var grant = await client.PostAsJsonAsync("/api/security/step-up/challenge", new { purpose = "export", operationScope = "report.generate", code = Totp(secret), evidence = "Regression" });
        Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
        var json = JsonDocument.Parse(await grant.Content.ReadAsStringAsync()).RootElement;
        Assert.True(json.GetProperty("expiresAt").GetDateTimeOffset() <= DateTimeOffset.UtcNow.AddMinutes(11));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/phase1/reports/generate", report)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/phase1/reports/generate", report)).StatusCode);

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x => x.Action == "security.step_up_granted"));
        Assert.True(await verifyDb.Database.SqlQueryRaw<int>("""select 1 as "Value" from auth_step_up_events where event_type='Required' limit 1""").AnyAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => verifyDb.Database.ExecuteSqlRawAsync("update auth_step_up_events set detail='changed' where event_type='Used'"));
    }

    private static string Totp(string secret)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>(); int buffer = 0, bits = 0;
        foreach (var ch in secret) { buffer = (buffer << 5) | alphabet.IndexOf(ch); bits += 5; if (bits >= 8) { output.Add((byte)(buffer >> (bits - 8))); bits -= 8; buffer &= (1 << bits) - 1; } }
        var bytes = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30); if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        using var hmac = new HMACSHA1(output.ToArray()); var hash = hmac.ComputeHash(bytes); var index = hash[^1] & 15;
        return ((((hash[index] & 127) << 24) | ((hash[index + 1] & 255) << 16) | ((hash[index + 2] & 255) << 8) | (hash[index + 3] & 255)) % 1_000_000).ToString("D6");
    }
}
