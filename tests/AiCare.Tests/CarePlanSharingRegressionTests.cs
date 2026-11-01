using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiCare.Api;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace AiCare.Tests;
public sealed partial class MeasurableCareGoalsRegressionTests
{
    [Fact]
    public async Task SharingRegisterPinsSelectedContentAndRevocationBlocksFurtherRetrieval()
    {
        var f=await Setup();var root=$"/api/phase1/care-plans/{f.Plan}/emergency-sections/shares";
        var request=new CarePlanShareRequest(Guid.NewGuid(),1,"Person's representative","Authorised representative","Review agreed care","Consent record reviewed with person",["PersonalCare"],DateTimeOffset.UtcNow.AddDays(7));
        await Expect(await f.Manager.PostAsJsonAsync(root,request),HttpStatusCode.Conflict);
        await Activate(f,f.Plan,seed:true);request=request with{ExpectedPlanRevision=await Revision(f.Manager,f.Plan)};
        await Expect(await f.OtherBranch.GetAsync(root),HttpStatusCode.NotFound);
        await Expect(await (await ForeignManager()).PostAsJsonAsync(root,request),HttpStatusCode.NotFound);
        await Expect(await f.Worker.GetAsync(root),HttpStatusCode.Forbidden);
        await Expect(await f.Manager.PostAsJsonAsync(root,request with{ExpiresAt=DateTimeOffset.UtcNow.AddDays(-1)}),HttpStatusCode.BadRequest);
        var attempts=await Task.WhenAll(f.Manager.PostAsJsonAsync(root,request),f.Manager.PostAsJsonAsync(root,request));
        Assert.Single(attempts,r=>r.StatusCode==HttpStatusCode.Created);Assert.Single(attempts,r=>r.StatusCode==HttpStatusCode.OK);
        await Expect(await f.Manager.PostAsJsonAsync(root,request with{Purpose="Changed replay"}),HttpStatusCode.Conflict);
        var copyPath=$"{root}/{request.RequestId}/copy";
        var response=await f.Manager.PostAsync(copyPath,null);await Expect(response,HttpStatusCode.OK);
        var html=(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("html").GetString()!;
        Assert.Contains("Selected care-plan copy",html);Assert.Contains("Agreed care",html);Assert.DoesNotContain("Agreed nutrition",html);
        await Expect(await f.Manager.PostAsync(copyPath,null),HttpStatusCode.OK);
        var revocation=new RevokePlanShareRequest(Guid.NewGuid(),"Authority withdrawn by the person");
        var revokePath=$"{root}/{request.RequestId}/revoke";
        await Expect(await f.Manager.PostAsJsonAsync(revokePath,revocation),HttpStatusCode.OK);
        await Expect(await f.Manager.PostAsJsonAsync(revokePath,revocation),HttpStatusCode.OK);
        await Expect(await f.Manager.PostAsync(copyPath,null),HttpStatusCode.Conflict);
        var view=await f.Manager.GetFromJsonAsync<JsonElement>(root);
        Assert.Equal("Revoked",Assert.Single(view.GetProperty("shares").EnumerateArray()).GetProperty("status").GetString());
        using var scope=factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal(2,await db.AuditEvents.CountAsync(e=>e.EntityId==request.RequestId&&e.Action=="care_plan.share_copy_downloaded"));
        await Assert.ThrowsAnyAsync<Exception>(()=>db.Database.ExecuteSqlRawAsync("delete from care_plan_shares where id={0}",request.RequestId));
        await Assert.ThrowsAnyAsync<Exception>(()=>db.Database.ExecuteSqlRawAsync("update care_plan_share_revocations set reason='overwrite' where id={0}",revocation.RequestId));
    }
}
