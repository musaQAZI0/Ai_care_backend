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
public sealed class MessagingPrivacyRegressionTests(PostgresRegressionFactory factory):IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task CareTeamDocumentsReceiptsRevocationAndAtomicDeliveryAreEnforced()
    {
        await factory.EnsureClinicalSeedAsync();
        var manager=await User(UserRole.CareManager,TenantDefaults.BranchId);
        var second=await User(UserRole.CareCoordinator,TenantDefaults.BranchId);
        var foreign=await User(UserRole.CareManager,Guid.NewGuid());
        var unrelated=await User(UserRole.CareWorker,TenantDefaults.BranchId);
        var assigned=await User(UserRole.CareWorker,TenantDefaults.BranchId,worker:RegressionIds.WorkerId);
        var familyId=Guid.NewGuid();var grantId=Guid.NewGuid();var shared=Guid.NewGuid();var hidden=Guid.NewGuid();
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.FamilyMembers.Add(new FamilyMember(familyId,RegressionIds.ServiceUserId,"Privacy family","privacy@example.test","Daughter","Portal","Active",TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            db.Documents.AddRange(new DocumentItem(shared,RegressionIds.ServiceUserId,"shared.pdf","Care plan","local://shared","Test",DateTimeOffset.UtcNow,TenantDefaults.OrganizationId,TenantDefaults.BranchId),new DocumentItem(hidden,RegressionIds.ServiceUserId,"internal.pdf","Internal","local://internal","Test",DateTimeOffset.UtcNow,TenantDefaults.OrganizationId,TenantDefaults.BranchId));await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"insert into family_access_grants(id,family_member_id,service_user_id,authority_type,verification_status,access_status,organization_id,branch_id) values({grantId},{familyId},{RegressionIds.ServiceUserId},'Representative','Verified','Active',{TenantDefaults.OrganizationId},{TenantDefaults.BranchId})");
            await db.Database.ExecuteSqlInterpolatedAsync($"insert into family_access_permissions(access_grant_id,permission) values({grantId},'MessageCareTeam'),({grantId},'ViewDocuments')");
            await db.Database.ExecuteSqlInterpolatedAsync($"insert into family_document_visibility(document_id,visibility,organization_id) values({shared},'ServiceUserAndRepresentative',{TenantDefaults.OrganizationId}),({hidden},'InternalOnly',{TenantDefaults.OrganizationId})");
        }
        var family=await User(UserRole.FamilyMember,TenantDefaults.BranchId,familyId);
        var directory=await family.Client.GetFromJsonAsync<JsonElement[]>($"/api/messaging/participants?serviceUserId={RegressionIds.ServiceUserId}");
        Assert.Contains(directory!,x=>x.GetProperty("id").GetGuid()==manager.Id);
        Assert.Contains(directory!,x=>x.GetProperty("id").GetGuid()==assigned.Id);
        Assert.DoesNotContain(directory!,x=>x.GetProperty("id").GetGuid()==foreign.Id||x.GetProperty("id").GetGuid()==unrelated.Id);
        var denied=await foreign.Client.PostAsJsonAsync("/api/messaging/conversations",new {serviceUserId=RegressionIds.ServiceUserId,subject="Denied",participantUserIds=new[]{manager.Id}});
        Assert.Equal(HttpStatusCode.NotFound,denied.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await unrelated.Client.PostAsJsonAsync("/api/messaging/conversations",new {serviceUserId=RegressionIds.ServiceUserId,subject="Denied",participantUserIds=new[]{manager.Id}})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await family.Client.PostAsJsonAsync("/api/messaging/conversations",new {serviceUserId=RegressionIds.ServiceUserId,subject="Denied",participantUserIds=new[]{foreign.Id}})).StatusCode);
        var create=await family.Client.PostAsJsonAsync("/api/messaging/conversations",new {serviceUserId=RegressionIds.ServiceUserId,subject="Privacy",participantUserIds=new[]{manager.Id,second.Id}});
        create.EnsureSuccessStatusCode();var conversation=(await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var url=$"/api/messaging/conversations/{conversation}";
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            // Legacy membership alone must not grant access to an unrelated branch or worker.
            await db.Database.ExecuteSqlInterpolatedAsync($"insert into conversation_participants(conversation_id,user_id,joined_at,last_read_at) values({conversation},{foreign.Id},now(),now()),({conversation},{unrelated.Id},now(),now())");
        }
        Assert.Equal(HttpStatusCode.NotFound,(await foreign.Client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await unrelated.Client.GetAsync(url)).StatusCode);
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"update conversation_participants set left_at=now() where conversation_id={conversation} and user_id in ({foreign.Id},{unrelated.Id})");
        }
        Assert.Equal(HttpStatusCode.BadRequest,(await family.Client.PostAsJsonAsync(url+"/messages",new {body="Hidden",documentIds=new[]{hidden}})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await manager.Client.PostAsJsonAsync(url+"/messages",new {body="Hidden",documentIds=new[]{hidden}})).StatusCode);
        var sent=await family.Client.PostAsJsonAsync(url+"/messages",new {body="Private message body",documentIds=new[]{shared}});sent.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent,(await manager.Client.PostAsync(url+"/read",null)).StatusCode);
        var detail=await second.Client.GetFromJsonAsync<JsonElement>(url);var message=detail.GetProperty("messages")[0];
        Assert.Equal(1,message.GetProperty("readCount").GetInt32());
        Assert.False(message.GetProperty("isReadByCurrentUser").GetBoolean());
        Assert.Equal("Delivered",message.GetProperty("deliveryStatus").GetString());
        Assert.Single((await family.Client.GetFromJsonAsync<JsonElement[]>(url+"/attachments"))!);
        using var verify=factory.Services.CreateScope();var db0=verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.False(await db0.Notifications.AnyAsync(x=>x.Detail.Contains("Private message body")));
        await db0.Database.ExecuteSqlInterpolatedAsync($"update family_document_visibility set visibility='InternalOnly' where document_id={shared}");
        Assert.Empty((await family.Client.GetFromJsonAsync<JsonElement[]>(url+"/attachments"))!);
        await db0.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_message_audit_test() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW."Action"='messaging.message_delivered' THEN RAISE EXCEPTION 'Simulated audit failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER fail_message_audit_test BEFORE INSERT ON "AuditEvents" FOR EACH ROW EXECUTE FUNCTION fail_message_audit_test();
            """);
        var notifications=await db0.Notifications.CountAsync();
        try
        {
            Assert.Equal(HttpStatusCode.InternalServerError,(await manager.Client.PostAsJsonAsync(url+"/messages",new {body="Must roll back",documentIds=Array.Empty<Guid>()})).StatusCode);
            var after=await manager.Client.GetFromJsonAsync<JsonElement>(url);
            Assert.Single(after.GetProperty("messages").EnumerateArray());
            Assert.Equal(notifications,await db0.Notifications.CountAsync());
        }
        finally { await db0.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_message_audit_test ON \"AuditEvents\"; DROP FUNCTION fail_message_audit_test();"); }
        await db0.Database.ExecuteSqlInterpolatedAsync($"update family_access_grants set access_status='Revoked' where id={grantId}");
        Assert.Equal(HttpStatusCode.NotFound,(await family.Client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await family.Client.GetAsync(url+"/attachments")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await family.Client.GetAsync($"/api/messaging/governance/conversations/{conversation}")).StatusCode);
        Assert.Empty((await family.Client.GetFromJsonAsync<JsonElement[]>("/api/messaging/conversations"))!);
    }
    private async Task<(HttpClient Client,Guid Id)> User(UserRole role,Guid branch,Guid? family=null,Guid? worker=null)
    {
        var id=Guid.NewGuid();var name=$"privacy.{Guid.NewGuid():N}";
        using var scope=factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
        db.AppUsers.Add(new AppUser(id,name,$"{name}@example.test",PasswordHasher.HashPassword("Admin123!"),role,true,TenantDefaults.OrganizationId,branch,worker,family));await db.SaveChangesAsync();
        var client=factory.CreateClient();var login=await client.PostAsJsonAsync("/api/auth/login",new {userName=name,password="Admin123!"});login.EnsureSuccessStatusCode();client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString());return(client,id);
    }
}
