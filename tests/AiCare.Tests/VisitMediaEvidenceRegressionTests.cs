using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace AiCare.Tests;
[Collection("Postgres regression")]
public sealed class VisitMediaEvidenceRegressionTests(PostgresRegressionFactory factory):IClassFixture<PostgresRegressionFactory>
{
    static readonly byte[] Png=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a0O8AAAAASUVORK5CYII=");
    [Theory]
    [InlineData("Photo")]
    [InlineData("Signature")]
    public async Task PrivateMediaRequiresConsentValidBytesAuditAndStableRetriesAndKeepsWithdrawalHistory(string kind)
    {
        await factory.EnsureClinicalSeedAsync();var visit=Guid.NewGuid();var unassigned="media.worker."+Guid.NewGuid().ToString("N");
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();var original=await db.Visits.AsNoTracking().SingleAsync(x=>x.Id==RegressionIds.VisitId);db.Visits.Add(original with { Id=visit });
            db.AppUsers.Add(new AppUser(Guid.NewGuid(),unassigned,unassigned+"@example.invalid",PasswordHasher.HashPassword("Admin123!"),UserRole.CareWorker,true,TenantDefaults.OrganizationId,TenantDefaults.BranchId));await db.SaveChangesAsync();
        }
        using var client=await Client("admin");using var worker=await Client(unassigned);var path=$"/api/phase1/visits/{visit}/media-evidence";var id=Guid.NewGuid();
        using(var denied=Form(id,kind,false))Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsync(path,denied)).StatusCode);
        // The shared upload validator uses 415 for mismatched image signatures.
        using(var unsafeForm=Form(id,kind,true,[1,2,3]))Assert.Equal(HttpStatusCode.UnsupportedMediaType,(await client.PostAsync(path,unsafeForm)).StatusCode);
        using(var forbidden=Form(id,kind,true))Assert.Equal(HttpStatusCode.NotFound,(await worker.PostAsync(path,forbidden)).StatusCode);
        using var scope2=factory.Services.CreateScope();var db2=scope2.ServiceProvider.GetRequiredService<CareDbContext>();var constraint="ck_media_audit_"+Guid.NewGuid().ToString("N");
        // Locally generated UUIDs form trusted SQL identifiers/literals for failure injection.
        await db2.Database.ExecuteSqlRawAsync($"""ALTER TABLE "AuditEvents" ADD CONSTRAINT "{constraint}" CHECK ("EntityId"<>'{id}' OR "Action"<>'visit.media_uploaded')""");
        try{using var failing=Form(id,kind,true);Assert.Equal(HttpStatusCode.InternalServerError,(await client.PostAsync(path,failing)).StatusCode);}
        finally{await db2.Database.ExecuteSqlRawAsync($"""
            ALTER TABLE "AuditEvents" DROP CONSTRAINT "{constraint}"
            """);}
        Assert.Equal(0,await db2.Database.SqlQueryRaw<int>("select count(*)::int as \"Value\" from visit_media_evidence where id={0}",id).SingleAsync());
        using(var first=Form(id,kind,true)){var result=await client.PostAsync(path,first);Assert.True(result.StatusCode==HttpStatusCode.Created,await result.Content.ReadAsStringAsync());}
        using(var replay=Form(id,kind,true))Assert.Equal(HttpStatusCode.OK,(await client.PostAsync(path,replay)).StatusCode);
        using(var recover=Form(Guid.NewGuid(),kind,true))Assert.Equal(HttpStatusCode.OK,(await client.PostAsync(path,recover)).StatusCode);
        using(var changed=Form(id,kind,true,caption:"Changed caption"))Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsync(path,changed)).StatusCode);
        var items=await client.GetFromJsonAsync<JsonElement>(path);var item=Assert.Single(items.GetProperty("items").EnumerateArray());Assert.Equal(id,item.GetProperty("id").GetGuid());Assert.Equal(Convert.ToHexString(SHA256.HashData(Png)),item.GetProperty("sha256").GetString());
        var content=await client.GetAsync(path+$"/{id}/content");content.EnsureSuccessStatusCode();Assert.Equal(Png,await content.Content.ReadAsByteArrayAsync());Assert.Equal("no-store",content.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.NotFound,(await worker.GetAsync(path+$"/{id}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await worker.PostAsJsonAsync(path+$"/{id}/withdraw",new{reason="Wrong worker attempted withdrawal"})).StatusCode);
        var reason=new{reason="Synthetic photo no longer appropriate for sharing"};Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync(path+$"/{id}/withdraw",reason)).StatusCode);Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync(path+$"/{id}/withdraw",reason)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync(path+$"/{id}/content")).StatusCode);
        var retained=Assert.Single((await client.GetFromJsonAsync<JsonElement>(path)).GetProperty("items").EnumerateArray());Assert.Equal(reason.reason,retained.GetProperty("withdrawal_reason").GetString());
        Assert.Equal(1,await db2.AuditEvents.CountAsync(x=>x.EntityId==id&&x.Action=="visit.media_uploaded"));Assert.Equal(1,await db2.AuditEvents.CountAsync(x=>x.EntityId==id&&x.Action=="visit.media_withdrawn"));
        await Assert.ThrowsAnyAsync<Exception>(()=>db2.Database.ExecuteSqlRawAsync("delete from visit_media_evidence where id={0}",id));
    }
    static MultipartFormDataContent Form(Guid id,string kind,bool consent,byte[]? bytes=null,string caption="Synthetic visit evidence")
    {
        var form=new MultipartFormDataContent();form.Add(new StringContent(id.ToString()),"requestId");form.Add(new StringContent(kind),"kind");form.Add(new StringContent(caption),"caption");form.Add(new StringContent("File"),"captureSource");form.Add(new StringContent(consent.ToString()),"consentConfirmed");
        if(kind=="Signature"){form.Add(new StringContent("Synthetic signer"),"signerName");form.Add(new StringContent("Signed in the recorder's presence under the approved consent process"),"declaration");}
        var file=new ByteArrayContent(bytes??Png);file.Headers.ContentType=new MediaTypeHeaderValue("image/png");form.Add(file,"file","evidence.png");return form;
    }
    async Task<HttpClient> Client(string name){var client=factory.CreateClient();var login=await client.PostAsJsonAsync("/api/auth/login",new{userName=name,password="Admin123!",mfaCode=(string?)null});login.EnsureSuccessStatusCode();client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await login.Content.ReadFromJsonAsync<Login>())!.Token);return client;}
    sealed record Login(string Token);
}
