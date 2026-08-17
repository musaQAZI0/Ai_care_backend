using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed class ProductionRegressionTests : IClassFixture<PostgresRegressionFactory>
{
    private readonly PostgresRegressionFactory _factory;

    public ProductionRegressionTests(PostgresRegressionFactory factory) => _factory = factory;

    [Fact]
    public async Task ProductionHealthAndDatabaseAreHealthy()
    {
        var client = _factory.CreateClient();
        var health = await client.GetAsync("/health");
        var database = await client.GetAsync("/health/db");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, database.StatusCode);
        Assert.Contains("healthy", await database.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshTokenRotatesAndOldTokenCannotBeReused()
    {
        var client = _factory.CreateClient();
        var login = await Login(client);

        var rotatedResponse = await client.PostAsJsonAsync("/api/auth/refresh-token", new { refreshToken = login.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, rotatedResponse.StatusCode);
        var rotated = await rotatedResponse.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.False(string.IsNullOrWhiteSpace(rotated?.RefreshToken));
        Assert.NotEqual(login.RefreshToken, rotated!.RefreshToken);

        var replay = await client.PostAsJsonAsync("/api/auth/refresh-token", new { refreshToken = login.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rotated.Token);
        var logout = await client.PostAsJsonAsync("/api/auth/logout", new { refreshToken = rotated.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        var afterLogout = await client.PostAsJsonAsync("/api/auth/refresh-token", new { refreshToken = rotated.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Fact]
    public async Task MedicationSafetyProfileAndMarAuditEventRoundTrip()
    {
        await _factory.EnsureClinicalSeedAsync();
        var client = _factory.CreateClient();
        var login = await Login(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        var profile = await client.PutAsJsonAsync($"/api/phase1/medication-safety/medications/{RegressionIds.MedicationId}/profile", new
        {
            indication = "Pain management",
            prescriber = "Dr Regression",
            form = "Tablet",
            strength = "500 mg",
            startDate = DateTimeOffset.UtcNow.AddDays(-1),
            endDate = (DateTimeOffset?)null,
            doseWindowMinutes = 60,
            maxPrnDoses24h = 4,
            minPrnIntervalMinutes = 240,
            prnIndication = "Pain score 4 or above",
            prnEffectReviewMinutes = 60,
            stockOnHand = 20m,
            reorderLevel = 5m,
            requiresWitness = false,
            lastReconciledAt = DateTimeOffset.UtcNow,
            reconciledBy = "Regression Admin"
        });
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        Assert.Contains("Dr Regression", await profile.Content.ReadAsStringAsync());

        var safetyEvent = await client.PostAsJsonAsync($"/api/phase1/medication-safety/mar/{RegressionIds.MarId}/events", new
        {
            eventType = "OmissionReason",
            reason = "Person asleep; manager informed",
            effect = "Dose withheld safely",
            witnessedBy = "",
            stockDelta = (decimal?)null
        });
        Assert.Equal(HttpStatusCode.Created, safetyEvent.StatusCode);

        var events = await client.GetAsync($"/api/phase1/medication-safety/mar/{RegressionIds.MarId}/events");
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        var body = await events.Content.ReadAsStringAsync();
        Assert.Contains("OmissionReason", body);
        Assert.Contains("Person asleep", body);
    }

    [Fact]
    public async Task SafeguardingCaseSupportsActionAndSafeClosure()
    {
        await _factory.EnsureClinicalSeedAsync();
        var client = _factory.CreateClient();
        var login = await Login(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        var create = await client.PostAsJsonAsync("/api/phase1/safeguarding/cases", new
        {
            serviceUserId = RegressionIds.ServiceUserId,
            incidentId = (Guid?)null,
            category = "Neglect",
            concern = "Regression safeguarding concern",
            immediateActions = "Manager informed and person made safe",
            riskLevel = "High",
            externalReferral = "Local authority",
            referralReference = "REG-001",
            owner = "Regression Admin",
            reviewDueAt = DateTimeOffset.UtcNow.AddDays(1)
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<SafeguardingCaseResponse>();
        Assert.NotNull(created);

        var action = await client.PostAsJsonAsync($"/api/phase1/safeguarding/cases/{created!.Id}/actions", new
        {
            actionType = "Protection",
            detail = "Confirm immediate protection plan",
            owner = "Regression Admin",
            dueAt = DateTimeOffset.UtcNow.AddHours(4)
        });
        Assert.Equal(HttpStatusCode.Created, action.StatusCode);
        var actionId = (await action.Content.ReadFromJsonAsync<CreatedId>())!.Id;

        var complete = await client.PostAsync($"/api/phase1/safeguarding/cases/{created.Id}/actions/{actionId}/complete", null);
        Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);

        var close = await client.PutAsJsonAsync($"/api/phase1/safeguarding/cases/{created.Id}", new
        {
            status = "Closed",
            immediateActions = "Protection plan completed",
            riskLevel = "Low",
            externalReferral = "Local authority",
            referralReference = "REG-001",
            owner = "Regression Admin",
            reviewDueAt = (DateTimeOffset?)null,
            closureSummary = "Regression case reviewed and safely closed"
        });
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        Assert.Contains("Closed", await close.Content.ReadAsStringAsync());
    }

    private static async Task<LoginResponse> Login(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "Admin123!", mfaCode = (string?)null });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    private sealed record LoginResponse(string Token, string RefreshToken, int ExpiresInMinutes);
    private sealed record CreatedId(Guid Id);
    private sealed record SafeguardingCaseResponse(Guid Id, string Status);
}

[CollectionDefinition("Postgres regression", DisableParallelization = true)]
public sealed class PostgresRegressionCollection;

public sealed class PostgresRegressionFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = Environment.GetEnvironmentVariable("AICARE_REGRESSION_CONNECTION") ?? "Host=localhost;Port=5432;Database=aicare_regression;Username=postgres;Password=postgres",
            ["JwtOptions:Issuer"] = "AiCare",
            ["JwtOptions:Audience"] = "AiCareClient",
            ["JwtOptions:SigningKey"] = "regression-signing-key-with-enough-length-for-hmac-2026",
            ["JwtOptions:TokenLifetimeMinutes"] = "30",
            ["Storage:Provider"] = "Local"
        }));
    }

    public async Task EnsureClinicalSeedAsync()
    {
        _ = CreateClient();
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        if (await db.ServiceUsers.FindAsync(RegressionIds.ServiceUserId) is null)
            db.ServiceUsers.Add(new ServiceUser(RegressionIds.ServiceUserId, "Regression Person", new DateOnly(1980, 1, 1), "07000000000", "Personal care", "Regression Contact", "Regression Worker", RiskLevel.Medium, "Active", "1 Test Street", "None", "None", "Local authority", "Other", "", "Independent", "Full capacity", "Verbal", "None", "Standard", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        if (await db.CareWorkers.FindAsync(RegressionIds.WorkerId) is null)
            db.CareWorkers.Add(new CareWorker(RegressionIds.WorkerId, "Regression Worker", "Medication support", "Available", 1, 50, "Valid", "Compliant", "10 miles", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        if (await db.Visits.FindAsync(RegressionIds.VisitId) is null)
            db.Visits.Add(new Visit(RegressionIds.VisitId, RegressionIds.ServiceUserId, RegressionIds.WorkerId, DateTimeOffset.UtcNow.AddHours(1), "Medication visit", 30, "Medication support", VisitStatus.Scheduled, null, null, null, null, null, null, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        if (await db.Medications.FindAsync(RegressionIds.MedicationId) is null)
            db.Medications.Add(new Medication(RegressionIds.MedicationId, RegressionIds.ServiceUserId, "Paracetamol", "500 mg", "Oral", "PRN", true, "Regression Pharmacy", "None", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        if (await db.MedicationAdministrationRecords.FindAsync(RegressionIds.MarId) is null)
            db.MedicationAdministrationRecords.Add(new MedicationAdministrationRecord(RegressionIds.MarId, RegressionIds.MedicationId, RegressionIds.VisitId, RegressionIds.WorkerId, DateTimeOffset.UtcNow.AddHours(1), null, "Scheduled", "", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        await db.SaveChangesAsync();
    }
}

internal static class RegressionIds
{
    internal static readonly Guid ServiceUserId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    internal static readonly Guid WorkerId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    internal static readonly Guid VisitId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
    internal static readonly Guid MedicationId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004");
    internal static readonly Guid MarId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000005");
}
