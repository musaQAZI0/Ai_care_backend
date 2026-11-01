using System.Data;
using System.Data.Common;
using System.Text.Json;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
namespace AiCare.Api;
[ApiController,Authorize(Roles="Administrator,BackOffice")]
[Route("api/phase1/subscription")]
public sealed class SubscriptionsController(CareDbContext db,ITenantContext tenant,ICurrentUserContext user,IConfiguration configuration):ControllerBase
{
 bool Operator=>user.IsAdministrator&&user.UserId is Guid id&&(configuration.GetSection("SubscriptionBilling:OperatorUserIds").Get<string[]>()??[]).Contains(id.ToString(),StringComparer.OrdinalIgnoreCase);
 [HttpGet]public async Task<IActionResult> Get(CancellationToken t)
 {
  var current=await Rows("select * from saas.subscription_versions where organization_id=@org and approved_by is not null and effective_from<=now() order by effective_from desc,version desc limit 1",_=>{},t);
  var history=await Rows("select * from saas.subscription_versions where organization_id=@org order by version desc",_=>{},t);var usage=await Rows("select (select count(*) from \"CareWorkers\" where \"OrganizationId\"=@org) stored_workers,(select count(*) from \"Branches\" where \"OrganizationId\"=@org) stored_branches",_=>{},t);
  return Ok(new{isOperator=Operator,mode="ManualProvisioning",providerIntegrationEnabled=false,current=current.Count==1?(JsonElement?)current[0]:null,history,usage=usage[0],usageDefinition="All stored worker and branch records, including historical records. Limits are explicit and do not delete records."});
 }
 [HttpGet("entitlements/{code}")]public async Task<IActionResult> Entitlement(string code,CancellationToken t)
 {
  var current=await Rows("select subscription_status,valid_until,entitlements from saas.subscription_versions where organization_id=@org and approved_by is not null and effective_from<=now() order by effective_from desc,version desc limit 1",_=>{},t);
  if(current.Count==0)return Ok(new{configured=false,allowed=(bool?)null});var row=current[0];return Ok(new{configured=true,allowed=row.GetProperty("subscription_status").GetString() is "Active" or "Trial"&&row.GetProperty("valid_until").GetDateTimeOffset()>DateTimeOffset.UtcNow&&row.GetProperty("entitlements").TryGetProperty(code,out var value)&&value.ValueKind==JsonValueKind.True});
 }
 [HttpPost("versions")]public Task<IActionResult> Create(SubscriptionVersionRequest r,CancellationToken t)=>Tx(async()=>
 {
  if(!Operator)return Forbid();if(r.RequestId==Guid.Empty||string.IsNullOrWhiteSpace(r.PlanReference)||string.IsNullOrWhiteSpace(r.BillingContact)||!System.Net.Mail.MailAddress.TryCreate(r.BillingContact,out _)||r.SubscriptionStatus is not("Trial" or "Active" or "Suspended" or "Cancelled")||r.InvoiceStatus is not("NotInvoiced" or "Open" or "Paid" or "Overdue" or "Void")||r.ValidUntil<=r.EffectiveFrom||r.StoredWorkersLimit<0||r.StoredBranchesLimit<0||string.IsNullOrWhiteSpace(r.DecisionReference)||(r.InvoiceStatus!="NotInvoiced"&&string.IsNullOrWhiteSpace(r.SubscriptionInvoiceReference)))return BadRequest(new{message="Explicit plan/contact/status, validity, decision reference and subscription invoice reference are required. Limits count all stored records."});
  var snapshot=JsonSerializer.Serialize(r);var existing=await Rows("select request_snapshot=cast(@snapshot as jsonb) matches from saas.subscription_versions where id=@id and organization_id=@org",c=>{Add(c,"id",r.RequestId);Add(c,"snapshot",snapshot);},t);if(existing.Count>0)return existing[0].GetProperty("matches").GetBoolean()?Ok(new{id=r.RequestId,replayed=true}):Conflict(new{message="Subscription request ID has different details."});
  await Exec("insert into saas.subscription_versions(id,organization_id,version,plan_reference,billing_contact,subscription_status,invoice_status,subscription_invoice_reference,decision_reference,effective_from,valid_until,stored_workers_limit,stored_branches_limit,entitlements,request_snapshot,created_by) select @id,@org,coalesce(max(version),0)+1,@plan,@contact,@status,@invoiceStatus,@invoice,@reference,@start,@end,@workers,@branches,cast(@entitlements as jsonb),cast(@snapshot as jsonb),@actor from saas.subscription_versions where organization_id=@org",c=>{Add(c,"id",r.RequestId);Add(c,"plan",r.PlanReference.Trim());Add(c,"contact",r.BillingContact.Trim());Add(c,"status",r.SubscriptionStatus);Add(c,"invoiceStatus",r.InvoiceStatus);Add(c,"invoice",r.SubscriptionInvoiceReference.Trim());Add(c,"reference",r.DecisionReference.Trim());Add(c,"start",r.EffectiveFrom);Add(c,"end",r.ValidUntil);NullableInt(c,"workers",r.StoredWorkersLimit);NullableInt(c,"branches",r.StoredBranchesLimit);Add(c,"entitlements",JsonSerializer.Serialize(r.Entitlements));Add(c,"snapshot",snapshot);},t);await Audit("SAAS_SUBSCRIPTION_VERSION_CREATED",r.RequestId,t);return Created("",new{id=r.RequestId});
 },t);
 [HttpPost("versions/{id:guid}/approve")]public Task<IActionResult> Approve(Guid id,CancellationToken t)=>Tx(async()=>
 {
  if(!Operator)return Forbid();await using var c=await Command("update saas.subscription_versions set approved_by=@actor,approved_at=now() where id=@id and organization_id=@org and approved_by is null and created_by<>@actor returning id",t);Add(c,"id",id);if(await c.ExecuteScalarAsync(t) is not Guid)return Conflict(new{message="A different configured subscription operator must approve the version."});await Audit("SAAS_SUBSCRIPTION_VERSION_APPROVED",id,t);return Ok(new{id});
 },t);
 async Task<IActionResult>Tx(Func<Task<IActionResult>>work,CancellationToken t){try{return await db.Database.CreateExecutionStrategy().ExecuteAsync(async()=>{await using var tx=await db.Database.BeginTransactionAsync(t);await Exec("select pg_advisory_xact_lock(hashtextextended(@key,0))",c=>Add(c,"key",$"saas-subscription:{tenant.OrganizationId}"),t);var result=await work();if(result is ObjectResult o&&o.StatusCode>=400){await tx.RollbackAsync(t);return result;}await tx.CommitAsync(t);return result;});}catch(Npgsql.PostgresException e)when(e.SqlState is "23514" or "23505"){return Conflict(new{message="Subscription version, effective time, usage or immutable history conflict."});}}
 async Task<List<JsonElement>>Rows(string sql,Action<DbCommand>b,CancellationToken t){var rows=new List<JsonElement>();await using var c=await Command($"select row_to_json(q)::text from ({sql})q",t);b(c);await using var r=await c.ExecuteReaderAsync(t);while(await r.ReadAsync(t))rows.Add(JsonSerializer.Deserialize<JsonElement>(r.GetString(0)));return rows;}
 async Task Exec(string sql,Action<DbCommand>b,CancellationToken t){await using var c=await Command(sql,t);b(c);await c.ExecuteNonQueryAsync(t);}
 async Task<DbCommand>Command(string sql,CancellationToken t){var n=db.Database.GetDbConnection();if(n.State!=ConnectionState.Open)await n.OpenAsync(t);var c=n.CreateCommand();c.Transaction=db.Database.CurrentTransaction?.GetDbTransaction();c.CommandText=sql;Add(c,"org",tenant.OrganizationId);Add(c,"actor",user.UserId);return c;}
 static void Add(DbCommand c,string name,object?value){var p=c.CreateParameter();p.ParameterName=name;p.Value=value??DBNull.Value;c.Parameters.Add(p);}
 static void NullableInt(DbCommand c,string name,int?value){var p=c.CreateParameter();p.ParameterName=name;p.DbType=DbType.Int32;p.Value=(object?)value??DBNull.Value;c.Parameters.Add(p);}
 async Task Audit(string action,Guid id,CancellationToken t){db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),action,user.UserName,"SaaSSubscription",id,DateTimeOffset.UtcNow,tenant.OrganizationId));await db.SaveChangesAsync(t);}
}
public sealed record SubscriptionVersionRequest(Guid RequestId,string PlanReference,string BillingContact,string SubscriptionStatus,string InvoiceStatus,string SubscriptionInvoiceReference,string DecisionReference,DateTimeOffset EffectiveFrom,DateTimeOffset ValidUntil,int?StoredWorkersLimit,int?StoredBranchesLimit,Dictionary<string,bool>Entitlements);
