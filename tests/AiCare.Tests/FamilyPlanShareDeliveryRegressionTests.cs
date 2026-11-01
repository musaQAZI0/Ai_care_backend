using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiCare.Api;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace AiCare.Tests;
public sealed partial class MeasurableCareGoalsRegressionTests
{
    [Theory]
    [InlineData("ConsentWithdrawn")]
    [InlineData("AuthorityChanged")]
    [InlineData("PermissionRemoved")]
    public async Task FamilyShareAccessRechecksConsentAuthorityAndRecipientAndRecordsReceipt(string change)
    {
        var f=await Setup();await Activate(f,f.Plan,seed:true);
        var family=Guid.NewGuid();var other=Guid.NewGuid();var name=$"share.family.{Guid.NewGuid():N}";var otherName=name+"other";
        using(var scope=factory.Services.CreateScope()){
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            foreach(var entry in new[]{(family,name),(other,otherName)}){
                db.FamilyMembers.Add(new FamilyMember(entry.Item1,f.Person,"Verified recipient",entry.Item2+"@test.invalid","Representative","Care plan","Active",TenantDefaults.OrganizationId,TenantDefaults.BranchId));
                db.AppUsers.Add(new AppUser(Guid.NewGuid(),entry.Item2,entry.Item2+"@test.invalid",PasswordHasher.HashPassword("Admin123!"),UserRole.FamilyMember,true,TenantDefaults.OrganizationId,TenantDefaults.BranchId,null,entry.Item1));
            }await db.SaveChangesAsync();
        }
        foreach(var id in new[]{family,other})await Expect(await f.Manager.PutAsJsonAsync($"/api/phase1/family-access/{id}",new{authorityType="Authorized representative",verificationStatus="Verified",verificationReference="Authority checked",validFrom=DateTimeOffset.UtcNow.AddDays(-1),validUntil=DateTimeOffset.UtcNow.AddDays(30),permissions=new[]{"ViewCarePlan"}}),HttpStatusCode.OK);
        var portal=await Client(name);var otherPortal=await Client(otherName);
        var root=$"/api/phase1/care-plans/{f.Plan}/emergency-sections/shares";
        var share=new CarePlanShareRequest(Guid.NewGuid(),await Revision(f.Manager,f.Plan),"Verified recipient","Representative","Review care","Consent verified",["PersonalCare"],DateTimeOffset.UtcNow.AddDays(7));
        await Expect(await f.Manager.PostAsJsonAsync(root,share),HttpStatusCode.Created);
        var familyRoot=$"/api/family/service-users/{f.Person}/plan-shares";
        Assert.Empty((await portal.GetFromJsonAsync<JsonElement>(familyRoot)).EnumerateArray());
        var options=await f.Manager.GetFromJsonAsync<JsonElement>(root+"/recipient-options");
        var consent=options.GetProperty("consents").EnumerateArray().First().GetProperty("id").GetGuid();
        var grant=options.GetProperty("people").EnumerateArray().Single(p=>p.GetProperty("id").GetGuid()==family).GetProperty("grant_revision").GetInt64();
        var binding=new BindShareRecipientRequest(Guid.NewGuid(),family,consent,grant,"Confirmed this recipient, purpose and selected sections are covered");
        var bindPath=$"{root}/{share.RequestId}/recipient";
        await Expect(await f.Manager.PostAsJsonAsync(bindPath,binding),HttpStatusCode.OK);
        await Expect(await f.Manager.PostAsJsonAsync(bindPath,binding),HttpStatusCode.OK);
        Assert.Single((await portal.GetFromJsonAsync<JsonElement>(familyRoot)).EnumerateArray());
        Assert.Empty((await otherPortal.GetFromJsonAsync<JsonElement>(familyRoot)).EnumerateArray());
        await Expect(await otherPortal.PostAsync($"{familyRoot}/{share.RequestId}/copy",null),HttpStatusCode.NotFound);
        await Expect(await portal.PostAsync($"{familyRoot}/{share.RequestId}/copy",null),HttpStatusCode.OK);
        await Expect(await portal.PostAsync($"{familyRoot}/{share.RequestId}/acknowledge",null),HttpStatusCode.OK);
        await Expect(await portal.PostAsync($"{familyRoot}/{share.RequestId}/acknowledge",null),HttpStatusCode.OK);
        using(var scope=factory.Services.CreateScope()){
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            Assert.Single(await db.AuditEvents.Where(a=>a.EntityId==share.RequestId&&a.Action=="family.plan_share_acknowledged").ToListAsync());
            if(change=="ConsentWithdrawn")await db.Database.ExecuteSqlRawAsync("update consent_records set status='Withdrawn',withdrawn_at=now(),withdrawal_reason='Person withdrew sharing consent' where id={0}",consent);
            else if(change=="AuthorityChanged")await db.Database.ExecuteSqlRawAsync("update family_access_grants set revision=revision+1 where family_member_id={0}",family);
            else await db.Database.ExecuteSqlRawAsync("delete from family_access_permissions where permission='ViewCarePlan' and access_grant_id in (select id from family_access_grants where family_member_id={0})",family);
        }
        if(change=="PermissionRemoved")await Expect(await portal.GetAsync(familyRoot),HttpStatusCode.Forbidden);
        else Assert.Empty((await portal.GetFromJsonAsync<JsonElement>(familyRoot)).EnumerateArray());
        var denied=change=="PermissionRemoved"?HttpStatusCode.Forbidden:HttpStatusCode.NotFound;
        await Expect(await portal.PostAsync($"{familyRoot}/{share.RequestId}/copy",null),denied);
        await Expect(await portal.PostAsync($"{familyRoot}/{share.RequestId}/acknowledge",null),denied);
        await Expect(await f.Manager.PostAsync($"{root}/{share.RequestId}/copy",null),HttpStatusCode.Conflict);
    }
}
