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
public sealed class AdminAccountBranchRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task MissingWorkerBranchRequiresStepUpAndMatchingProfileAndPreservesAccount()
    {
        await factory.EnsureClinicalSeedAsync();
        var id = Guid.NewGuid(); var workerId = Guid.NewGuid(); var otherBranch = Guid.NewGuid();
        var name = $"branch.repair.{id:N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Branches.Add(new Branch(otherBranch, TenantDefaults.OrganizationId, "Other repair test branch", "Test", "Active"));
            db.CareWorkers.Add(new CareWorker(workerId, name, "Personal care", "Weekdays", 0, 0, "Pending", "Not started", "10 miles", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.AppUsers.Add(new AppUser(id, name, $"{name}@example.invalid", PasswordHasher.HashPassword("SyntheticTest123!"), UserRole.CareWorker, true, TenantDefaults.OrganizationId, null, workerId));
            await db.SaveChangesAsync();
        }
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "Admin123!" });
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString());
        var path = $"/api/phase1/admin/accounts/{id}/branch";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PatchAsJsonAsync(path, new { branchId = TenantDefaults.BranchId, reason = "Repair missing branch assignment" })).StatusCode);
        await StepUpTestGrants.GrantAsync(factory, client, "admin");
        var wrongBranch = await client.PatchAsJsonAsync(path, new { branchId = otherBranch, reason = "Reject incorrect worker branch" });
        Assert.True(wrongBranch.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict);
        await StepUpTestGrants.GrantAsync(factory, client, "admin");
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync(path, new { branchId = TenantDefaults.BranchId, reason = "Repair missing branch assignment" })).StatusCode);
        await StepUpTestGrants.GrantAsync(factory, client, "admin");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PatchAsJsonAsync(path, new { branchId = TenantDefaults.BranchId, reason = "Do not duplicate branch repair" })).StatusCode);
        using var verify = factory.Services.CreateScope();
        var context = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        var account = await context.AppUsers.AsNoTracking().SingleAsync(user => user.Id == id);
        Assert.Equal(TenantDefaults.BranchId, account.BranchId);
        Assert.Equal(workerId, account.CareWorkerId);
        Assert.True(account.IsActive);
        Assert.Equal(UserRole.CareWorker, account.Role);
        var count = await context.Database.SqlQuery<int>($"""select count(*)::integer as "Value" from admin_branch_change_events where user_id={id}""").SingleAsync();
        Assert.Equal(1, count);
    }
}
