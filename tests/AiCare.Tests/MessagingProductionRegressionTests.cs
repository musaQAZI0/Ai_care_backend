using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed class MessagingProductionRegressionTests : IClassFixture<PostgresRegressionFactory>
{
    private static readonly Guid AdminUserId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid DocumentId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000099");
    private readonly PostgresRegressionFactory _factory;

    public MessagingProductionRegressionTests(PostgresRegressionFactory factory) => _factory = factory;

    [Fact]
    public async Task ConversationMessageReplyAttachmentUnreadAndReadReceiptRoundTrip()
    {
        await _factory.EnsureClinicalSeedAsync();
        await EnsureDocumentAsync();
        var client = _factory.CreateClient();
        var login = await Login(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        var directory = await client.GetAsync("/api/messaging/participants");
        Assert.Equal(HttpStatusCode.OK, directory.StatusCode);
        var directoryBody = await directory.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"userName\":\"admin\"", directoryBody, StringComparison.OrdinalIgnoreCase);

        var create = await client.PostAsJsonAsync("/api/messaging/conversations", new
        {
            serviceUserId = RegressionIds.ServiceUserId,
            subject = "Regression care conversation",
            participantUserIds = new[] { AdminUserId }
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var conversationId = (await create.Content.ReadFromJsonAsync<CreatedId>())!.Id;

        var send = await client.PostAsJsonAsync($"/api/messaging/conversations/{conversationId}/messages", new
        {
            body = "Initial secure care message",
            replyToMessageId = (Guid?)null,
            documentIds = new[] { DocumentId }
        });
        Assert.Equal(HttpStatusCode.OK, send.StatusCode);
        var messageId = (await send.Content.ReadFromJsonAsync<CreatedId>())!.Id;

        var attachments = await client.GetAsync($"/api/messaging/conversations/{conversationId}/attachments");
        Assert.Equal(HttpStatusCode.OK, attachments.StatusCode);
        var attachmentBody = await attachments.Content.ReadAsStringAsync();
        Assert.Contains("regression.pdf", attachmentBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(DocumentId.ToString(), attachmentBody, StringComparison.OrdinalIgnoreCase);

        var reply = await client.PostAsJsonAsync($"/api/messaging/conversations/{conversationId}/messages", new
        {
            body = "Reply to secure care message",
            replyToMessageId = messageId,
            documentIds = Array.Empty<Guid>()
        });
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);

        var conversation = await client.GetAsync($"/api/messaging/conversations/{conversationId}");
        Assert.Equal(HttpStatusCode.OK, conversation.StatusCode);
        var body = await conversation.Content.ReadAsStringAsync();
        Assert.Contains("Initial secure care message", body);
        Assert.Contains("Reply to secure care message", body);

        var markRead = await client.PostAsync($"/api/messaging/conversations/{conversationId}/read", null);
        Assert.Equal(HttpStatusCode.NoContent, markRead.StatusCode);

        var list = await client.GetAsync("/api/messaging/conversations");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains("Regression care conversation", await list.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DeliveryFailureRetryUrgentEscalationRetentionAndHistoryAreGoverned()
    {
        await _factory.EnsureClinicalSeedAsync();var client=_factory.CreateClient();var login=await Login(client);client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",login.Token);
        var create=await client.PostAsJsonAsync("/api/messaging/conversations",new{serviceUserId=RegressionIds.ServiceUserId,subject="Urgent governed conversation",participantUserIds=new[]{AdminUserId}});Assert.Equal(HttpStatusCode.Created,create.StatusCode);var conversation=(await create.Content.ReadFromJsonAsync<CreatedId>())!.Id;
        var send=await client.PostAsJsonAsync($"/api/messaging/conversations/{conversation}/messages",new{body="Urgent response required",replyToMessageId=(Guid?)null,documentIds=Array.Empty<Guid>(),classification="Urgent",retentionUntil=DateTimeOffset.UtcNow.AddYears(2)});Assert.Equal(HttpStatusCode.OK,send.StatusCode);var message=(await send.Content.ReadFromJsonAsync<CreatedId>())!.Id;
        var detail=await client.GetAsync($"/api/messaging/conversations/{conversation}");var detailText=await detail.Content.ReadAsStringAsync();Assert.Contains("Urgent",detailText);Assert.Contains("Delivered",detailText);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync($"/api/messaging/governance/messages/{message}/fail",new{detail=""})).StatusCode);Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync($"/api/messaging/governance/messages/{message}/fail",new{detail="Internal queue unavailable"})).StatusCode);Assert.Equal(HttpStatusCode.OK,(await client.PostAsync($"/api/messaging/governance/messages/{message}/retry",null)).StatusCode);Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsync($"/api/messaging/governance/messages/{message}/retry",null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync($"/api/messaging/governance/messages/{message}/retention",new{retentionUntil=DateTimeOffset.UtcNow.AddYears(7),legalHold=true})).StatusCode);var governance=await client.GetAsync($"/api/messaging/governance/conversations/{conversation}");var payload=await governance.Content.ReadAsStringAsync();Assert.Contains("Retried",payload);Assert.Contains("Legal hold applied",payload);Assert.Contains("Urgent family/care-team message",payload);using var document=System.Text.Json.JsonDocument.Parse(payload);var escalation=document.RootElement.GetProperty("escalations")[0].GetProperty("id").GetGuid();Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync($"/api/messaging/governance/escalations/{escalation}/transition",new{action="Resolve",detail="Care team responded"})).StatusCode);
    }

    [Fact]
    public async Task CrossTenantUserCannotReadConversationById()
    {
        await _factory.EnsureClinicalSeedAsync();
        var owner = _factory.CreateClient();
        var login = await Login(owner);
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        var create = await owner.PostAsJsonAsync("/api/messaging/conversations", new
        {
            serviceUserId = RegressionIds.ServiceUserId,
            subject = "Tenant isolation conversation",
            participantUserIds = new[] { AdminUserId }
        });
        create.EnsureSuccessStatusCode();
        var conversationId = (await create.Content.ReadFromJsonAsync<CreatedId>())!.Id;

        var attackerUserName = await CreateCrossTenantUserAsync();
        var attacker = _factory.CreateClient();
        var attackerLogin = await Login(attacker, attackerUserName, "CrossTenant123!");
        attacker.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", attackerLogin.Token);
        var response = await attacker.GetAsync($"/api/messaging/conversations/{conversationId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var attachments = await attacker.GetAsync($"/api/messaging/conversations/{conversationId}/attachments");
        Assert.Equal(HttpStatusCode.NotFound, attachments.StatusCode);
    }

    private async Task EnsureDocumentAsync()
    {
        _ = _factory.CreateClient();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        if (await db.Documents.FindAsync(DocumentId) is null)
        {
            db.Documents.Add(new DocumentItem(DocumentId, RegressionIds.ServiceUserId, "regression.pdf", "Care plan", "local://regression.pdf", "Regression Admin", DateTimeOffset.UtcNow, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await db.SaveChangesAsync();
        }
    }

    private async Task<string> CreateCrossTenantUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        var userName = $"cross.tenant.{Guid.NewGuid():N}";
        var organizationId = Guid.NewGuid();
        db.AppUsers.Add(new AppUser(Guid.NewGuid(), userName, $"{userName}@aicare.local", PasswordHasher.HashPassword("CrossTenant123!"), UserRole.Administrator, true, organizationId, null, null, null));
        await db.SaveChangesAsync();
        return userName;
    }

    private static Task<LoginResponse> Login(HttpClient client) => Login(client, "admin", "Admin123!");

    private static async Task<LoginResponse> Login(HttpClient client, string userName, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { userName, password, mfaCode = (string?)null });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    private sealed record LoginResponse(string Token, string RefreshToken, int ExpiresInMinutes);
    private sealed record CreatedId(Guid Id);
}
