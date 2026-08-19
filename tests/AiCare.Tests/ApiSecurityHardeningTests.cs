using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using AiCare.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace AiCare.Tests;

public sealed class ApiSecurityHardeningTests : IClassFixture<AiCareApiFactory>
{
    private const string SigningKey = "test-signing-key-with-enough-length-for-hmac";
    private readonly AiCareApiFactory _factory;

    public ApiSecurityHardeningTests(AiCareApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void ProductionOriginPolicyAllowsOnlyConfiguredOrigin()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:AllowedOrigins:0"] = "https://ai-care-frontend.vercel.app"
            })
            .Build();

        Assert.True(ApiSecurityPolicy.IsOriginAllowed(configuration, "https://ai-care-frontend.vercel.app"));
        Assert.True(ApiSecurityPolicy.IsOriginAllowed(configuration, "https://ai-care-frontend.vercel.app/"));
        Assert.False(ApiSecurityPolicy.IsOriginAllowed(configuration, "http://localhost:5173"));
        Assert.False(ApiSecurityPolicy.IsOriginAllowed(configuration, "https://evil.example"));
    }

    [Theory]
    [InlineData("POST", "/api/auth/login", 10)]
    [InlineData("POST", "/api/auth/forgot-password", 5)]
    [InlineData("POST", "/api/auth/reset-password", 5)]
    [InlineData("POST", "/api/family/invitations/validate", 10)]
    [InlineData("POST", "/api/family/invitations/accept", 10)]
    [InlineData("POST", "/api/phase1/documents/upload", 30)]
    public void SensitiveEndpointHasExplicitRateLimit(string method, string path, int expected)
    {
        Assert.Equal(expected, ApiSecurityPolicy.GetRateLimit(method, path));
    }

    [Fact]
    public void NormalCareReadsAreNotRateLimitedByThisAbusePolicy()
    {
        Assert.Null(ApiSecurityPolicy.GetRateLimit("GET", "/api/phase1/service-users"));
        Assert.Null(ApiSecurityPolicy.GetRateLimit("POST", "/api/phase1/care-notes"));
    }

    [Fact]
    public async Task ApiResponsesIncludeCspAndNoStoreHeaders()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("default-src 'none'; frame-ancestors 'none'; base-uri 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? response.Headers.GetValues("Cache-Control").Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no-cache", response.Headers.GetValues("Pragma").Single());
    }

    [Fact]
    public async Task DatabaseHealthResponseDoesNotExposeConnectionDetails()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/db");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("PostgreSQL", body);
        Assert.DoesNotContain("ConnectionStrings", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password=", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Data Source", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CrossTenantAdministratorCannotReadServiceUserById()
    {
        var client = CrossTenantAdminClient();

        var response = await client.GetAsync($"/api/phase1/service-users/{TestIds.ServiceUserId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CrossTenantAdministratorCannotReadCompletePersonRecordById()
    {
        var client = CrossTenantAdminClient();

        var response = await client.GetAsync($"/api/phase1/service-users/{TestIds.ServiceUserId}/complete-record");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CrossTenantAdministratorCannotDeleteServiceUserById()
    {
        var attacker = CrossTenantAdminClient();

        var delete = await attacker.DeleteAsync($"/api/phase1/service-users/{TestIds.ServiceUserId}");
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

        var legitimate = _factory.CreateClient();
        legitimate.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateToken(TenantDefaults.OrganizationId, "legitimate-admin"));
        var read = await legitimate.GetAsync($"/api/phase1/service-users/{TestIds.ServiceUserId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task CrossTenantAdministratorCannotReadFamilyAccessById()
    {
        var client = CrossTenantAdminClient();

        var response = await client.GetAsync($"/api/phase1/family-access/{TestIds.FamilyMemberId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private HttpClient CrossTenantAdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateToken(Guid.Parse("77777777-7777-7777-7777-777777777777"), "cross-tenant-admin"));
        return client;
    }

    private static string CreateToken(Guid organizationId, string userName)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.UniqueName, userName),
            new(ClaimTypes.Role, "Administrator"),
            new(ClaimTypes.Email, $"{userName}@test.local"),
            new("organization_id", organizationId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
            SecurityAlgorithms.HmacSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            "AiCare",
            "AiCareClient",
            claims,
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: credentials));
    }
}
