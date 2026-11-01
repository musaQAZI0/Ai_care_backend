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
[Route("api/phase1/finance/exports")]
[SubscriptionEntitlement("FinanceExports")]
public sealed class FinanceExportsController(CareDbContext db,ITenantContext tenant,ICurrentUserContext user):ControllerBase
{
 [HttpGet("permissions")]public async Task<IActionResult> Permissions(CancellationToken t)=>Ok(await Rows("select * from finance_export_permissions where organization_id=@org and (@wide or branch_id=@branch) order by created_at desc",_=>{},t));
 [HttpPost("permissions")]public async Task<IActionResult> CreatePermission(FinanceExportPermissionRequest r,CancellationToken t)
 {
  if(user.UserId is null)return Unauthorized();if(r.RequestId==Guid.Empty||r.UserId==Guid.Empty||r.ExportKind is not("Ledger" or "Claims" or "Payroll" or "InvoiceDocuments")||r.ValidFrom==default||r.ValidTo<r.ValidFrom||string.IsNullOrWhiteSpace(r.Reference)||(!tenant.IsOrganizationWide&&r.BranchId!=tenant.BranchId))return BadRequest(new{message="Explicit scope, beneficiary, export kind, dates and authority reference are required."});
  try{return await db.Database.CreateExecutionStrategy().ExecuteAsync(async()=>{await using var tx=await db.Database.BeginTransactionAsync(t);await Exec("select pg_advisory_xact_lock(hashtextextended(@key,0))",c=>Add(c,"key",$"finance-export-authority:{tenant.OrganizationId}"),t);
   var existing=await Rows("select * from finance_export_permissions where id=@id and organization_id=@org and (@wide or branch_id=@branch)",c=>Add(c,"id",r.RequestId),t);
   if(existing.Count>0){var x=existing[0];return (IActionResult)(x.GetProperty("user_id").GetGuid()==r.UserId&&x.GetProperty("branch_id").GetGuid()==r.BranchId&&x.GetProperty("export_kind").GetString()==r.ExportKind&&x.GetProperty("allow_identifiers").GetBoolean()==r.AllowIdentifiers&&x.GetProperty("allow_rates").GetBoolean()==r.AllowRates&&x.GetProperty("reference").GetString()==r.Reference.Trim()&&x.GetProperty("valid_from").GetString()==r.ValidFrom.ToString("yyyy-MM-dd")&&x.GetProperty("valid_to").GetString()==r.ValidTo.ToString("yyyy-MM-dd")?Ok(new{id=r.RequestId,replayed=true}):Conflict(new{message="Permission ID already has different details."}));}
   await Exec("insert into finance_export_permissions(id,organization_id,branch_id,user_id,export_kind,allow_identifiers,allow_rates,valid_from,valid_to,reference,created_by) values(@id,@org,@target,@beneficiary,@kind,@identifiers,@rates,@start,@end,@reference,@actor)",c=>{Add(c,"id",r.RequestId);Add(c,"target",r.BranchId);Add(c,"beneficiary",r.UserId);Add(c,"kind",r.ExportKind);Add(c,"identifiers",r.AllowIdentifiers);Add(c,"rates",r.AllowRates);Add(c,"start",r.ValidFrom);Add(c,"end",r.ValidTo);Add(c,"reference",r.Reference.Trim());},t);await Audit("FINANCE_EXPORT_PERMISSION_CREATED",r.RequestId,r.BranchId,t);await tx.CommitAsync(t);return Created($"/api/phase1/finance/exports/permissions/{r.RequestId}",new{id=r.RequestId});});}
  catch(Npgsql.PostgresException e)when(e.SqlState is "23514" or "23505"){return Conflict(new{message="Export authority scope or request conflicts with existing history."});}
 }
 [HttpPost("permissions/{id:guid}/approve")]public Task<IActionResult> Approve(Guid id,CancellationToken t)=>db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async()=>
 {
  await using var tx=await db.Database.BeginTransactionAsync(t);await using var c=await Command("update finance_export_permissions set approved_by=@actor,approved_at=now() where id=@id and organization_id=@org and (@wide or branch_id=@branch) and approved_by is null and created_by<>@actor and user_id<>@actor returning branch_id",t);Add(c,"id",id);if(await c.ExecuteScalarAsync(t) is not Guid branch)return Conflict(new{message="Approval requires a third person distinct from creator and beneficiary."});await Audit("FINANCE_EXPORT_PERMISSION_APPROVED",id,branch,t);await tx.CommitAsync(t);return Ok(new{id});
 });
 [HttpGet("{kind}")]public async Task<IActionResult> Download(string kind,[FromQuery]Guid branchId,[FromQuery]DateOnly from,[FromQuery]DateOnly to,[FromQuery]string format="csv",CancellationToken t=default)
 {
  if(kind is not("Ledger" or "Claims")||format is not("csv" or "xlsx" or "pdf")||from==default||to<from||to==DateOnly.MaxValue)return BadRequest(new{message="Select Ledger or Claims, inclusive UK dates and csv/xlsx/pdf."});
  if(!tenant.IsOrganizationWide&&branchId!=tenant.BranchId)return Forbid();
  var permissions=await Rows("select * from finance_export_permissions where organization_id=@org and branch_id=@target and user_id=@actor and export_kind=@kind and approved_by is not null and (now() at time zone 'Europe/London')::date between valid_from and valid_to order by allow_identifiers desc,allow_rates desc,id",c=>{Add(c,"target",branchId);Add(c,"kind",kind);},t);if(permissions.Count==0)return Forbid();
  // Use a single approved grant, never combine separate grants into broader access.
  var permission=permissions[0];var identifiers=permission.GetProperty("allow_identifiers").GetBoolean();var rates=permission.GetProperty("allow_rates").GetBoolean();
  var sql=kind=="Ledger"?"select invoice_id,invoice_date,currency,gross_amount,credits,write_offs,net_cash,outstanding,customer_credit,status from finance_invoice_ledger where organization_id=@org and branch_id=@target and invoice_date between @start and @end order by invoice_date,invoice_id":"select id,care_worker_id,claim_date,claim_type,currency,quantity,unit_rate,amount,status from finance_workforce_claims where organization_id=@org and branch_id=@target and claim_date between @start and @end order by claim_date,id";
  var records=await Rows(sql,c=>{Add(c,"target",branchId);Add(c,"start",from);Add(c,"end",to);},t);
  var headers=kind=="Ledger"?new[]{"invoice_id","invoice_date","currency","gross_amount","credits","write_offs","net_cash","outstanding","customer_credit","status"}:new[]{"id","care_worker_id","claim_date","claim_type","currency","quantity","unit_rate","amount","status"};
  var columns=headers.Where(h=>(identifiers||h is not("invoice_id" or "care_worker_id" or "id"))&&(rates||h is not("unit_rate" or "quantity"))).ToArray();
  var rows=records.Select(r=>columns.Select(h=>Value(r.GetProperty(h))).ToArray()).ToArray();
  if(format=="pdf"&&!FinanceExportDocument.SupportsPdf(columns,rows))return UnprocessableEntity(new{message="This data requires a Unicode font. Select CSV or XLSX to preserve every character."});
  await Audit($"FINANCE_EXPORT_DOWNLOADED:{kind}:{format}:identifiers={identifiers}:rates={rates}:from={from:yyyy-MM-dd}:to={to:yyyy-MM-dd}:rows={rows.Length}",permission.GetProperty("id").GetGuid(),branchId,t);
  return Render(kind,format,columns,rows);
 }
 [NonAction]public FileContentResult Render(string kind,string format,string[] headers,object?[][] rows)=>format switch{"xlsx"=>File(FinanceExportDocument.Xlsx(headers,rows),"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",$"{kind}.xlsx"),"pdf"=>File(FinanceExportDocument.Pdf(kind,headers,rows),"application/pdf",$"{kind}.pdf"),_=>File(FinanceExportDocument.Csv(headers,rows),"text/csv",$"{kind}.csv")};
 static object? Value(JsonElement x)=>x.ValueKind switch{JsonValueKind.Null=>null,JsonValueKind.Number=>x.GetDecimal(),JsonValueKind.True=>true,JsonValueKind.False=>false,_=>x.GetString()};
 async Task<List<JsonElement>>Rows(string sql,Action<DbCommand>b,CancellationToken t){var rows=new List<JsonElement>();await using var c=await Command($"select row_to_json(q)::text from ({sql})q",t);b(c);await using var r=await c.ExecuteReaderAsync(t);while(await r.ReadAsync(t))rows.Add(JsonSerializer.Deserialize<JsonElement>(r.GetString(0)));return rows;}
 async Task Exec(string sql,Action<DbCommand>b,CancellationToken t){await using var c=await Command(sql,t);b(c);await c.ExecuteNonQueryAsync(t);}
 async Task<DbCommand> Command(string sql,CancellationToken t){var n=db.Database.GetDbConnection();if(n.State!=ConnectionState.Open)await n.OpenAsync(t);var c=n.CreateCommand();c.Transaction=db.Database.CurrentTransaction?.GetDbTransaction();c.CommandText=sql;Add(c,"org",tenant.OrganizationId);Add(c,"branch",tenant.BranchId??Guid.Empty);Add(c,"wide",tenant.IsOrganizationWide);Add(c,"actor",user.UserId);return c;}
 static void Add(DbCommand c,string name,object? value){var p=c.CreateParameter();p.ParameterName=name;p.Value=value??DBNull.Value;c.Parameters.Add(p);}
 async Task Audit(string action,Guid id,Guid branch,CancellationToken t){db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),action,user.UserName,"FinanceExport",id,DateTimeOffset.UtcNow,tenant.OrganizationId,branch));await db.SaveChangesAsync(t);}
}
public sealed record FinanceExportPermissionRequest(Guid RequestId,Guid BranchId,Guid UserId,string ExportKind,bool AllowIdentifiers,bool AllowRates,DateOnly ValidFrom,DateOnly ValidTo,string Reference);
