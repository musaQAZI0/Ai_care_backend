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
public sealed class SafeguardingReopenRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task ReopeningRequiresAuthorityClosedStateReasonAndPreservesClosureAcrossConcurrentRequests()
    {
        await factory.EnsureClinicalSeedAsync();
        var admin = await Login("admin");
        var manager = await Staff(UserRole.CareManager, TenantDefaults.BranchId);
        var coordinator = await Staff(UserRole.CareCoordinator, TenantDefaults.BranchId);
        var foreignManager = await Staff(UserRole.CareManager, Guid.NewGuid());
        var created = await admin.PostAsJsonAsync("/api/phase1/safeguarding/cases", new {
            serviceUserId=RegressionIds.ServiceUserId, category="Safety", concern="Reopen regression", riskLevel="Low"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var url = $"/api/phase1/safeguarding/cases/{id}";
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync(url+"/reopen", new {reason="New evidence"})).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(url, new {status="Closed",closureSummary="Original closure evidence"})).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await coordinator.PostAsJsonAsync(url+"/reopen", new {reason="New evidence"})).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreignManager.PostAsJsonAsync(url+"/reopen", new {reason="New evidence"})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync(url+"/reopen", new {reason="  "})).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync(url, new {status="Open"})).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync(url, new {status="Closed",closureSummary="Overwrite"})).StatusCode);
        var results = await Task.WhenAll(
            manager.PostAsJsonAsync(url+"/reopen", new {reason="New evidence from review"}),
            admin.PostAsJsonAsync(url+"/reopen", new {reason="New evidence from review"}));
        Assert.Single(results, r=>r.StatusCode==HttpStatusCode.NoContent);
        Assert.Single(results, r=>r.StatusCode==HttpStatusCode.Conflict);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal(1, await db.AuditEvents.CountAsync(x=>x.EntityId==id && x.Action=="safeguarding.case_reopened"));
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText="select detail from safeguarding_case_events where case_id=@id and event_type='Reopened'";
        var parameter=command.CreateParameter();parameter.ParameterName="id";parameter.Value=id;command.Parameters.Add(parameter);
        var snapshot=JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!).RootElement;
        Assert.Equal("Original closure evidence",snapshot.GetProperty("previousClosureSummary").GetString());
        Assert.Equal("Closed",snapshot.GetProperty("previousStatus").GetString());
        Assert.NotEqual(JsonValueKind.Null,snapshot.GetProperty("previousClosedAt").ValueKind);
        command.CommandText="select count(*) from safeguarding_case_events where case_id=@id and event_type='Closed'";
        Assert.Equal(1L,Convert.ToInt64(await command.ExecuteScalarAsync()));
        Assert.Equal(HttpStatusCode.OK,(await admin.PutAsJsonAsync(url,new {status="Closed",closureSummary="Second closure"})).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await admin.PostAsJsonAsync(url+"/reopen",new {reason="Second review"})).StatusCode);
        command.CommandText="select count(*) from safeguarding_case_events where case_id=@id and event_type='Reopened'";
        Assert.Equal(2L,Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task AuditFailureRollsBackReopenAndHistory()
    {
        await factory.EnsureClinicalSeedAsync();
        var admin=await Login("admin");
        var created=await admin.PostAsJsonAsync("/api/phase1/safeguarding/cases",new {serviceUserId=RegressionIds.ServiceUserId,category="Safety",concern="Atomicity regression",riskLevel="Low"});
        created.EnsureSuccessStatusCode();
        var id=(await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var url=$"/api/phase1/safeguarding/cases/{id}";
        (await admin.PutAsJsonAsync(url,new {status="Closed",closureSummary="Keep this closure"})).EnsureSuccessStatusCode();
        using var scope=factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_reopen_audit_test() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."Action"='safeguarding.case_reopened' THEN
                    RAISE EXCEPTION 'Simulated audit failure';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER fail_reopen_audit_test BEFORE INSERT ON "AuditEvents"
            FOR EACH ROW EXECUTE FUNCTION fail_reopen_audit_test();
            """);
        try
        {
            Assert.Equal(HttpStatusCode.InternalServerError,(await admin.PostAsJsonAsync(url+"/reopen",new {reason="Atomicity check"})).StatusCode);
            var cases=await admin.GetFromJsonAsync<JsonElement>("/api/phase1/safeguarding/cases");
            var row=cases.EnumerateArray().Single(x=>x.GetProperty("id").GetGuid()==id);
            Assert.Equal("Closed",row.GetProperty("status").GetString());
            Assert.Equal("Keep this closure",row.GetProperty("closureSummary").GetString());
            Assert.False(await db.AuditEvents.AnyAsync(x=>x.EntityId==id&&x.Action=="safeguarding.case_reopened"));
            await db.Database.OpenConnectionAsync();
            await using var command=db.Database.GetDbConnection().CreateCommand();
            command.CommandText="select count(*) from safeguarding_case_events where case_id=@id and event_type='Reopened'";
            var parameter=command.CreateParameter();parameter.ParameterName="id";parameter.Value=id;command.Parameters.Add(parameter);
            Assert.Equal(0L,Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_reopen_audit_test ON \"AuditEvents\"; DROP FUNCTION fail_reopen_audit_test();");
        }
    }

    private async Task<HttpClient> Staff(UserRole role,Guid branch)
    {
        var name=$"reopen.{Guid.NewGuid():N}";
        using var scope=factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
        db.AppUsers.Add(new AppUser(Guid.NewGuid(),name,$"{name}@aicare.local",PasswordHasher.HashPassword("Admin123!"),role,true,TenantDefaults.OrganizationId,branch,null,null));
        await db.SaveChangesAsync();return await Login(name);
    }
    private async Task<HttpClient> Login(string name)
    {
        var client=factory.CreateClient();var response=await client.PostAsJsonAsync("/api/auth/login",new {userName=name,password="Admin123!"});
        response.EnsureSuccessStatusCode();client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString());return client;
    }
}
