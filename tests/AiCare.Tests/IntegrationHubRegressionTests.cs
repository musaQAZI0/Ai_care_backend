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
public sealed class IntegrationHubRegressionTests(PostgresRegressionFactory factory):IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task AdminCanCreateConnectorJobsWebhookFailureRetryAndDashboardWhileCareManagerCannot()
    {
        await factory.EnsureClinicalSeedAsync(); var managerName=$"integration.manager.{Guid.NewGuid():N}";
        using(var scope=factory.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();db.AppUsers.Add(new AppUser(Guid.NewGuid(),managerName,$"{managerName}@aicare.local",PasswordHasher.HashPassword("Admin123!"),UserRole.CareManager,true,TenantDefaults.OrganizationId,TenantDefaults.BranchId));await db.SaveChangesAsync();}
        var admin=await Login("admin");var manager=await Login(managerName);
        Assert.Equal(HttpStatusCode.Forbidden,(await manager.GetAsync("/api/phase1/integrations/dashboard")).StatusCode);
        var connectorResponse=await admin.PostAsJsonAsync("/api/phase1/integrations/connectors",new{name="Regression payroll",connectorType="Payroll",endpointUrl="https://example.invalid/api",configuration=new{mode="test"}});
        Assert.Equal(HttpStatusCode.Created,connectorResponse.StatusCode);var connectorId=(await connectorResponse.Content.ReadFromJsonAsync<Created>())!.Id;
        Assert.Equal(HttpStatusCode.Created,(await admin.PostAsJsonAsync("/api/phase1/integrations/jobs/import",new{connectorId,resourceType="Workers",options=new{scope="branch"}})).StatusCode);
        Assert.Equal(HttpStatusCode.Created,(await admin.PostAsJsonAsync("/api/phase1/integrations/jobs/export",new{connectorId,resourceType="Payroll",simulateFailure=true})).StatusCode);
        var webhook=await admin.PostAsJsonAsync($"/api/phase1/integrations/webhooks/{connectorId}",new{eventType="worker.updated",externalEventId=$"evt-{Guid.NewGuid():N}",payload=new{worker="regression"}});
        Assert.Equal(HttpStatusCode.Accepted,webhook.StatusCode);
        var failures=await admin.GetFromJsonAsync<List<Failure>>("/api/phase1/integrations/failures");var failure=Assert.Single(failures!,x=>x.Status=="Pending"&&x.ConnectorId==connectorId);
        Assert.Equal(HttpStatusCode.OK,(await admin.PostAsync($"/api/phase1/integrations/failures/{failure.Id}/retry",null)).StatusCode);
        var dashboard=await admin.GetStringAsync("/api/phase1/integrations/dashboard");Assert.Contains("connectorItems",dashboard);Assert.Contains("webhookEvents",dashboard);Assert.Contains("failedJobs",dashboard);
        using var verify=factory.Services.CreateScope();var verifyDb=verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="connector.created"&&x.EntityId==connectorId));Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="sync_failure.retried"&&x.EntityId==failure.Id));
    }
    private async Task<HttpClient> Login(string name){var client=factory.CreateClient();var response=await client.PostAsJsonAsync("/api/auth/login",new{userName=name,password="Admin123!",mfaCode=(string?)null});response.EnsureSuccessStatusCode();client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await response.Content.ReadFromJsonAsync<LoginResponse>())!.Token);return client;}
    private sealed record LoginResponse(string Token);private sealed record Created(Guid Id);private sealed record Failure(Guid Id,Guid? ConnectorId,string Status);
}
