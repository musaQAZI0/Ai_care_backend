using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiCare.Api;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace AiCare.Tests;
public sealed partial class AssessmentTemplateRegressionTests
{
    [Fact]
    public async Task CustomFormsRequirePublishedVersionsCompleteAnswersScopeAndImmutableCorrectionHistory()
    {
        var f=await Setup();var template=Template() with { AssessmentType="Custom form",Title="Custom support form" };
        var root=$"/api/phase1/service-users/{f.Person}/custom-forms";
        var capture=new CustomFormRequest(Guid.NewGuid(),template.RequestId,"{\"help\":true,\"details\":\"Synthetic support\"}",null,"");
        await Expect(await f.Author.PostAsJsonAsync(Templates,template),HttpStatusCode.Created);
        await Expect(await f.Author.PostAsJsonAsync(root,capture),HttpStatusCode.Conflict);
        await Expect(await f.Author.PostAsync($"{Templates}/{template.RequestId}/submit",null),HttpStatusCode.OK);
        await Expect(await f.Manager.PostAsJsonAsync($"{Templates}/{template.RequestId}/approve",new{reason="Reviewed general form fields"}),HttpStatusCode.OK);
        await Expect(await f.Author.PostAsJsonAsync(root,capture with { AnswersJson="{\"help\":true}" }),HttpStatusCode.BadRequest);
        await Expect(await f.Author.PostAsJsonAsync(root,capture with { AnswersJson="{\"help\":false,\"details\":\"Hidden answer\"}" }),HttpStatusCode.BadRequest);
        var saved=await Task.WhenAll(f.Author.PostAsJsonAsync(root,capture),f.Author.PostAsJsonAsync(root,capture));
        Assert.Single(saved,x=>x.StatusCode==HttpStatusCode.Created);Assert.Single(saved,x=>x.StatusCode==HttpStatusCode.OK);
        await Expect(await f.Author.PostAsJsonAsync(root,capture with { AnswersJson="{\"help\":false}" }),HttpStatusCode.Conflict);
        await Expect(await f.OtherBranch.GetAsync(root),HttpStatusCode.NotFound);
        await Expect(await f.Worker.GetAsync(root),HttpStatusCode.NotFound);
        var foreign=await Setup();await Expect(await foreign.Author.GetAsync(root),HttpStatusCode.NotFound);
        var next=template with { RequestId=Guid.NewGuid(),SourceVersionId=template.RequestId,ChangeReason="Updated question",ImpactReview="Original labels retained",Fields=[template.Fields[0] with { Label="Help required now" },template.Fields[1]] };
        await Expect(await f.Author.PostAsJsonAsync(Templates,next),HttpStatusCode.Created);await Expect(await f.Author.PostAsync($"{Templates}/{next.RequestId}/submit",null),HttpStatusCode.OK);
        await Expect(await f.Manager.PostAsJsonAsync($"{Templates}/{next.RequestId}/approve",new{reason="Reviewed replacement impact"}),HttpStatusCode.OK);
        await Expect(await f.Author.PostAsJsonAsync(root,capture with { RequestId=Guid.NewGuid() }),HttpStatusCode.Conflict);
        var correction=capture with { RequestId=Guid.NewGuid(),TemplateVersionId=next.RequestId,SupersedesId=capture.RequestId,Reason="Corrected after review of synthetic information",AnswersJson="{\"help\":false}" };
        await Expect(await f.Author.PostAsJsonAsync(root,correction with { Reason="Short" }),HttpStatusCode.BadRequest);
        await Expect(await f.Author.PostAsJsonAsync(root,correction),HttpStatusCode.Created);
        await Expect(await f.Author.PostAsJsonAsync(root,correction with { RequestId=Guid.NewGuid() }),HttpStatusCode.Conflict);
        var history=await f.Manager.GetFromJsonAsync<JsonElement>(root);
        var original=Assert.Single(history.GetProperty("submissions").EnumerateArray().Where(x=>x.GetProperty("id").GetGuid()==capture.RequestId));
        Assert.Equal("Needs help",original.GetProperty("field_labels").GetProperty("help").GetString());Assert.Equal("Synthetic support",original.GetProperty("answers").GetProperty("details").GetString());Assert.True(original.GetProperty("superseded").GetBoolean());
        Assert.Equal(2,history.GetProperty("submissions").GetArrayLength());Assert.False(history.GetProperty("hasMore").GetBoolean());
        await Expect(await f.Author.GetAsync(root+"?page=0"),HttpStatusCode.BadRequest);
        using var verify=factory.Services.CreateScope();var db=verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal(1,await db.AuditEvents.CountAsync(x=>x.EntityId==capture.RequestId&&x.Action=="custom_form.submitted"));
        Assert.Equal(1,await db.AuditEvents.CountAsync(x=>x.EntityId==correction.RequestId&&x.Action=="custom_form.corrected"));
        await Assert.ThrowsAnyAsync<Exception>(()=>db.Database.ExecuteSqlRawAsync("update custom_form_submissions set answers='{}'::jsonb where id={0}",capture.RequestId));
        await Assert.ThrowsAnyAsync<Exception>(()=>db.Database.ExecuteSqlRawAsync("delete from custom_form_submissions where id={0}",correction.RequestId));
    }
}
