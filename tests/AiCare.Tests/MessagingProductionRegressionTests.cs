using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
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

        var attacker = _factory.CreateClient();
        attacker.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateCrossTenantToken());
        var response = await attacker.GetAsync($"/api/messaging/conversations/{conversationId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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

    private static async Task<LoginResponse> Login(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "Admin123!", mfaCode = (string?)null });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    private static string CreateCrossTenantToken()
    {
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("regression-signing-key-with-enough-length-for-hmac-2026")), SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
            new Claim(JwtRegisteredClaimNames.UniqueName, "cross-tenant-messaging"),
            new Claim(ClaimTypes.Role, "Administrator"),
            new Claim("organization_id", Guid.Parse("77777777-7777-7777-7777-777777777777").ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("AiCare", "AiCareClient", claims, expires: DateTime.UtcNow.AddMinutes(30), signingCredentials: credentials));
    }

    private sealed record LoginResponse(string Token, string RefreshToken, int ExpiresInMinutes);
    private sealed record CreatedId(Guid Id);
}
