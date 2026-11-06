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
public sealed class FamilyAccessAtomicityRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Theory]
    [InlineData("suspend", "suspended", false)]
    [InlineData("suspend", "suspended", true)]
    [InlineData("restore", "active", false)]
    [InlineData("restore", "active", true)]
    [InlineData("revoke", "revoked", false)]
    [InlineData("revoke", "revoked", true)]
    public async Task StatusGrantAccountInvitationAndAuditCommitTogether(string operation, string auditStatus, bool rejectAudit)
    {
        await factory.EnsureClinicalSeedAsync();
        var person=Guid.NewGuid(); var branch=Guid.NewGuid(); var member=Guid.NewGuid(); var account=Guid.NewGuid();
        var manager=Guid.NewGuid(); var invitation=Guid.NewGuid(); var actor=$"family.atomic.{Guid.NewGuid():N}";
        using var scope=factory.Services.CreateScope();
        var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
        db.Branches.Add(new(branch,TenantDefaults.OrganizationId,"Family atomic branch","Test","Active"));
        db.ServiceUsers.Add(new(person,"Fictional family atomic person",new DateOnly(1950,1,1),"1","Care","Contact","",RiskLevel.Low,
            "Active","Address","None","None","Private","","","Independent","Independent","Verbal","Routine","Standard",TenantDefaults.OrganizationId,branch));
        db.FamilyMembers.Add(new(member,person,"Fictional representative",$"{member:N}@test.invalid","Daughter","Full","Active",TenantDefaults.OrganizationId,branch));
        db.AppUsers.Add(new(manager,actor,actor+"@test.invalid",PasswordHasher.HashPassword("Admin123!"),UserRole.CareManager,true,TenantDefaults.OrganizationId,branch));
        db.AppUsers.Add(new(account,$"family.account.{account:N}",$"{account:N}@test.invalid",PasswordHasher.HashPassword("Admin123!"),UserRole.FamilyMember,true,TenantDefaults.OrganizationId,branch,null,member));
        await db.SaveChangesAsync();
        using var client=factory.CreateClient();
        var login=await client.PostAsJsonAsync("/api/auth/login",new{userName=actor,password="Admin123!",mfaCode=(string?)null});
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await login.Content.ReadFromJsonAsync<Login>())!.Token);
        var path=$"/api/phase1/family-access/{member}";
        var configure=await client.PutAsJsonAsync(path,new{authorityType="Authorized representative",verificationStatus="Verified",verificationReference="Synthetic authority evidence",permissions=new[]{"ViewCareSummary"}});
        Assert.Equal(HttpStatusCode.OK,configure.StatusCode);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into family_portal_invitations(id,family_member_id,token_hash,email,status,expires_at,created_by_user_id,created_by,organization_id,branch_id)
            values({invitation},{member},{$"{Guid.NewGuid():N}{Guid.NewGuid():N}"},{$"{member:N}@test.invalid"},{"Sent"},{DateTimeOffset.UtcNow.AddDays(7)},{manager},{actor},{TenantDefaults.OrganizationId},{branch})
            """);
        if(operation=="restore") Assert.Equal(HttpStatusCode.OK,(await client.PostAsync(path+"/suspend",null)).StatusCode);
        var before=await client.GetStringAsync(path);
        var beforeActive=await db.AppUsers.AsNoTracking().Where(u=>u.Id==account).Select(u=>u.IsActive).SingleAsync();
        var action="family.access_"+auditStatus;
        var constraint="ck_family_audit_"+Guid.NewGuid().ToString("N");
        if(rejectAudit)
            await db.Database.ExecuteSqlRawAsync($"""
                ALTER TABLE "AuditEvents" ADD CONSTRAINT "{constraint}"
                CHECK ("EntityId" IS DISTINCT FROM '{member:D}'::uuid OR "Action"<>'{action}')
                """);
        try
        {
            var response=await client.PostAsync(path+"/"+operation,null);
            Assert.Equal(rejectAudit?HttpStatusCode.InternalServerError:HttpStatusCode.OK,response.StatusCode);
        }
        finally
        {
            if(rejectAudit)
                await db.Database.ExecuteSqlRawAsync($"""
                    ALTER TABLE "AuditEvents" DROP CONSTRAINT "{constraint}"
                    """);
        }
        if(rejectAudit) Assert.Equal(before,await client.GetStringAsync(path));
        var active=await db.AppUsers.AsNoTracking().Where(u=>u.Id==account).Select(u=>u.IsActive).SingleAsync();
        Assert.Equal(rejectAudit?beforeActive:operation=="restore",active);
        Assert.Equal(!rejectAudit&&operation=="revoke"?"Revoked":"Sent",await db.Database.SqlQuery<string>($"""
            select status as "Value" from family_portal_invitations where id={invitation}
            """).SingleAsync());
        var audit=await db.AuditEvents.AsNoTracking().Where(a=>a.EntityId==member&&a.Action==action).ToListAsync();
        Assert.Equal(rejectAudit?0:1,audit.Count);
        if(!rejectAudit)
        {
            Assert.Equal(branch,audit[0].BranchId);
            Assert.Equal(actor,audit[0].Actor);
            Assert.Equal(auditStatus=="active"?"Active":auditStatus=="suspended"?"Suspended":"Revoked",await db.Database.SqlQuery<string>($"""
                select access_status as "Value" from family_access_grants where family_member_id={member}
                """).SingleAsync());
        }
    }
    private sealed record Login(string Token);
}
