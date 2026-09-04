using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Xunit;
namespace AiCare.Tests;
[Collection("Postgres regression")]
public sealed class IntegrationConnectorManagementRegressionTests(PostgresRegressionFactory factory):IClassFixture<PostgresRegressionFactory>
{
 [Fact]public async Task AdminCanScheduleDisableAndRotateConnectorSecret(){await factory.EnsureClinicalSeedAsync();var client=factory.CreateClient();var login=await client.PostAsJsonAsync("/api/auth/login",new{userName="admin",password="Admin123!",mfaCode=(string?)null});login.EnsureSuccessStatusCode();client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await login.Content.ReadFromJsonAsync<Login>())!.Token);var create=await client.PostAsJsonAsync("/api/phase1/integrations/connectors",new{name=$"Managed {Guid.NewGuid():N}",connectorType="Custom API",endpointUrl="https://example.invalid",enableWebhook=true,scheduleMinutes=15});create.EnsureSuccessStatusCode();var connector=(await create.Content.ReadFromJsonAsync<Connector>())!;Assert.NotEmpty(connector.WebhookSecret);var update=await client.PatchAsJsonAsync($"/api/phase1/integrations/connectors/{connector.Id}",new{name="Managed updated",endpointUrl="https://updated.invalid",status="Disabled",scheduleMinutes=30});Assert.Equal(HttpStatusCode.NoContent,update.StatusCode);var rotate=await client.PostAsync($"/api/phase1/integrations/connectors/{connector.Id}/rotate-webhook-secret",null);rotate.EnsureSuccessStatusCode();var rotated=(await rotate.Content.ReadFromJsonAsync<Secret>())!;Assert.NotEqual(connector.WebhookSecret,rotated.WebhookSecret);var dashboard=await client.GetStringAsync("/api/phase1/integrations/dashboard");Assert.Contains("Managed updated",dashboard);Assert.Contains("scheduleMinutes",dashboard);Assert.Contains("Disabled",dashboard);}
 private sealed record Login(string Token);private sealed record Connector(Guid Id,string WebhookSecret);private sealed record Secret(string WebhookSecret);
}
