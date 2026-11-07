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
public sealed class IncidentGovernanceAtomicityRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Theory]
    [InlineData("investigation", "incident.investigation_saved")]
    [InlineData("add-action", "incident.capa_added")]
    [InlineData("complete-action", "incident.capa_completed")]
    [InlineData("close", "incident.closed")]
    public async Task FailedAuditRollsBackIncidentStatusEvidenceAndHistory(string operation, string auditAction)
    {
        await factory.EnsureClinicalSeedAsync();
        var incident=Guid.NewGuid(); var actor=$"incident.atomic.{Guid.NewGuid():N}";
        using var scope=factory.Services.CreateScope();
        var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
        db.Incidents.Add(new(incident,RegressionIds.ServiceUserId,null,"Fall","High","Fictional atomic incident","Reported",DateTimeOffset.UtcNow,TenantDefaults.OrganizationId,TenantDefaults.BranchId));
        db.AppUsers.Add(new(Guid.NewGuid(),actor,actor+"@test.invalid",PasswordHasher.HashPassword("Admin123!"),UserRole.CareManager,true,TenantDefaults.OrganizationId,TenantDefaults.BranchId));
        await db.SaveChangesAsync();
        using var client=factory.CreateClient();
        var login=await client.PostAsJsonAsync("/api/auth/login",new{userName=actor,password="Admin123!",mfaCode=(string?)null});
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await login.Content.ReadFromJsonAsync<Login>())!.Token);
        var path=$"/api/phase1/incident-governance/incidents/{incident}";
        var investigation=new{chronology="Synthetic chronology",evidenceReviewed="Synthetic evidence",findings="Synthetic findings",rootCause="Synthetic root cause",lessonsLearned="Synthetic learning",owner="Test manager",complete=true,expectedRevision=0};
        var action=new{actionType="Corrective",detail="Synthetic follow-up",owner="Test manager",dueAt=DateTimeOffset.UtcNow.AddDays(7)};
        var actionId=Guid.Empty;
        if(operation=="complete-action")
        {
            var created=await client.PostAsJsonAsync(path+"/actions",action);
            Assert.Equal(HttpStatusCode.Created,created.StatusCode);
            actionId=(await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }
        if(operation=="close")
            Assert.Equal(HttpStatusCode.OK,(await client.PutAsJsonAsync(path+"/investigation",investigation)).StatusCode);
        var before=await client.GetStringAsync(path);
        var constraint="ck_incident_audit_"+Guid.NewGuid().ToString("N");
        // The fixture actor and InlineData are generated/trusted SQL values.
        await db.Database.ExecuteSqlRawAsync($"""
            ALTER TABLE "AuditEvents" ADD CONSTRAINT "{constraint}"
            CHECK ("Actor"<>'{actor}' OR "Action"<>'{auditAction}')
            """);
        try
        {
            var response=operation switch {
                "investigation"=>await client.PutAsJsonAsync(path+"/investigation",investigation),
                "add-action"=>await client.PostAsJsonAsync(path+"/actions",action),
                "complete-action"=>await client.PostAsJsonAsync($"{path}/actions/{actionId}/complete",new{completionEvidence="Synthetic completion"}),
                _=>await client.PostAsJsonAsync(path+"/close",new{closureSummary="Synthetic closure"})
            };
            Assert.Equal(HttpStatusCode.InternalServerError,response.StatusCode);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync($"""
                ALTER TABLE "AuditEvents" DROP CONSTRAINT "{constraint}"
                """);
        }
        Assert.Equal(before,await client.GetStringAsync(path));
        Assert.False(await db.AuditEvents.AnyAsync(a=>a.Actor==actor&&a.Action==auditAction));
    }
    private sealed record Login(string Token);
}
