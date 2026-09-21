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
public sealed class EmploymentHistoryRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task VersionsAreImmutableScopedAndConcurrentEditsCannotOverwrite()
    {
        await factory.EnsureClinicalSeedAsync();
        var worker=Guid.NewGuid();
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.CareWorkers.Add(new CareWorker(worker,"Versioned worker","Care","Available",0,0,"Valid","Compliant","Local",TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            await db.SaveChangesAsync();
        }
        var manager=await Client(UserRole.CareManager,TenantDefaults.BranchId);
        var foreign=await Client(UserRole.CareManager,Guid.NewGuid());
        var coordinator=await Client(UserRole.CareCoordinator,TenantDefaults.BranchId);
        var url=$"/api/phase1/workforce-lifecycle/care-workers/{worker}/employment";
        Assert.Equal(HttpStatusCode.BadRequest,(await manager.PutAsJsonAsync(url,Request(null,"Initial"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await manager.PutAsJsonAsync(url,Request(0,"  "))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await coordinator.PutAsJsonAsync(url,Request(0,"Initial"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await foreign.PutAsJsonAsync(url,Request(0,"Initial"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await manager.PutAsJsonAsync(url,Request(0,"Initial", "Care worker"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await manager.PutAsJsonAsync(url,Request(0,"Stale"))).StatusCode);
        var competing=await Task.WhenAll(manager.PutAsJsonAsync(url,Request(1,"Promotion A","Senior A")),manager.PutAsJsonAsync(url,Request(1,"Promotion B","Senior B")));
        Assert.Single(competing,x=>x.StatusCode==HttpStatusCode.OK);
        Assert.Single(competing,x=>x.StatusCode==HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.Forbidden,(await coordinator.GetAsync(url+"/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await foreign.GetAsync(url+"/history")).StatusCode);
        var history=await manager.GetFromJsonAsync<JsonElement[]>(url+"/history");
        Assert.Equal(2,history!.Length);
        Assert.Equal(2,history[0].GetProperty("revision").GetInt32());
        Assert.Equal("Care worker",history[1].GetProperty("snapshot").GetProperty("job_title").GetString());
        Assert.Equal("Initial",history[1].GetProperty("changeReason").GetString());
        Assert.False(string.IsNullOrWhiteSpace(history[0].GetProperty("actor").GetString()));
        using var verify=factory.Services.CreateScope();var db0=verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal(2,await db0.AuditEvents.CountAsync(x=>x.EntityId==worker&&x.Action=="employment.updated"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>db0.Database.ExecuteSqlInterpolatedAsync($"update worker_employment_history set change_reason='Tampered' where care_worker_id={worker}"));

        // A failed audit insert must roll back the profile revision, snapshot and lifecycle event.
        await db0.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_employment_audit_test() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW."Action"='employment.updated' THEN RAISE EXCEPTION 'Audit failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER fail_employment_audit_test BEFORE INSERT ON "AuditEvents" FOR EACH ROW EXECUTE FUNCTION fail_employment_audit_test();
            """);
        try
        {
            Assert.Equal(HttpStatusCode.InternalServerError,(await manager.PutAsJsonAsync(url,Request(2,"Should roll back"))).StatusCode);
            Assert.Equal(2,(await manager.GetFromJsonAsync<JsonElement[]>(url+"/history"))!.Length);
            var workspace=await manager.GetFromJsonAsync<JsonElement>($"/api/phase1/workforce-lifecycle/care-workers/{worker}");
            Assert.Equal(2,workspace.GetProperty("employment").GetProperty("revision").GetInt32());
            Assert.Equal(2,workspace.GetProperty("events").EnumerateArray().Count(x=>x.GetProperty("eventType").GetString()=="employment.updated"));
        }
        finally { await db0.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_employment_audit_test ON \"AuditEvents\"; DROP FUNCTION fail_employment_audit_test();"); }
    }
    private static object Request(int? revision,string reason,string job="Care worker") => new {
        expectedRevision=revision,changeReason=reason,recruitmentStatus="Cleared",employmentStatus="Employed",
        contractType="Permanent",jobTitle=job,contractedWeeklyMinutes=2100,startDate=new DateOnly(2026,1,1),
        endDate=(DateOnly?)null,lineManager="Manager",statusReason=""
    };
    private async Task<HttpClient> Client(UserRole role,Guid branch)
    {
        var name=$"employment.{Guid.NewGuid():N}";
        using var scope=factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
        db.AppUsers.Add(new AppUser(Guid.NewGuid(),name,$"{name}@aicare.local",PasswordHasher.HashPassword("Admin123!"),role,true,TenantDefaults.OrganizationId,branch,null,null));await db.SaveChangesAsync();
        var client=factory.CreateClient();var login=await client.PostAsJsonAsync("/api/auth/login",new {userName=name,password="Admin123!"});login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString());return client;
    }
}
