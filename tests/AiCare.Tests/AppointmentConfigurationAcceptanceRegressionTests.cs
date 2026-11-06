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
public sealed class AppointmentConfigurationAcceptanceRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BookingCapRuntimeEffectMatchesTheAuditedPersistedConfiguration(bool rejectAudit)
    {
        await factory.EnsureClinicalSeedAsync();
        var branch=Guid.NewGuid(); var person=Guid.NewGuid(); var worker=Guid.NewGuid(); var actor=$"configuration.acceptance.{Guid.NewGuid():N}";
        using var scope=factory.Services.CreateScope();
        var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
        db.Branches.Add(new(branch,TenantDefaults.OrganizationId,"Configuration acceptance branch","Test","Active"));
        db.ServiceUsers.Add(new(person,"Fictional configuration person",new DateOnly(1950,1,1),"1","Care","Contact","",RiskLevel.Low,
            "Active","Address","None","None","Private","","","Independent","Independent","Verbal","Routine","Standard",TenantDefaults.OrganizationId,branch));
        db.CareWorkers.Add(new(worker,"Configuration worker","Care","Flexible",0,0,"Valid","Compliant","Local",TenantDefaults.OrganizationId,branch));
        db.AppUsers.Add(new(Guid.NewGuid(),actor,actor+"@test.invalid",PasswordHasher.HashPassword("Admin123!"),UserRole.Administrator,true,TenantDefaults.OrganizationId,branch));
        await db.SaveChangesAsync();
        using var client=factory.CreateClient();
        var login=await client.PostAsJsonAsync("/api/auth/login",new{userName=actor,password="Admin123!",mfaCode=(string?)null});
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await login.Content.ReadFromJsonAsync<Login>())!.Token);
        const string path="/api/phase1/admin/appointment-configuration";
        object Config(int cap)=>new{branchId=branch,configuration=new{rules=new{maxWorkerVisitsPerDay=cap}}};
        Assert.Equal(HttpStatusCode.NoContent,(await client.PutAsJsonAsync(path,Config(1))).StatusCode);
        var starts=new DateTimeOffset(DateTime.UtcNow.Date.AddDays(70).AddHours(10),TimeSpan.Zero);
        object Visit(DateTimeOffset at)=>new{serviceUserId=person,careWorkerId=worker,startsAt=at,visitType="Configuration acceptance",durationMinutes=30,requiredSkills=""};
        Assert.Equal(HttpStatusCode.Created,(await client.PostAsJsonAsync("/api/phase1/visits",Visit(starts))).StatusCode);
        var constraint="ck_config_audit_"+Guid.NewGuid().ToString("N");
        if(rejectAudit)
            // NOT VALID leaves the initial audit intact while rejecting new rows.
            await db.Database.ExecuteSqlRawAsync($"""
                ALTER TABLE "AuditEvents" ADD CONSTRAINT "{constraint}"
                CHECK ("EntityId" IS DISTINCT FROM '{branch:D}'::uuid OR "Action"<>'appointments.configuration_updated') NOT VALID
                """);
        try
        {
            Assert.Equal(rejectAudit?HttpStatusCode.InternalServerError:HttpStatusCode.NoContent,(await client.PutAsJsonAsync(path,Config(2))).StatusCode);
        }
        finally
        {
            if(rejectAudit)
                await db.Database.ExecuteSqlRawAsync($"""
                    ALTER TABLE "AuditEvents" DROP CONSTRAINT "{constraint}"
                    """);
        }
        var saved=await client.GetFromJsonAsync<JsonElement>($"{path}?branchId={branch}");
        Assert.Equal(rejectAudit?1:2,saved.GetProperty("rules").GetProperty("maxWorkerVisitsPerDay").GetInt32());
        var second=await client.PostAsJsonAsync("/api/phase1/visits",Visit(starts.AddHours(2)));
        Assert.Equal(rejectAudit?HttpStatusCode.BadRequest:HttpStatusCode.Created,second.StatusCode);
        Assert.Equal(rejectAudit?1:2,await db.Visits.CountAsync(v=>v.ServiceUserId==person));
        var audits=await db.AuditEvents.AsNoTracking().Where(a=>a.EntityId==branch&&a.Action=="appointments.configuration_updated").ToListAsync();
        Assert.Equal(rejectAudit?1:2,audits.Count);
        Assert.All(audits,a=>{Assert.Equal(actor,a.Actor);Assert.Equal(branch,a.BranchId);});
    }
    private sealed record Login(string Token);
}
