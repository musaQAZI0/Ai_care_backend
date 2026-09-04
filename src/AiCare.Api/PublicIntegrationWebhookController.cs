using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[ApiController,AllowAnonymous]
[Route("api/integrations/webhooks")]
public sealed class PublicIntegrationWebhookController(CareDbContext db,IDataProtectionProvider protection):ControllerBase
{
 [HttpPost("{connectorId:guid}")]
 public async Task<IActionResult> Receive(Guid connectorId,CancellationToken token)
 {
  var connector=await Load(connectorId,token);if(connector is null)return NotFound();
  if(!Request.Headers.TryGetValue("X-AiCare-Timestamp",out var timestampText)||!long.TryParse(timestampText,out var timestamp)||Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds()-timestamp)>300)return Unauthorized(new{message="Webhook timestamp is missing or outside the five-minute replay window."});
  if(!Request.Headers.TryGetValue("X-AiCare-Signature",out var supplied))return Unauthorized(new{message="Webhook signature is required."});
  using var reader=new StreamReader(Request.Body,Encoding.UTF8);var body=await reader.ReadToEndAsync(token);var secret=protection.CreateProtector("AiCare.Integration.Webhook.v1").Unprotect(connector.Secret);var expected=Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),Encoding.UTF8.GetBytes($"{timestamp}.{body}"))).ToLowerInvariant();
  if(!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected),Encoding.ASCII.GetBytes(supplied.ToString().ToLowerInvariant())))return Unauthorized(new{message="Webhook signature is invalid."});
  JsonDocument payload;try{payload=JsonDocument.Parse(body);}catch(JsonException){return BadRequest(new{message="Webhook body must be valid JSON."});}using(payload){var eventType=Request.Headers["X-AiCare-Event"].ToString();if(string.IsNullOrWhiteSpace(eventType))return BadRequest(new{message="X-AiCare-Event is required."});var external=Request.Headers["X-AiCare-Event-Id"].ToString();if(string.IsNullOrWhiteSpace(external))return BadRequest(new{message="X-AiCare-Event-Id is required for idempotency."});try{await db.Database.ExecuteSqlInterpolatedAsync($"insert into integration_webhook_events(id,connector_id,organization_id,branch_id,event_type,external_event_id,status,payload_json) values({Guid.NewGuid()},{connectorId},{connector.Organization},{connector.Branch},{eventType},{external},'Received',cast({body} as jsonb))",token);}catch(Exception e)when(e.InnerException is Npgsql.PostgresException { SqlState:"23505" }||e is Npgsql.PostgresException { SqlState:"23505" }){return Ok(new{status="Duplicate",externalEventId=external});}}
  return Accepted(new{status="Received"});
 }
 private async Task<Connector?> Load(Guid id,CancellationToken token){var cn=db.Database.GetDbConnection();if(cn.State!=ConnectionState.Open)await cn.OpenAsync(token);await using var c=cn.CreateCommand();c.CommandText="select organization_id,branch_id,webhook_secret_protected from integration_connectors where id=@id and status='Active' and webhook_secret_protected is not null";var p=c.CreateParameter();p.ParameterName="id";p.Value=id;c.Parameters.Add(p);await using var r=await c.ExecuteReaderAsync(token);return await r.ReadAsync(token)?new(r.GetGuid(0),r.GetGuid(1),r.GetString(2)):null;}
 private sealed record Connector(Guid Organization,Guid Branch,string Secret);
}
