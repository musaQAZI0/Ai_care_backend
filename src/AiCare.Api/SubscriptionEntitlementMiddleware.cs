using System.Data;
using System.Text.Json;
using AiCare.Application;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace AiCare.Api;
[AttributeUsage(AttributeTargets.Class|AttributeTargets.Method)]
public sealed class SubscriptionEntitlementAttribute(string code):Attribute{public string Code{get;}=code;}
public sealed class SubscriptionEntitlementMiddleware(RequestDelegate next)
{
 public async Task InvokeAsync(HttpContext context,CareDbContext db,ITenantContext tenant)
 {
  var requirement=context.GetEndpoint()?.Metadata.GetMetadata<SubscriptionEntitlementAttribute>();if(requirement is null||context.User.Identity?.IsAuthenticated!=true){await next(context);return;}
  var connection=db.Database.GetDbConnection();if(connection.State!=ConnectionState.Open)await connection.OpenAsync(context.RequestAborted);await using var c=connection.CreateCommand();c.CommandText="select row_to_json(s)::text from (select subscription_status,valid_until,entitlements from saas.subscription_versions where organization_id=@org and approved_by is not null and effective_from<=now() order by effective_from desc,version desc limit 1)s";var p=c.CreateParameter();p.ParameterName="org";p.Value=tenant.OrganizationId;c.Parameters.Add(p);var result=await c.ExecuteScalarAsync(context.RequestAborted);
  // Legacy tenants remain unprovisioned until an explicit subscription contract is approved.
  if(result is null){await next(context);return;}var subscription=JsonSerializer.Deserialize<JsonElement>((string)result);var allowed=subscription.GetProperty("subscription_status").GetString() is "Active" or "Trial"&&subscription.GetProperty("valid_until").GetDateTimeOffset()>DateTimeOffset.UtcNow&&subscription.GetProperty("entitlements").TryGetProperty(requirement.Code,out var value)&&value.ValueKind==JsonValueKind.True;
  if(!allowed){context.Response.StatusCode=403;await context.Response.WriteAsJsonAsync(new{message="The approved subscription does not currently entitle this feature.",feature=requirement.Code},context.RequestAborted);return;}await next(context);
 }
}
