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
 private const int MaxPayloadBytes=256*1024;
 private static readonly UTF8Encoding StrictUtf8=new(false,true);

 [HttpPost("{connectorId:guid}")]
 [RequestSizeLimit(MaxPayloadBytes)]
 public async Task<IActionResult> Receive(Guid connectorId,CancellationToken token)
 {
  var connector=await Load(connectorId,token);if(connector is null)return NotFound();
  if(Request.ContentLength is > MaxPayloadBytes)return StatusCode(StatusCodes.Status413PayloadTooLarge,new{message=$"Webhook payload cannot exceed {MaxPayloadBytes} bytes."});
  if(!Request.HasJsonContentType())return StatusCode(StatusCodes.Status415UnsupportedMediaType,new{message="Webhook content type must be application/json."});
  if(!Request.Headers.TryGetValue("X-AiCare-Timestamp",out var timestampText)||!long.TryParse(timestampText,out var timestamp)||Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds()-timestamp)>300)return Unauthorized(new{message="Webhook timestamp is missing or outside the five-minute replay window."});
  if(!Request.Headers.TryGetValue("X-AiCare-Signature",out var supplied)||supplied.Count!=1||supplied[0] is not { Length:64 } signature||!signature.All(Uri.IsHexDigit))return Unauthorized(new{message="Webhook signature is required and must be a SHA-256 hexadecimal value."});
  var bodyBytes=await ReadBody(token);if(bodyBytes is null)return StatusCode(StatusCodes.Status413PayloadTooLarge,new{message=$"Webhook payload cannot exceed {MaxPayloadBytes} bytes."});
  string body;try{body=StrictUtf8.GetString(bodyBytes);}catch(DecoderFallbackException){return BadRequest(new{message="Webhook body must use valid UTF-8 encoding."});}
  var secret=protection.CreateProtector("AiCare.Integration.Webhook.v1").Unprotect(connector.Secret);var prefix=Encoding.UTF8.GetBytes($"{timestamp}.");var signedBytes=new byte[prefix.Length+bodyBytes.Length];Buffer.BlockCopy(prefix,0,signedBytes,0,prefix.Length);Buffer.BlockCopy(bodyBytes,0,signedBytes,prefix.Length,bodyBytes.Length);var expected=Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),signedBytes)).ToLowerInvariant();
  if(!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected),Encoding.ASCII.GetBytes(signature.ToLowerInvariant())))return Unauthorized(new{message="Webhook signature is invalid."});
  JsonDocument payload;try{payload=JsonDocument.Parse(body,new JsonDocumentOptions{MaxDepth=32});}catch(JsonException){return BadRequest(new{message="Webhook body must be valid JSON."});}
  using(payload)
  {
   if(payload.RootElement.ValueKind!=JsonValueKind.Object)return BadRequest(new{message="Webhook body must be a JSON object."});
   var eventType=Request.Headers["X-AiCare-Event"].ToString().Trim();if(string.IsNullOrWhiteSpace(eventType)||eventType.Length>100)return BadRequest(new{message="X-AiCare-Event is required and cannot exceed 100 characters."});
   var external=Request.Headers["X-AiCare-Event-Id"].ToString().Trim();if(string.IsNullOrWhiteSpace(external)||external.Length>200)return BadRequest(new{message="X-AiCare-Event-Id is required for idempotency and cannot exceed 200 characters."});
   try{await db.Database.ExecuteSqlInterpolatedAsync($"insert into integration_webhook_events(id,connector_id,organization_id,branch_id,event_type,external_event_id,status,payload_json) values({Guid.NewGuid()},{connectorId},{connector.Organization},{connector.Branch},{eventType},{external},'Received',cast({body} as jsonb))",token);}catch(Exception e)when(e.InnerException is Npgsql.PostgresException { SqlState:"23505" }||e is Npgsql.PostgresException { SqlState:"23505" }){return Ok(new{status="Duplicate",externalEventId=external});}
  }
  return Accepted(new{status="Received"});
 }

 private async Task<byte[]?> ReadBody(CancellationToken token)
 {
  await using var output=new MemoryStream();var buffer=new byte[16*1024];
  while(true){var read=await Request.Body.ReadAsync(buffer.AsMemory(0,buffer.Length),token);if(read==0)break;if(output.Length+read>MaxPayloadBytes)return null;await output.WriteAsync(buffer.AsMemory(0,read),token);}
  return output.ToArray();
 }

 private async Task<Connector?> Load(Guid id,CancellationToken token){var cn=db.Database.GetDbConnection();if(cn.State!=ConnectionState.Open)await cn.OpenAsync(token);await using var c=cn.CreateCommand();c.CommandText="select organization_id,branch_id,webhook_secret_protected from integration_connectors where id=@id and status='Active' and webhook_secret_protected is not null";var p=c.CreateParameter();p.ParameterName="id";p.Value=id;c.Parameters.Add(p);await using var r=await c.ExecuteReaderAsync(token);return await r.ReadAsync(token)?new(r.GetGuid(0),r.GetGuid(1),r.GetString(2)):null;}
 private sealed record Connector(Guid Organization,Guid Branch,string Secret);
}
