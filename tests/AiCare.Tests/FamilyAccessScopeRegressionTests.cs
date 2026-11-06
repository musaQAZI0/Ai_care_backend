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
public sealed class FamilyAccessScopeRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Theory]
    [InlineData(UserRole.CareManager, false)]
    [InlineData(UserRole.CareCoordinator, false)]
    [InlineData(UserRole.Administrator, false)]
    [InlineData(UserRole.CareManager, true)]
    [InlineData(UserRole.CareCoordinator, true)]
    [InlineData(UserRole.Administrator, true)]
    public async Task FamilyAccessAdministrationRespectsMemberBranchAndTenant(UserRole role, bool foreignTenant)
    {
        await factory.EnsureClinicalSeedAsync();
        var organization=foreignTenant?Guid.NewGuid():TenantDefaults.OrganizationId;
        var branch=Guid.NewGuid(); var person=Guid.NewGuid(); var member=Guid.NewGuid(); var actor=$"family.scope.{Guid.NewGuid():N}";
        var name=$"Fictional representative {Guid.NewGuid():N}";
        using var scope=factory.Services.CreateScope();
        var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
        if(foreignTenant) db.Organizations.Add(new(organization,"Family scope tenant","Trial","Active"));
        db.Branches.Add(new(branch,organization,"Family scope branch","Test","Active"));
        db.ServiceUsers.Add(new(person,"Fictional family scope person",new DateOnly(1950,1,1),"1","Care","Contact","",RiskLevel.Low,
            "Active","Address","None","None","Private","","","Independent","Independent","Verbal","Routine","Standard",organization,branch));
        db.FamilyMembers.Add(new(member,person,name,$"{member:N}@test.invalid","Daughter","Full","Active",organization,branch));
        db.AppUsers.Add(new(Guid.NewGuid(),actor,actor+"@test.invalid",PasswordHasher.HashPassword("Admin123!"),role,true,TenantDefaults.OrganizationId,TenantDefaults.BranchId));
        await db.SaveChangesAsync();
        using var client=factory.CreateClient();
        var login=await client.PostAsJsonAsync("/api/auth/login",new{userName=actor,password="Admin123!",mfaCode=(string?)null});
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await login.Content.ReadFromJsonAsync<Login>())!.Token);
        var allowed=role==UserRole.Administrator&&!foreignTenant;
        var path=$"/api/phase1/family-access/{member}";
        var get=await client.GetAsync(path);
        Assert.Equal(allowed?HttpStatusCode.OK:HttpStatusCode.NotFound,get.StatusCode);
        if(!allowed) Assert.DoesNotContain(name,await get.Content.ReadAsStringAsync());
        var configured=await client.PutAsJsonAsync(path,new{
            authorityType="Authorized representative",verificationStatus="Verified",verificationReference="Synthetic authority evidence",
            permissions=new[]{"ViewCareSummary"}
        });
        Assert.Equal(allowed?HttpStatusCode.OK:HttpStatusCode.NotFound,configured.StatusCode);
        if(!allowed)
            Assert.Equal(HttpStatusCode.NotFound,(await client.PostAsync(path+"/invite",null)).StatusCode);
        foreach(var operation in new[]{"suspend","restore","revoke"})
            Assert.Equal(allowed?HttpStatusCode.OK:HttpStatusCode.NotFound,(await client.PostAsync(path+"/"+operation,null)).StatusCode);
        var grants=await db.Database.SqlQuery<int>($"""
            select count(*)::integer as "Value" from family_access_grants where family_member_id={member}
            """).SingleAsync();
        Assert.Equal(allowed?1:0,grants);
        Assert.Equal(0,await db.Database.SqlQuery<int>($"""
            select count(*)::integer as "Value" from family_portal_invitations where family_member_id={member}
            """).SingleAsync());
        if(allowed)
        {
            Assert.Equal(branch,await db.Database.SqlQuery<Guid>($"""
                select branch_id as "Value" from family_access_grants where family_member_id={member}
                """).SingleAsync());
            Assert.True(await db.AuditEvents.AnyAsync(a=>a.Actor==actor&&a.EntityId==member&&a.Action=="family.access_configured"&&a.BranchId==branch));
        }
        else Assert.False(await db.AuditEvents.AnyAsync(a=>a.EntityId==member));
    }
    private sealed record Login(string Token);
}
