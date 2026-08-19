using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiCare.Tests;

public sealed class DataGovernanceComplianceTests : IClassFixture<AiCareApiFactory>
{
    private readonly AiCareApiFactory _factory;

    public DataGovernanceComplianceTests(AiCareApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AdministratorCanCreateRetentionPolicyAndAuditIsRecorded()
    {
        var client = _factory.CreateClient();
        await Login(client, "admin", "AdminPassword123!");

        var response = await client.PutAsJsonAsync("/api/phase1/data-governance/retention-policies/CareRecords", new
        {
            retentionDays = 2920,
            legalBasis = "Care record retention schedule",
            dispositionAction = "Review",
            isActive = true,
            reviewDueAt = DateTimeOffset.UtcNow.AddYears(1)
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Contains(db.RetentionPolicies, x => x.OrganizationId == TenantDefaults.OrganizationId && x.DataCategory == "CareRecords");
        Assert.Contains(db.AuditEvents, x => x.Action == "governance.retention_policy_updated");
    }

    [Fact]
    public async Task SubjectExportIsTenantScopedAndAudited()
    {
        var client = _factory.CreateClient();
        await Login(client, "admin", "AdminPassword123!");

        var response = await client.GetAsync($"/api/phase1/data-governance/service-users/{TestIds.ServiceUserId}/export");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Test Service User", body);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Contains(db.DataGovernanceRequests, x => x.ServiceUserId == TestIds.ServiceUserId && x.RequestType == "SubjectAccessExport" && x.Status == "Completed");
        Assert.Contains(db.AuditEvents, x => x.Action == "governance.subject_exported" && x.EntityId == TestIds.ServiceUserId);
    }

    [Fact]
    public async Task ActiveServiceUserCannotBeAnonymized()
    {
        var client = _factory.CreateClient();
        await Login(client, "admin", "AdminPassword123!");

        var response = await client.PostAsJsonAsync($"/api/phase1/data-governance/service-users/{TestIds.ServiceUserId}/anonymize", new
        {
            confirmation = "ANONYMIZE",
            reason = "Retention period completed"
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal("Test Service User", db.ServiceUsers.Single(x => x.Id == TestIds.ServiceUserId).FullName);
    }

    [Fact]
    public void ExistingAuditEventCannotBeModifiedOrDeleted()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        var audit = new AuditEvent(Guid.NewGuid(), "governance.test", "tester", "ServiceUser", TestIds.ServiceUserId, DateTimeOffset.UtcNow, TenantDefaults.OrganizationId, TenantDefaults.BranchId);
        db.AuditEvents.Add(audit);
        db.SaveChanges();

        var tracked = db.AuditEvents.Single(x => x.Id == audit.Id);
        db.Entry(tracked).State = Microsoft.EntityFrameworkCore.EntityState.Deleted;

        var ex = Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        Assert.Contains("immutable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task Login(HttpClient client, string userName, string password)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName, password });
        login.EnsureSuccessStatusCode();
        var payload = await login.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(payload?.Token);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", payload.Token);
    }

    private sealed record LoginResponse(string Token);
}
