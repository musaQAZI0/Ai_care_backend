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
public sealed class AdminAccountStatusRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task AdministratorCanFilterSuspendAndUnblockExistingAccountsButCannotActivateAnInvitation()
    {
        await factory.EnsureClinicalSeedAsync();
        var name = $"admin.status.{Guid.NewGuid():N}";
        var invitedName = $"admin.invited.{Guid.NewGuid():N}";
        var accountId = Guid.NewGuid();
        var invitedId = Guid.NewGuid();
        var outsideOrganizationId = Guid.NewGuid();
        var outsideAccountId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Organizations.Add(new Organization(outsideOrganizationId, "Role boundary organisation", "Trial", "Active"));
            db.AppUsers.Add(new AppUser(accountId, name, $"{name}@aicare.local", PasswordHasher.HashPassword("Admin123!"), UserRole.CareCoordinator, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.AppUsers.Add(new AppUser(invitedId, invitedName, $"{invitedName}@aicare.local", PasswordHasher.HashPassword("Admin123!"), UserRole.CareCoordinator, false, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.AppUsers.Add(new AppUser(outsideAccountId, $"outside.{name}", $"outside.{name}@aicare.local", PasswordHasher.HashPassword("Admin123!"), UserRole.CareCoordinator, true, outsideOrganizationId));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""insert into account_invitations(id,user_id,token_hash,email,role,status,expires_at,created_at,created_by_user_id,created_by,organization_id,branch_id) values ({Guid.NewGuid()},{invitedId},{Guid.NewGuid().ToString("N")},{$"{invitedName}@aicare.local"},{"CareCoordinator"},{"Sent"},{DateTimeOffset.UtcNow.AddDays(1)},now(),{accountId},{name},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId})""");
        }

        var admin = factory.CreateClient();
        var login = await admin.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString());
        await StepUpTestGrants.GrantAsync(factory, admin, "admin");

        var active = await admin.GetFromJsonAsync<JsonElement>($"/api/phase1/admin/accounts?search={name}&includeBlocked=false");
        Assert.Equal(1, active.GetProperty("total").GetInt32());
        var suspended = await admin.PatchAsJsonAsync($"/api/phase1/admin/accounts/{accountId}/status", new { active = false, reason = "Suspended after access review" });
        Assert.True(suspended.StatusCode == HttpStatusCode.OK, $"Expected 200, got {(int)suspended.StatusCode}: {await suspended.Content.ReadAsStringAsync()}");
        var hidden = await admin.GetFromJsonAsync<JsonElement>($"/api/phase1/admin/accounts?search={name}&includeBlocked=false");
        Assert.Equal(0, hidden.GetProperty("total").GetInt32());
        var listed = await admin.GetFromJsonAsync<JsonElement>($"/api/phase1/admin/accounts?search={name}&includeBlocked=true");
        Assert.Equal("Suspended", listed.GetProperty("items")[0].GetProperty("status").GetString());

        await StepUpTestGrants.GrantAsync(factory, admin, "admin");
        var invited = await admin.PatchAsJsonAsync($"/api/phase1/admin/accounts/{invitedId}/status", new { active = true, reason = "Restore access after review" });
        Assert.Equal(HttpStatusCode.Conflict, invited.StatusCode);
        await StepUpTestGrants.GrantAsync(factory, admin, "admin");
        var unblocked = await admin.PostAsJsonAsync("/api/phase1/admin/accounts/bulk-unblock", new { userIds = new[] { accountId }, reason = "Restore access after review" });
        Assert.Equal(HttpStatusCode.OK, unblocked.StatusCode);
        var affectedUser = factory.CreateClient();
        var affectedLogin = await affectedUser.PostAsJsonAsync("/api/auth/login", new { userName = name, password = "Admin123!", mfaCode = (string?)null });
        affectedLogin.EnsureSuccessStatusCode();
        affectedUser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", JsonDocument.Parse(await affectedLogin.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString());
        await StepUpTestGrants.GrantAsync(factory, admin, "admin");
        var missingReason = await admin.PatchAsJsonAsync($"/api/phase1/admin/users/{accountId}/role", new { role = "CareManager" });
        Assert.Equal(HttpStatusCode.BadRequest, missingReason.StatusCode);
        await StepUpTestGrants.GrantAsync(factory, admin, "admin");
        var platformRole = await admin.PatchAsJsonAsync($"/api/phase1/admin/users/{accountId}/role", new { role = "BackOffice", reason = "Requested platform access review" });
        Assert.Equal(HttpStatusCode.BadRequest, platformRole.StatusCode);
        await StepUpTestGrants.GrantAsync(factory, admin, "admin");
        var roleChange = await admin.PatchAsJsonAsync($"/api/phase1/admin/users/{accountId}/role", new { role = "CareManager", reason = "Approved access review change" });
        Assert.True(roleChange.StatusCode == HttpStatusCode.OK,
            $"Expected 200, got {(int)roleChange.StatusCode}: {await roleChange.Content.ReadAsStringAsync()}");
        var history = await admin.GetFromJsonAsync<JsonElement>($"/api/phase1/admin/accounts/{accountId}/role-history");
        Assert.Equal("CareCoordinator", history[0].GetProperty("oldRole").GetString());
        Assert.Equal("CareManager", history[0].GetProperty("newRole").GetString());
        Assert.Equal("Approved access review change", history[0].GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await affectedUser.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/phase1/admin/accounts/{outsideAccountId}/role-history")).StatusCode);
        await StepUpTestGrants.GrantAsync(factory, admin, "admin");
        var outsideChange = await admin.PatchAsJsonAsync($"/api/phase1/admin/users/{outsideAccountId}/role", new { role = "CareManager", reason = "Cross organisation denial check" });
        Assert.Equal(HttpStatusCode.NotFound, outsideChange.StatusCode);
        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        var ownAdminId = await verifyDb.AppUsers.Where(user => user.UserName == "admin").Select(user => user.Id).SingleAsync();
        await StepUpTestGrants.GrantAsync(factory, admin, "admin");
        var selfDemotion = await admin.PatchAsJsonAsync($"/api/phase1/admin/users/{ownAdminId}/role", new { role = "CareManager", reason = "Review of my administrator access" });
        Assert.Equal(HttpStatusCode.Conflict, selfDemotion.StatusCode);
        Assert.True(await verifyDb.AppUsers.Where(user => user.Id == accountId).Select(user => user.IsActive).SingleAsync());
        Assert.Equal(UserRole.CareManager, await verifyDb.AppUsers.Where(user => user.Id == accountId).Select(user => user.Role).SingleAsync());
        Assert.False(await verifyDb.AppUsers.Where(user => user.Id == invitedId).Select(user => user.IsActive).SingleAsync());
    }
}
