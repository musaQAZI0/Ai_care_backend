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
    [Fact]
    public async Task DocumentCompletenessRequiresVerifiedCurrentMetadataAndPreservesRequirementAndRemediationVersions()
    {
        var f=await Setup();var root=$"/api/phase1/service-users/{f.Person}/onboarding/document-requirements";
        var rule=new DocumentRequirementRequest(Guid.NewGuid(),0,"Current support agreement","Support agreement",true,true,"Agreed onboarding evidence");
        await Expect(await f.OtherBranch.GetAsync(root),HttpStatusCode.NotFound);
        await Expect(await (await ForeignManager()).PostAsJsonAsync(root,rule),HttpStatusCode.NotFound);
        await Expect(await f.Worker.GetAsync(root),HttpStatusCode.Forbidden);
        var attempts=await Task.WhenAll(f.Manager.PostAsJsonAsync(root,rule),f.Manager.PostAsJsonAsync(root,rule));
        Assert.Single(attempts,r=>r.StatusCode==HttpStatusCode.Created);Assert.Single(attempts,r=>r.StatusCode==HttpStatusCode.OK);
        await Expect(await f.Manager.PostAsJsonAsync(root,rule with{Reason="Changed replay"}),HttpStatusCode.Conflict);
        var document=Guid.NewGuid();
        using(var scope=factory.Services.CreateScope()){
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Documents.Add(new DocumentItem(document,f.Person,"agreement.pdf","Support agreement","local://regression-agreement","Regression",DateTimeOffset.UtcNow,TenantDefaults.OrganizationId,TenantDefaults.BranchId));await db.SaveChangesAsync();
        }
        async Task<bool> Complete(){var state=await f.Manager.GetFromJsonAsync<JsonElement>(root);return Assert.Single(state.GetProperty("checks").EnumerateArray()).GetProperty("complete").GetBoolean();}
        Assert.False(await Complete());
        var path=$"{root}/documents/{document}/verification";
        var verification=new DocumentVerificationRequest(Guid.NewGuid(),0,"Verified","Reviewed the uploaded agreement");
        await Expect(await f.Manager.PostAsJsonAsync(path,verification),HttpStatusCode.Created);
        Assert.False(await Complete()); // This requirement specifically requires an expiry.
        verification=verification with{RequestId=Guid.NewGuid(),ExpectedRevision=1,ValidUntil=DateTimeOffset.UtcNow.AddDays(30)};
        await Expect(await f.Manager.PostAsJsonAsync(path,verification with{ValidUntil=DateTimeOffset.UtcNow.AddDays(-1)}),HttpStatusCode.BadRequest);
        await Expect(await f.Manager.PostAsJsonAsync(path,verification),HttpStatusCode.Created);Assert.True(await Complete());
        var remediation=$"/api/phase1/service-users/{f.Person}/onboarding/remediation/document:{rule.RequestId}";
        var resolved=new CompletenessEventRequest(Guid.NewGuid(),0,"Resolved",f.Owner,DateTimeOffset.UtcNow.AddDays(1),"Verified the current agreement");
        await Expect(await f.Manager.PostAsJsonAsync(remediation,resolved),HttpStatusCode.Created);
        using(var scope=factory.Services.CreateScope()){
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            await db.Database.ExecuteSqlRawAsync("update \"Documents\" set \"FileName\"='changed-agreement.pdf' where \"Id\"={0}",document);
        }
        Assert.False(await Complete());
        await Expect(await f.Manager.PostAsJsonAsync(remediation,resolved with{RequestId=Guid.NewGuid(),ExpectedRevision=1}),HttpStatusCode.Conflict);
        await Expect(await f.Manager.PostAsJsonAsync(path,verification),HttpStatusCode.OK);Assert.False(await Complete()); // Replay does not verify changed metadata.
        await Expect(await f.Manager.PostAsJsonAsync(path,verification with{RequestId=Guid.NewGuid(),ExpectedRevision=2}),HttpStatusCode.Created);Assert.True(await Complete());
        await Expect(await f.Manager.DeleteAsync($"/api/phase1/documents/{document}?reason=Superseded%20after%20metadata%20change"),HttpStatusCode.NoContent);Assert.False(await Complete());
        await Expect(await f.Manager.PostAsJsonAsync(path,verification with{RequestId=Guid.NewGuid(),ExpectedRevision=3}),HttpStatusCode.NotFound);
        var disable=rule with{RequestId=Guid.NewGuid(),RootId=rule.RequestId,ExpectedVersion=1,Enabled=false,Reason="Requirement retired by manager"};
        await Expect(await f.Manager.PostAsJsonAsync(root,disable),HttpStatusCode.Created);
        var state=await f.Manager.GetFromJsonAsync<JsonElement>(root);Assert.Empty(state.GetProperty("checks").EnumerateArray());
        Assert.Equal(2,state.GetProperty("requirements").GetArrayLength());Assert.Equal(3,state.GetProperty("verificationHistory").GetArrayLength());
        await Expect(await f.Manager.PostAsJsonAsync(root,disable with{RequestId=Guid.NewGuid()}),HttpStatusCode.Conflict);
        using(var scope=factory.Services.CreateScope()){
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            await Assert.ThrowsAnyAsync<Exception>(()=>db.Database.ExecuteSqlRawAsync("delete from person_document_verifications where id={0}",verification.RequestId));
            await Assert.ThrowsAnyAsync<Exception>(()=>db.Database.ExecuteSqlRawAsync("update person_document_requirements set title='overwrite' where id={0}",rule.RequestId));
        }
    }
}
