using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed class ReportingBranchIsolationRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task BranchReportsAreConcealedWhileOrganizationWideAdministratorsCanAccessThem()
    {
        await factory.EnsureClinicalSeedAsync();
        var branchA = Guid.NewGuid();
        var branchB = Guid.NewGuid();
        var managerName = $"branch.manager.{Guid.NewGuid():N}";
        var adminName = $"branch.admin.{Guid.NewGuid():N}";
        var branchAReport = Guid.NewGuid();
        var branchBReport = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.AppUsers.AddRange(
                User(managerName, UserRole.CareManager, branchA),
                User(adminName, UserRole.Administrator, branchA));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                insert into report_runs(id,organization_id,branch_id,name,category,format,status,filters_json,metrics_json,generated_by)
                values
                ({{branchAReport}},{{TenantDefaults.OrganizationId}},{{branchA}},'Branch A report','Operational','CSV','Generated','{}'::jsonb,'{"count":1}'::jsonb,'regression'),
                ({{branchBReport}},{{TenantDefaults.OrganizationId}},{{branchB}},'Branch B secret report','Operational','CSV','Generated','{}'::jsonb,'{"count":2}'::jsonb,'regression');
                insert into compliance_evidence_items(id,organization_id,branch_id,domain,requirement,evidence_type,evidence_reference,status,owner)
                values
                ({{Guid.NewGuid()}},{{TenantDefaults.OrganizationId}},{{branchA}},'Safe','Visible branch evidence','Report','A-1','Ready','Owner'),
                ({{Guid.NewGuid()}},{{TenantDefaults.OrganizationId}},{{branchB}},'Safe','Hidden branch evidence','Report','B-1','Ready','Owner');
                """);
        }

        var manager = await Login(managerName);
        var admin = await Login(adminName);
        await StepUpTestGrants.GrantAsync(factory, manager, "export");
        await StepUpTestGrants.GrantAsync(factory, admin, "export");

        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"/api/phase1/reporting-compliance/report-runs/{branchAReport}/csv")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/phase1/reporting-compliance/report-runs/{branchBReport}/csv")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/phase1/reporting-compliance/report-runs/{branchBReport}/csv")).StatusCode);

        var managerPack = await manager.GetStringAsync("/api/phase1/reporting-compliance/evidence-pack.csv");
        Assert.Contains("Visible branch evidence", managerPack);
        Assert.DoesNotContain("Hidden branch evidence", managerPack);
        var adminPack = await admin.GetStringAsync("/api/phase1/reporting-compliance/evidence-pack.csv");
        Assert.Contains("Hidden branch evidence", adminPack);
    }

    private async Task<HttpClient> Login(string name)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { userName = name, password = "Admin123!", mfaCode = (string?)null });
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<LoginResponse>())!.Token);
        return client;
    }

    private static AppUser User(string name, UserRole role, Guid branchId) =>
        new(Guid.NewGuid(), name, $"{name}@aicare.local", PasswordHasher.HashPassword("Admin123!"), role, true, TenantDefaults.OrganizationId, branchId);

    private sealed record LoginResponse(string Token);
}