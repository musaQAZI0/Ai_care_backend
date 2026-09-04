using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed class IncidentGovernanceRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task InvestigationCapaAuthorizationAndClosureAreGoverned()
    {
        await factory.EnsureClinicalSeedAsync();
        var incidentId = Guid.NewGuid();
        var coordinatorName = $"incident.coordinator.{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Incidents.Add(new Incident(incidentId, RegressionIds.ServiceUserId, null, "Medication error", "High", "Governed incident regression", "Reported", DateTimeOffset.UtcNow, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.AppUsers.Add(new AppUser(Guid.NewGuid(), coordinatorName, $"{coordinatorName}@aicare.local", PasswordHasher.HashPassword("Admin123!"), UserRole.CareCoordinator, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId, null, null));
            await db.SaveChangesAsync();
        }

        async Task<HttpClient> Login(string name)
        {
            var client = factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/auth/login", new { userName = name, password = "Admin123!", mfaCode = (string?)null });
            response.EnsureSuccessStatusCode();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<LoginResponse>())!.Token);
            return client;
        }

        var admin = await Login("admin");
        var coordinator = await Login(coordinatorName);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/phase1/incidents/{incidentId}/investigate", new { outcome = "Legacy", actionPlan = "Bypass", closeIncident = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/phase1/incident-governance/incidents/{incidentId}/close", new { closureSummary = "Too early" })).StatusCode);
        var investigation = new { chronology = "Dose prepared, checked and withheld", evidenceReviewed = "MAR, prescription and witness statement", findings = "Selection error intercepted", rootCause = "Similar packaging", lessonsLearned = "Separate storage and second check", owner = "Investigation lead", complete = true };
        Assert.Equal(HttpStatusCode.Forbidden, (await coordinator.PutAsJsonAsync($"/api/phase1/incident-governance/incidents/{incidentId}/investigation", investigation)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/phase1/incident-governance/incidents/{incidentId}/investigation", investigation)).StatusCode);
        var action = await admin.PostAsJsonAsync($"/api/phase1/incident-governance/incidents/{incidentId}/actions", new { actionType = "Corrective", detail = "Separate look-alike medicines", owner = "Medication lead", dueAt = DateTimeOffset.UtcNow.AddDays(2) });
        Assert.Equal(HttpStatusCode.Created, action.StatusCode);
        var actionId = (await action.Content.ReadFromJsonAsync<CreatedResponse>())!.Id;
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/phase1/incident-governance/incidents/{incidentId}/close", new { closureSummary = "Action still open" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/phase1/incident-governance/incidents/{incidentId}/actions/{actionId}/complete", new { completionEvidence = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await coordinator.PostAsJsonAsync($"/api/phase1/incident-governance/incidents/{incidentId}/actions/{actionId}/complete", new { completionEvidence = "Photographic storage check reviewed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await coordinator.PostAsJsonAsync($"/api/phase1/incident-governance/incidents/{incidentId}/close", new { closureSummary = "Coordinator attempted closure" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/phase1/incident-governance/incidents/{incidentId}/close", new { closureSummary = "Investigation and corrective action reviewed" })).StatusCode);
        var workspace = await admin.GetFromJsonAsync<WorkspaceResponse>($"/api/phase1/incident-governance/incidents/{incidentId}");
        Assert.Equal("Closed", workspace!.Incident.Status);
        Assert.Single(workspace.Actions);
        Assert.True(workspace.History.Length >= 3);
    }

    private sealed record LoginResponse(string Token);
    private sealed record CreatedResponse(Guid Id);
    private sealed record IncidentResponse(string Status);
    private sealed record WorkspaceResponse(IncidentResponse Incident, object[] Actions, object[] History);
}
