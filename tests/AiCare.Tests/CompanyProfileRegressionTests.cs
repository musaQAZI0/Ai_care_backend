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
public sealed class CompanyProfileRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task CompanyProfileSavesReloadsPreservesAndRemovesLogoAndRejectsManagerWrites()
    {
        await factory.EnsureClinicalSeedAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var managerName = $"company.manager.{suffix}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.AppUsers.Add(new AppUser(Guid.NewGuid(), managerName, $"{managerName}@example.test", PasswordHasher.HashPassword("Admin123!"),
                UserRole.CareManager, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await db.SaveChangesAsync();
        }

        object Payload(string display, string? logo = null, bool remove = false) => new
        {
            legalName = $"Legal {suffix}", displayName = display, address = "1 Care Street\nLondon", businessPhone = "020 0000 0000",
            contactEmail = $"contact.{suffix}@example.test", logoContentType = logo is null ? null : "image/png", logoBase64 = logo, removeLogo = remove
        };
        using var manager = await Login(managerName);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PutAsJsonAsync("/api/phase1/admin/company-profile", Payload("Rejected"))).StatusCode);

        using var admin = await Login("admin");
        var logo = Convert.ToBase64String([1, 2, 3, 4]);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/phase1/admin/company-profile", Payload($"Display {suffix}", logo))).StatusCode);
        var saved = await admin.GetFromJsonAsync<JsonElement>("/api/phase1/admin/company-profile");
        Assert.Equal($"Legal {suffix}", saved.GetProperty("legalName").GetString());
        Assert.Equal($"Display {suffix}", saved.GetProperty("displayName").GetString());
        Assert.Equal(logo, saved.GetProperty("logoBase64").GetString());
        Assert.Contains(saved.GetProperty("branches").EnumerateArray(), item => item.GetProperty("id").GetGuid() == TenantDefaults.BranchId);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/phase1/admin/company-profile", Payload($"Renamed {suffix}"))).StatusCode);
        var preserved = await admin.GetFromJsonAsync<JsonElement>("/api/phase1/admin/company-profile");
        Assert.Equal(logo, preserved.GetProperty("logoBase64").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/phase1/admin/company-profile", Payload($"Renamed {suffix}", remove: true))).StatusCode);
        var removed = await admin.GetFromJsonAsync<JsonElement>("/api/phase1/admin/company-profile");
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("logoBase64").ValueKind);

        using var verify = factory.Services.CreateScope();
        var dbVerify = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal($"Renamed {suffix}", (await dbVerify.Organizations.AsNoTracking().SingleAsync(item => item.Id == TenantDefaults.OrganizationId)).Name);
        Assert.Equal(3, await dbVerify.AuditEvents.CountAsync(item => item.Action == "admin.company_profile_saved" && item.Actor == "admin" && item.EntityId == TenantDefaults.OrganizationId));
    }

    [Fact]
    public async Task RejectedCompanyProfileAuditRollsBackProfileAndSharedDisplayName()
    {
        await factory.EnsureClinicalSeedAsync();
        string originalName;
        string? originalDisplay;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            originalName = (await db.Organizations.AsNoTracking().SingleAsync(item => item.Id == TenantDefaults.OrganizationId)).Name;
            originalDisplay = await db.Database.SqlQueryRaw<string>("select display_name as \"Value\" from organization_profiles where organization_id={0}", TenantDefaults.OrganizationId).SingleOrDefaultAsync();
            await db.Database.ExecuteSqlRawAsync("""
                alter table "AuditEvents" add constraint company_profile_audit_rejected
                check ("Action" <> 'admin.company_profile_saved' or "Actor" <> 'admin') not valid
                """);
        }
        try
        {
            using var admin = await Login("admin");
            var response = await admin.PutAsJsonAsync("/api/phase1/admin/company-profile", new
            {
                legalName = "Rejected legal", displayName = "Rejected display", address = "", businessPhone = "", contactEmail = "",
                logoContentType = (string?)null, logoBase64 = (string?)null, removeLogo = false
            });
            Assert.False(response.IsSuccessStatusCode);

            using var verify = factory.Services.CreateScope();
            var db = verify.ServiceProvider.GetRequiredService<CareDbContext>();
            Assert.Equal(originalName, (await db.Organizations.AsNoTracking().SingleAsync(item => item.Id == TenantDefaults.OrganizationId)).Name);
            var display = await db.Database.SqlQueryRaw<string>("select display_name as \"Value\" from organization_profiles where organization_id={0}", TenantDefaults.OrganizationId).SingleOrDefaultAsync();
            Assert.Equal(originalDisplay, display);
        }
        finally
        {
            using var cleanup = factory.Services.CreateScope();
            await cleanup.ServiceProvider.GetRequiredService<CareDbContext>().Database.ExecuteSqlRawAsync(
                "alter table \"AuditEvents\" drop constraint if exists company_profile_audit_rejected");
        }
    }

    private async Task<HttpClient> Login(string name)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { userName = name, password = "Admin123!", mfaCode = (string?)null });
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString());
        return client;
    }
}
