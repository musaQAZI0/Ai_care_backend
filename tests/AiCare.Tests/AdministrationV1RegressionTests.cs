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
public sealed class AdministrationV1RegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task RegionsSaveReloadAssignBranchesAndStayInsideTheOrganization()
    {
        await factory.EnsureClinicalSeedAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var otherOrganization = Guid.NewGuid();
        var otherBranch = Guid.NewGuid();
        var ownBranch = Guid.NewGuid();
        var manager = $"regions.manager.{suffix}";
        var otherAdmin = $"regions.other.{suffix}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Organizations.Add(new Organization(otherOrganization, $"Other {suffix}", "Trial", "Active"));
            db.Branches.AddRange(
                new Branch(ownBranch, TenantDefaults.OrganizationId, "New branch", "Original", "Active"),
                new Branch(otherBranch, otherOrganization, "Other branch", "Secret region", "Active"));
            db.AppUsers.AddRange(User(manager, UserRole.CareManager, TenantDefaults.OrganizationId, ownBranch),
                User(otherAdmin, UserRole.Administrator, otherOrganization, otherBranch));
            await db.SaveChangesAsync();
        }

        using var admin = await Login("admin");
        using var restricted = await Login(manager);
        using var tenantTwo = await Login(otherAdmin);
        var name = $"North {suffix}";
        Assert.Equal(HttpStatusCode.Forbidden, (await restricted.PostAsJsonAsync("/api/phase1/admin/regions", new { name })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/phase1/admin/regions", new { name })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/phase1/admin/regions/branches/{ownBranch}", new { name })).StatusCode);
        var renamed = $"West {suffix}";
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/phase1/admin/regions/{Uri.EscapeDataString(name)}", new { name = renamed })).StatusCode);
        using var reloaded = await Login("admin");
        var regions = await reloaded.GetFromJsonAsync<JsonElement>("/api/phase1/admin/regions");
        Assert.Contains(regions.EnumerateArray(), item => item.GetProperty("name").GetString() == renamed && item.GetProperty("branchCount").GetInt32() == 1);
        Assert.DoesNotContain(regions.EnumerateArray(), item => item.GetProperty("name").GetString() == "Secret region");
        Assert.Equal(HttpStatusCode.NotFound, (await reloaded.PutAsJsonAsync($"/api/phase1/admin/regions/branches/{otherBranch}", new { name = renamed })).StatusCode);
        var otherRegions = await tenantTwo.GetFromJsonAsync<JsonElement>("/api/phase1/admin/regions");
        Assert.DoesNotContain(otherRegions.EnumerateArray(), item => item.GetProperty("name").GetString() == renamed);
        using var verify = factory.Services.CreateScope();
        var database = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal(renamed, (await database.Branches.SingleAsync(item => item.Id == ownBranch)).Region);
        Assert.True(await database.AuditEvents.AnyAsync(item => item.OrganizationId == TenantDefaults.OrganizationId && item.Action == "admin.branch_region_changed" && item.EntityId == ownBranch));
    }

    [Fact]
    public async Task DashboardPreferencesPersistPerUserAndRejectInvalidValues()
    {
        await factory.EnsureClinicalSeedAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var first = $"dashboard.one.{suffix}";
        var second = $"dashboard.two.{suffix}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.AppUsers.AddRange(User(first, UserRole.CareManager, TenantDefaults.OrganizationId, TenantDefaults.BranchId),
                User(second, UserRole.CareManager, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await db.SaveChangesAsync();
        }
        using var firstClient = await Login(first);
        Assert.Equal(HttpStatusCode.NoContent, (await firstClient.PutAsJsonAsync("/api/phase1/dashboard/preferences", new { widgets = new[] { "stats", "people" } })).StatusCode);
        using var reloaded = await Login(first);
        var saved = await reloaded.GetFromJsonAsync<JsonElement>("/api/phase1/dashboard/preferences");
        Assert.Equal(new[] { "stats", "people" }, saved.GetProperty("widgets").EnumerateArray().Select(item => item.GetString()).ToArray());
        using var secondClient = await Login(second);
        var other = await secondClient.GetFromJsonAsync<JsonElement>("/api/phase1/dashboard/preferences");
        Assert.Equal(6, other.GetProperty("widgets").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await reloaded.PutAsJsonAsync("/api/phase1/dashboard/preferences", new { widgets = new[] { "unknown" } })).StatusCode);
        using var verify = factory.Services.CreateScope();
        Assert.True(await verify.ServiceProvider.GetRequiredService<CareDbContext>().AuditEvents.AnyAsync(item => item.Action == "dashboard.preferences_saved" && item.OrganizationId == TenantDefaults.OrganizationId));
    }

    [Fact]
    public async Task LoginAttemptHistoryRecordsOutcomesAndRejectsOtherRolesAndTenants()
    {
        await factory.EnsureClinicalSeedAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var name = $"attempts.worker.{suffix}";
        var otherOrganization = Guid.NewGuid();
        var otherBranch = Guid.NewGuid();
        var otherAdmin = $"attempts.admin.{suffix}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Organizations.Add(new Organization(otherOrganization, $"Attempts {suffix}", "Trial", "Active"));
            db.Branches.Add(new Branch(otherBranch, otherOrganization, "Other", "Other", "Active"));
            db.AppUsers.AddRange(User(name, UserRole.CareManager, TenantDefaults.OrganizationId, TenantDefaults.BranchId),
                User(otherAdmin, UserRole.Administrator, otherOrganization, otherBranch));
            await db.SaveChangesAsync();
        }
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/auth/login", new { userName = name, password = "Wrong123!" })).StatusCode);
        using var manager = await Login(name);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/phase1/admin/login-attempts")).StatusCode);
        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/auth/login", new { userName = name, password = "Wrong123!" })).StatusCode);
        Assert.Equal((HttpStatusCode)423, (await anonymous.PostAsJsonAsync("/api/auth/login", new { userName = name, password = "Wrong123!" })).StatusCode);
        using var admin = await Login("admin");
        var historyResponse = await admin.GetAsync("/api/phase1/admin/login-attempts?limit=200");
        Assert.True(historyResponse.IsSuccessStatusCode, await historyResponse.Content.ReadAsStringAsync());
        var rows = await historyResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(rows.EnumerateArray(), item => item.GetProperty("userName").GetString() == name && item.GetProperty("outcome").GetString() == "InvalidPassword");
        Assert.Contains(rows.EnumerateArray(), item => item.GetProperty("userName").GetString() == name && item.GetProperty("outcome").GetString() == "Succeeded");
        Assert.Contains(rows.EnumerateArray(), item => item.GetProperty("userName").GetString() == name && item.GetProperty("outcome").GetString() == "Locked");
        using var tenantTwo = await Login(otherAdmin);
        var isolated = await tenantTwo.GetFromJsonAsync<JsonElement>("/api/phase1/admin/login-attempts?limit=200");
        Assert.DoesNotContain(isolated.EnumerateArray(), item => item.GetProperty("userName").GetString() == name);
    }

    [Theory]
    [InlineData("ContactType")]
    [InlineData("ClientRespiteReason")]
    public async Task AdministrationOptionsPersistAuditAndRejectUnauthorizedChanges(string kind)
    {
        await factory.EnsureClinicalSeedAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var value = $"Test {kind} {suffix}";
        var managerName = $"options.manager.{suffix}";
        var otherOrganization = Guid.NewGuid();
        var otherBranch = Guid.NewGuid();
        var otherAdmin = $"options.other.{suffix}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Organizations.Add(new Organization(otherOrganization, $"Options {suffix}", "Trial", "Active"));
            db.Branches.Add(new Branch(otherBranch, otherOrganization, "Other", "Other", "Active"));
            db.AppUsers.AddRange(User(managerName, UserRole.CareManager, TenantDefaults.OrganizationId, TenantDefaults.BranchId),
                User(otherAdmin, UserRole.Administrator, otherOrganization, otherBranch));
            await db.SaveChangesAsync();
        }
        using var manager = await Login(managerName);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync("/api/phase1/care-operations/options", new { kind, value, active = true })).StatusCode);
        using var admin = await Login("admin");
        var created = await admin.PostAsJsonAsync("/api/phase1/care-operations/options", new { kind, value, active = true });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var reloaded = await Login("admin");
        var options = await reloaded.GetFromJsonAsync<JsonElement>("/api/phase1/care-operations/options");
        Assert.Contains(options.EnumerateArray(), item => item.GetProperty("id").GetGuid() == id && item.GetProperty("value").GetString() == value);
        using var tenantTwo = await Login(otherAdmin);
        var isolatedOptions = await tenantTwo.GetFromJsonAsync<JsonElement>("/api/phase1/care-operations/options");
        Assert.DoesNotContain(isolatedOptions.EnumerateArray(), item => item.GetProperty("id").GetGuid() == id);
        if (kind == "ContactType")
        {
            var path = $"/api/phase1/service-users/{RegressionIds.ServiceUserId}/contacts";
            object Contact(string type) => new { contactType = type, fullName = $"Contact {suffix}", relationship = "Friend", phoneNumber = "", email = "", organizationName = "", isPrimary = false, isEmergency = false };
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(path, Contact("Unconfigured"))).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync(path, Contact(value))).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/phase1/care-operations/options/{id}", new { kind, value, active = false })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync(path, Contact(value))).StatusCode);
            var contacts = await admin.GetFromJsonAsync<JsonElement>(path);
            Assert.Contains(contacts.EnumerateArray(), item => item.GetProperty("contactType").GetString() == value);

            var anotherBranch = Guid.NewGuid();
            var anotherPerson = Guid.NewGuid();
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
                db.Branches.Add(new Branch(anotherBranch, TenantDefaults.OrganizationId, "Contact branch", "South", "Active"));
                var original = await db.ServiceUsers.AsNoTracking().SingleAsync(item => item.Id == RegressionIds.ServiceUserId);
                db.ServiceUsers.Add(original with { Id = anotherPerson, BranchId = anotherBranch });
                await db.SaveChangesAsync();
            }
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/phase1/care-operations/options/{id}", new { kind, value, active = true })).StatusCode);
            var anotherContact = await admin.PostAsJsonAsync($"/api/phase1/service-users/{anotherPerson}/contacts", Contact(value));
            Assert.Equal(HttpStatusCode.Created, anotherContact.StatusCode);
            var contactId = (await anotherContact.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            using (var scope = factory.Services.CreateScope())
            {
                var storedBranch = await scope.ServiceProvider.GetRequiredService<CareDbContext>().Database
                    .SqlQuery<Guid>($"select branch_id as \"Value\" from person_contacts where id={contactId}").SingleAsync();
                Assert.Equal(anotherBranch, storedBranch);
            }
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/phase1/service-users/{anotherPerson}/contacts/{contactId}", Contact(value))).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/phase1/service-users/{anotherPerson}/contacts/{contactId}")).StatusCode);
            using var branchVerify = factory.Services.CreateScope();
            var branchDb = branchVerify.ServiceProvider.GetRequiredService<CareDbContext>();
            Assert.True(await branchDb.AuditEvents.AnyAsync(item => item.EntityId == contactId && item.Action == "person_contact.created" && item.BranchId == anotherBranch));
            Assert.True(await branchDb.AuditEvents.AnyAsync(item => item.EntityId == contactId && item.Action == "person_contact.updated" && item.BranchId == anotherBranch));
            Assert.True(await branchDb.AuditEvents.AnyAsync(item => item.EntityId == contactId && item.Action == "person_contact.deleted" && item.BranchId == anotherBranch));
        }
        using var verify = factory.Services.CreateScope();
        var dbVerify = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.True(await dbVerify.AuditEvents.AnyAsync(item => item.OrganizationId == TenantDefaults.OrganizationId && item.EntityId == id && item.Action == "operations.option_created"));
    }

    private async Task<HttpClient> Login(string name)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { userName = name, password = "Admin123!", mfaCode = (string?)null });
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString());
        return client;
    }

    private static AppUser User(string name, UserRole role, Guid organization, Guid branch) =>
        new(Guid.NewGuid(), name, $"{name}@example.test", PasswordHasher.HashPassword("Admin123!"), role, true, organization, branch);
}
