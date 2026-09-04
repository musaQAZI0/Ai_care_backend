using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed class IntegrationWebhookSecurityRegressionTests(PostgresRegressionFactory factory):IClassFixture<PostgresRegressionFactory>
{
 [Fact]
 public async Task PublicWebhookRequiresFreshValidSignatureAndIsIdempotent()
 {
  await factory.EnsureClinicalSeedAsync();var admin=factory.CreateClient();var login=await admin.PostAsJsonAsync("/api/auth/login",new{userName="admin",password="Admin123!",mfaCode=(string?)null});login.EnsureSuccessStatusCode();admin.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await login.Content.ReadFromJsonAsync<Login>())!.Token);
  var created=await admin.PostAsJsonAsync("/api/phase1/integrations/connectors",new{name=$"Signed webhook {Guid.NewGuid():N}",connectorType="Custom API",endpointUrl="",configuration=new{},enableWebhook=true});created.EnsureSuccessStatusCode();var connector=(await created.Content.ReadFromJsonAsync<Connector>())!;Assert.False(string.IsNullOrWhiteSpace(connector.WebhookSecret));
  var body="{\"personId\":\"regression\"}";var timestamp=DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();var eventId=$"evt-{Guid.NewGuid():N}";
  var invalid=Message(connector.Id,body,timestamp,eventId,"bad");Assert.Equal(HttpStatusCode.Unauthorized,(await admin.SendAsync(invalid)).StatusCode);
  var signature=Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(connector.WebhookSecret),Encoding.UTF8.GetBytes($"{timestamp}.{body}"))).ToLowerInvariant();
  Assert.Equal(HttpStatusCode.Accepted,(await admin.SendAsync(Message(connector.Id,body,timestamp,eventId,signature))).StatusCode);
  var duplicate=await admin.SendAsync(Message(connector.Id,body,timestamp,eventId,signature));Assert.Equal(HttpStatusCode.OK,duplicate.StatusCode);Assert.Contains("Duplicate",await duplicate.Content.ReadAsStringAsync());
 }
 private static HttpRequestMessage Message(Guid id,string body,string timestamp,string eventId,string signature){var request=new HttpRequestMessage(HttpMethod.Post,$"/api/integrations/webhooks/{id}"){Content=new StringContent(body,Encoding.UTF8,"application/json")};request.Headers.Add("X-AiCare-Timestamp",timestamp);request.Headers.Add("X-AiCare-Signature",signature);request.Headers.Add("X-AiCare-Event","person.updated");request.Headers.Add("X-AiCare-Event-Id",eventId);return request;}
 private sealed record Login(string Token);private sealed record Connector(Guid Id,string WebhookSecret);
}
