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
public sealed class InvoiceProfileConcurrencyRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task ConcurrentProfileEditsRejectTheStaleWriterAndPreserveEveryAcceptedVersion()
    {
        await factory.EnsureClinicalSeedAsync();
        using var finance = await Login("admin");
        var initial = await Profile(finance);

        var firstSave = await finance.PutAsJsonAsync("/api/phase1/finance/invoice-profile", Payload("Initial governed provider", initial.Version));
        Assert.Equal(HttpStatusCode.NoContent, firstSave.StatusCode);
        var saved = await Profile(finance);
        Assert.Equal(initial.Version + 1, saved.Version);

        var competing = await Task.WhenAll(
            finance.PutAsJsonAsync("/api/phase1/finance/invoice-profile", Payload("First competing provider", saved.Version)),
            finance.PutAsJsonAsync("/api/phase1/finance/invoice-profile", Payload("Second competing provider", saved.Version)));
        Assert.Equal(1, competing.Count(response => response.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(1, competing.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        var conflict = competing.Single(response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Contains("Reload the latest settings", await conflict.Content.ReadAsStringAsync());

        var current = await Profile(finance);
        Assert.Equal(saved.Version + 1, current.Version);
        Assert.Contains(current.ProviderName, new[] { "First competing provider", "Second competing provider" });

        using var verify = factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        var versions = await db.Database.SqlQueryRaw<int>(
            "select count(*)::int as \"Value\" from finance_invoice_profile_versions where organization_id={0} and branch_id={1}",
            TenantDefaults.OrganizationId, TenantDefaults.BranchId).SingleAsync();
        Assert.Equal(current.Version, versions);
        Assert.Equal(2, await db.AuditEvents.CountAsync(item => item.Action == "finance.invoice_profile_updated" && item.Actor == "admin"));
    }

    [Fact]
    public async Task FailedAuditRollsBackProfileAndVersionTogether()
    {
        await factory.EnsureClinicalSeedAsync();
        using var finance = await Login("admin");
        var before = await Profile(finance);
        using (var setup = factory.Services.CreateScope())
        {
            await setup.ServiceProvider.GetRequiredService<CareDbContext>().Database.ExecuteSqlRawAsync("""
                alter table "AuditEvents" add constraint invoice_profile_audit_rejected
                check ("Action" <> 'finance.invoice_profile_updated' or "Actor" <> 'admin') not valid
                """);
        }
        try
        {
            var response = await finance.PutAsJsonAsync("/api/phase1/finance/invoice-profile", Payload("Must roll back", before.Version));
            Assert.False(response.IsSuccessStatusCode);
            var after = await Profile(finance);
            Assert.Equal(before.Version, after.Version);
            Assert.Equal(before.ProviderName, after.ProviderName);
        }
        finally
        {
            using var cleanup = factory.Services.CreateScope();
            await cleanup.ServiceProvider.GetRequiredService<CareDbContext>().Database.ExecuteSqlRawAsync(
                "alter table \"AuditEvents\" drop constraint if exists invoice_profile_audit_rejected");
        }
    }

    [Fact]
    public async Task FutureProfileIsAppendOnlyAndCareWorkersCannotMutateFinanceSettings()
    {
        await factory.EnsureClinicalSeedAsync();
        var financeName=$"profile.finance.{Guid.NewGuid():N}";var workerName=$"profile.worker.{Guid.NewGuid():N}";
        using(var setup=factory.Services.CreateScope())
        {
            var db=setup.ServiceProvider.GetRequiredService<CareDbContext>();
            db.AppUsers.AddRange(
                new AppUser(Guid.NewGuid(),financeName,$"{financeName}@aicare.local",PasswordHasher.HashPassword("Admin123!"),UserRole.BackOffice,true,TenantDefaults.OrganizationId,TenantDefaults.BranchId,null,null),
                new AppUser(Guid.NewGuid(),workerName,$"{workerName}@aicare.local",PasswordHasher.HashPassword("Admin123!"),UserRole.CareWorker,true,TenantDefaults.OrganizationId,TenantDefaults.BranchId,RegressionIds.WorkerId,null));
            await db.SaveChangesAsync();
        }
        using var finance=await Login(financeName);using var worker=await Login(workerName);
        var before=await Profile(finance);var effectiveFrom=DateTimeOffset.UtcNow.AddDays(7);
        var scheduled=await finance.PutAsJsonAsync("/api/phase1/finance/invoice-profile",new
        {
            providerName="Future governed provider",providerAddress="7 Future Street",providerEmail="future@example.test",
            providerPhone="020 7000 0000",companyNumber="87654321",vatNumber="",remittanceDetails="Future remittance",
            paymentTermsDays=21,vatRate=0m,vatExemptionReason="Future non-taxable supply",expectedVersion=before.Version,effectiveFrom
        });
        Assert.Equal(HttpStatusCode.NoContent,scheduled.StatusCode);
        var current=await Profile(finance);Assert.Equal(before.ProviderName,current.ProviderName);Assert.Equal(before.Version+1,current.Version);
        var denied=await worker.PutAsJsonAsync("/api/phase1/finance/invoice-profile",Payload("Forbidden worker edit",current.Version));
        Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
        using var verify=factory.Services.CreateScope();var verifyDb=verify.ServiceProvider.GetRequiredService<CareDbContext>();
        var futureName=await verifyDb.Database.SqlQueryRaw<string>("select profile_snapshot->>'providerName' as \"Value\" from finance_invoice_profile_versions where organization_id={0} and branch_id={1} and version={2}",TenantDefaults.OrganizationId,TenantDefaults.BranchId,current.Version).SingleAsync();
        Assert.Equal("Future governed provider",futureName);
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="finance.invoice_profile_scheduled"&&x.Actor==financeName));
    }

    [Fact]
    public async Task NumberPreviewUsesEffectiveSettingsAndDefaultWidthWhenCustomNumberingIsDisabled()
    {
        await factory.EnsureClinicalSeedAsync();
        using var finance = await Login("admin");
        var before = await Profile(finance);
        object NumberingPayload(int version, bool enabled, string prefix, DateTimeOffset? effectiveFrom = null) => new
        {
            providerName = "Numbering regression provider", providerAddress = "1 Version Street",
            providerEmail = "billing@example.test", providerPhone = "020 0000 0000",
            companyNumber = "12345678", vatNumber = "", remittanceDetails = "Quote invoice number",
            paymentTermsDays = 30, vatRate = 0m, vatExemptionReason = "Regression non-taxable supply",
            expectedVersion = version, customNumberingEnabled = enabled, invoiceNumberPrefix = prefix,
            invoiceNumberStart = 10000L, invoiceNumberPadding = 1, effectiveFrom
        };
        var saved = await finance.PutAsJsonAsync("/api/phase1/finance/invoice-profile", NumberingPayload(before.Version, true, "NOW-"));
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        var current = await Profile(finance);
        var scheduled = await finance.PutAsJsonAsync("/api/phase1/finance/invoice-profile",
            NumberingPayload(current.Version, true, "FUTURE-", DateTimeOffset.UtcNow.AddDays(7)));
        Assert.Equal(HttpStatusCode.NoContent, scheduled.StatusCode);
        var preview = await finance.GetFromJsonAsync<JsonElement>("/api/phase1/finance/invoice-profile/number-preview");
        Assert.Equal($"NOW-{preview.GetProperty("year").GetInt32()}-{preview.GetProperty("nextValue").GetInt64()}",
            preview.GetProperty("invoiceNumber").GetString());

        current = await Profile(finance);
        saved = await finance.PutAsJsonAsync("/api/phase1/finance/invoice-profile", NumberingPayload(current.Version, false, "IGNORED-"));
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        preview = await finance.GetFromJsonAsync<JsonElement>("/api/phase1/finance/invoice-profile/number-preview");
        Assert.False(preview.GetProperty("enabled").GetBoolean());
        Assert.Equal($"INV-{preview.GetProperty("year").GetInt32()}-{preview.GetProperty("nextValue").GetInt64():D6}",
            preview.GetProperty("invoiceNumber").GetString());
    }

    [Theory]
    [InlineData("inv-")]
    [InlineData("INV.2026")]
    [InlineData("INV-£")]
    [InlineData("   ")]
    public async Task InvalidNumberingPrefixesReturnValidationErrorsWithoutChangingTheProfile(string prefix)
    {
        await factory.EnsureClinicalSeedAsync();
        using var finance = await Login("admin");
        var before = await Profile(finance);
        var response = await finance.PutAsJsonAsync("/api/phase1/finance/invoice-profile", new
        {
            providerName = "Invalid numbering edit", providerAddress = "1 Version Street",
            providerEmail = "billing@example.test", providerPhone = "020 0000 0000",
            companyNumber = "12345678", vatNumber = "", remittanceDetails = "Quote invoice number",
            paymentTermsDays = 30, vatRate = 0m, vatExemptionReason = "Regression non-taxable supply",
            expectedVersion = before.Version, invoiceNumberPrefix = prefix
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var after = await Profile(finance);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.ProviderName, after.ProviderName);
    }

    private static object Payload(string providerName, int expectedVersion) => new
    {
        providerName, providerAddress = "1 Version Street", providerEmail = "billing@example.test",
        providerPhone = "020 0000 0000", companyNumber = "12345678", vatNumber = "",
        remittanceDetails = "Quote the invoice number.", paymentTermsDays = 30, vatRate = 0m,
        vatExemptionReason = "Regression non-taxable supply", expectedVersion
    };

    private static async Task<ProfileResponse> Profile(HttpClient client) =>
        (await client.GetFromJsonAsync<ProfileResponse>("/api/phase1/finance/invoice-profile"))!;

    private async Task<HttpClient> Login(string name)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { userName = name, password = "Admin123!", mfaCode = (string?)null });
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString());
        return client;
    }

    private sealed record ProfileResponse(string ProviderName, int Version);
}
