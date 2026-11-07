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
public sealed class IncidentGovernanceRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task OrganizationWideIncidentWritesKeepTheIncidentsBranchAttribution()
    {
        await factory.EnsureClinicalSeedAsync();
        var incidentId=Guid.NewGuid(); var incidentBranch=Guid.NewGuid();
        using (var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Branches.Add(new(incidentBranch,TenantDefaults.OrganizationId,"Incident attribution branch","Test","Active"));
            db.Incidents.Add(new(incidentId,RegressionIds.ServiceUserId,null,"Medication error","High","Branch attribution regression","Reported",DateTimeOffset.UtcNow,TenantDefaults.OrganizationId,incidentBranch));
            await db.SaveChangesAsync();
        }
        using var admin=await Login("admin");
        var path=$"/api/phase1/incident-governance/incidents/{incidentId}";
        var investigation=new { chronology="Synthetic chronology",evidenceReviewed="Synthetic evidence",findings="Synthetic findings",rootCause="Synthetic cause",lessonsLearned="",owner="Investigation lead",complete=false,expectedRevision=0 };
        Assert.Equal(HttpStatusCode.OK,(await admin.PutAsJsonAsync(path+"/investigation",investigation)).StatusCode);
        Assert.Equal(HttpStatusCode.Created,(await admin.PostAsJsonAsync(path+"/actions",new { actionType="Corrective",detail="Synthetic corrective action",owner="Action lead",dueAt=DateTimeOffset.UtcNow.AddDays(3) })).StatusCode);
        using var verifyScope=factory.Services.CreateScope();
        var verify=verifyScope.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal(incidentBranch,await verify.Database.SqlQuery<Guid>($"""select branch_id as "Value" from incident_investigations where incident_id={incidentId}""").SingleAsync());
        var evidenceBranches=await verify.Database.SqlQuery<Guid>($"""select branch_id as "Value" from incident_capa_actions where incident_id={incidentId} union all select branch_id as "Value" from incident_governance_events where incident_id={incidentId}""").ToListAsync();
        Assert.Equal(3,evidenceBranches.Count);
        Assert.All(evidenceBranches,branch=>Assert.Equal(incidentBranch,branch));
        var audits=await verify.AuditEvents.Where(a=>a.EntityId==incidentId&&(a.Action=="incident.investigation_saved"||a.Action=="incident.capa_added")).ToListAsync();
        Assert.Equal(2,audits.Count);
        Assert.All(audits,audit=>Assert.Equal(incidentBranch,audit.BranchId));
    }

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
        var investigation = new { chronology = "Dose prepared, checked and withheld", evidenceReviewed = "MAR, prescription and witness statement", findings = "Selection error intercepted", rootCause = "Similar packaging", lessonsLearned = "Separate storage and second check", owner = "Investigation lead", complete = true, expectedRevision=0 };
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
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/phase1/incident-governance/incidents/{incidentId}/investigation", investigation)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/phase1/incident-governance/incidents/{incidentId}/actions", new { actionType="Preventive",detail="Attempted closed-case change",owner="Manager",dueAt=DateTimeOffset.UtcNow.AddDays(3) })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/phase1/incident-governance/incidents/{incidentId}/actions/{actionId}/complete", new { completionEvidence="Attempted overwrite" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/phase1/incident-governance/incidents/{incidentId}/close", new { closureSummary="Duplicate closure" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/phase1/incidents/{incidentId}", new {
            serviceUserId=RegressionIds.ServiceUserId,visitId=(Guid?)null,category="Fall",severity="Low",description="Attempted legacy overwrite"
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/phase1/incidents/{incidentId}")).StatusCode);
        var retained=await admin.GetFromJsonAsync<WorkspaceResponse>($"/api/phase1/incident-governance/incidents/{incidentId}");
        Assert.Equal("Closed",retained!.Incident.Status);
        Assert.Equal(workspace.History.Length,retained.History.Length);
    }

    [Fact]
    public async Task StaleInvestigationRevisionCannotOverwriteNewerEvidence()
    {
        await factory.EnsureClinicalSeedAsync();var incidentId=Guid.NewGuid();
        using(var scope=factory.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();db.Incidents.Add(new(incidentId,RegressionIds.ServiceUserId,null,"Fall","Medium","Revision regression","Reported",DateTimeOffset.UtcNow,TenantDefaults.OrganizationId,TenantDefaults.BranchId));await db.SaveChangesAsync();}
        using var admin=await Login("admin");var path=$"/api/phase1/incident-governance/incidents/{incidentId}/investigation";
        object Request(string findings,int revision)=>new{chronology="Timeline",evidenceReviewed="Records",findings,rootCause="Process",lessonsLearned="",owner="Lead",complete=false,expectedRevision=revision};
        var created=await admin.PutAsJsonAsync(path,Request("Initial",0));Assert.Equal(HttpStatusCode.OK,created.StatusCode);
        var revision=(await created.Content.ReadFromJsonAsync<RevisionResponse>())!.Revision;
        Assert.Equal(HttpStatusCode.OK,(await admin.PutAsJsonAsync(path,Request("Newer evidence",revision))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await admin.PutAsJsonAsync(path,Request("Stale overwrite",revision))).StatusCode);
        Assert.Contains("Newer evidence",await admin.GetStringAsync($"/api/phase1/incident-governance/incidents/{incidentId}"));
        Assert.DoesNotContain("Stale overwrite",await admin.GetStringAsync($"/api/phase1/incident-governance/incidents/{incidentId}"));
    }

    private sealed record LoginResponse(string Token);
    private sealed record CreatedResponse(Guid Id);
    private sealed record RevisionResponse(int Revision);
    private sealed record IncidentResponse(string Status);
    private sealed record WorkspaceResponse(IncidentResponse Incident, object[] Actions, object[] History);

    private async Task<HttpClient> Login(string name)
    {
        var client=factory.CreateClient();
        var response=await client.PostAsJsonAsync("/api/auth/login",new { userName=name,password="Admin123!",mfaCode=(string?)null });
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await response.Content.ReadFromJsonAsync<LoginResponse>())!.Token);
        return client;
    }
}
