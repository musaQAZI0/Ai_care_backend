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
[Route("api/phase1/finance/vat")]
public sealed class VatTreatmentsController(CareDbContext db,ITenantContext tenant,ICurrentUserContext user):ControllerBase
{
 [HttpGet("legacy-findings")]public async Task<IActionResult> LegacyFindings(CancellationToken t)=>Ok(await Rows("select * from finance_vat_legacy_findings where organization_id=@org and (@wide or branch_id=@branch) order by invoice_id",_=>{},t));
 [HttpGet("authorities")]public async Task<IActionResult> Authorities(CancellationToken t)=>Ok(await Rows("select * from finance_vat_authorities where organization_id=@org and (@wide or branch_id=@branch) order by created_at desc",_=>{},t));
 [HttpGet("treatments")]public async Task<IActionResult> Treatments(CancellationToken t)=>Ok(await Rows("select * from finance_vat_treatments where organization_id=@org and (@wide or branch_id=@branch) order by created_at desc",_=>{},t));
 [HttpPost("authorities")]public Task<IActionResult> CreateAuthority(VatAuthorityRequest r,CancellationToken t)=>Tx(async()=>
 {
  if(r.RequestId==Guid.Empty||r.AdviserUserId==Guid.Empty||!Scope(r.BranchId)||r.ValidFrom==default||r.ValidTo<r.ValidFrom||string.IsNullOrWhiteSpace(r.QualificationReference))return BadRequest(new{message="An identified adviser, scope, validity dates and externally verified qualification/authority reference are required."});var snapshot=JsonSerializer.Serialize(r);var replay=await Replay("finance_vat_authorities",r.RequestId,snapshot,t);if(replay is not null)return replay;
  await Exec("insert into finance_vat_authorities(id,organization_id,branch_id,adviser_user_id,qualification_reference,valid_from,valid_to,created_by,request_snapshot) values(@id,@org,@target,@adviser,@reference,@start,@end,@actor,cast(@snapshot as jsonb))",c=>{Add(c,"id",r.RequestId);Add(c,"target",r.BranchId);Add(c,"adviser",r.AdviserUserId);Add(c,"reference",r.QualificationReference.Trim());Add(c,"start",r.ValidFrom);Add(c,"end",r.ValidTo);Add(c,"snapshot",snapshot);},t);await Audit("VAT_AUTHORITY_CREATED",r.RequestId,r.BranchId,t);return Created("",new{id=r.RequestId});
 },t);
 [HttpPost("authorities/{id:guid}/approve")]public Task<IActionResult> ApproveAuthority(Guid id,CancellationToken t)=>Tx(async()=>
 {
  await using var c=await Command("update finance_vat_authorities set approved_by=@actor,approved_at=now() where id=@id and organization_id=@org and (@wide or branch_id=@branch) and approved_by is null and created_by<>@actor and adviser_user_id<>@actor returning branch_id",t);Add(c,"id",id);if(await c.ExecuteScalarAsync(t) is not Guid branch)return Conflict(new{message="A third person must approve the adviser's authority."});await Audit("VAT_AUTHORITY_APPROVED",id,branch,t);return Ok(new{id});
 },t);
 [HttpPost("treatments")]public Task<IActionResult> Create(VatTreatmentRequest r,CancellationToken t)=>Tx(async()=>
 {
  if(r.RequestId==Guid.Empty||!Scope(r.BranchId)||string.IsNullOrWhiteSpace(r.ServiceType)||r.ValidFrom==default||r.ValidTo<r.ValidFrom||r.Treatment is not("Exempt" or "ZeroRated" or "Taxable")||r.Rate<0||r.Rate>100||decimal.Round(r.Rate,4)!=r.Rate||(r.Treatment=="Taxable"?r.Rate<=0:r.Rate!=0)||r.TaxPoint is not("ServiceDate" or "InvoiceDate")||r.RoundingMode is not("AwayFromZero" or "ToEven")||string.IsNullOrWhiteSpace(r.Reference))return BadRequest(new{message="Explicit supply type, treatment, applicable rate, effective dates, tax point, rounding and adviser decision reference are required."});
  var snapshot=JsonSerializer.Serialize(r);var replay=await Replay("finance_vat_treatments",r.RequestId,snapshot,t);if(replay is not null)return replay;
  await Exec("insert into finance_vat_treatments(id,organization_id,branch_id,service_type,valid_from,valid_to,treatment,rate,tax_point,rounding_mode,reference,created_by,request_snapshot) values(@id,@org,@target,@service,@start,@end,@treatment,@rate,@point,@rounding,@reference,@actor,cast(@snapshot as jsonb))",c=>{Add(c,"id",r.RequestId);Add(c,"target",r.BranchId);Add(c,"service",r.ServiceType.Trim());Add(c,"start",r.ValidFrom);Add(c,"end",r.ValidTo);Add(c,"treatment",r.Treatment);Add(c,"rate",r.Rate);Add(c,"point",r.TaxPoint);Add(c,"rounding",r.RoundingMode);Add(c,"reference",r.Reference.Trim());Add(c,"snapshot",snapshot);},t);await Audit("VAT_TREATMENT_CREATED",r.RequestId,r.BranchId,t);return Created("",new{id=r.RequestId});
 },t);
 [HttpPost("treatments/{id:guid}/approve")]public Task<IActionResult> ApproveTreatment(Guid id,VatApprovalRequest r,CancellationToken t)=>Tx(async()=>
 {
  await using var c=await Command("update finance_vat_treatments set approved_by=@actor,approved_at=now(),authority_id=@authority where id=@id and organization_id=@org and (@wide or branch_id=@branch) and approved_by is null and created_by<>@actor returning branch_id",t);Add(c,"id",id);Add(c,"authority",r.AuthorityId);if(await c.ExecuteScalarAsync(t) is not Guid branch)return Conflict(new{message="Only a different authorised adviser can approve a current treatment draft."});await Audit("VAT_TREATMENT_APPROVED",id,branch,t);return Ok(new{id});
 },t);
 bool Scope(Guid branch)=>tenant.IsOrganizationWide||tenant.BranchId==branch;
 async Task<IActionResult?>Replay(string table,Guid id,string snapshot,CancellationToken t){var rows=await Rows($"select request_snapshot=cast(@snapshot as jsonb) matches from {table} where id=@id and organization_id=@org and (@wide or branch_id=@branch)",c=>{Add(c,"id",id);Add(c,"snapshot",snapshot);},t);return rows.Count==0?null:rows[0].GetProperty("matches").GetBoolean()?Ok(new{id,replayed=true}):Conflict(new{message="Request ID has different details."});}
 async Task<IActionResult>Tx(Func<Task<IActionResult>>work,CancellationToken t){try{return await db.Database.CreateExecutionStrategy().ExecuteAsync(async()=>{await using var tx=await db.Database.BeginTransactionAsync(t);await Exec("select pg_advisory_xact_lock(hashtextextended(@key,0))",c=>Add(c,"key",$"finance-billing:{tenant.OrganizationId}"),t);var result=await work();if(result is ObjectResult o&&o.StatusCode>=400){await tx.RollbackAsync(t);return result;}await tx.CommitAsync(t);return result;});}catch(Npgsql.PostgresException e)when(e.SqlState is "23514" or "23505" or "23503"){return Conflict(new{message="VAT authority, dates, overlap or immutable history conflict."});}}
 async Task<List<JsonElement>>Rows(string sql,Action<DbCommand>b,CancellationToken t){var rows=new List<JsonElement>();await using var c=await Command($"select row_to_json(q)::text from ({sql})q",t);b(c);await using var r=await c.ExecuteReaderAsync(t);while(await r.ReadAsync(t))rows.Add(JsonSerializer.Deserialize<JsonElement>(r.GetString(0)));return rows;}
 async Task Exec(string sql,Action<DbCommand>b,CancellationToken t){await using var c=await Command(sql,t);b(c);await c.ExecuteNonQueryAsync(t);}
 async Task<DbCommand>Command(string sql,CancellationToken t){var n=db.Database.GetDbConnection();if(n.State!=ConnectionState.Open)await n.OpenAsync(t);var c=n.CreateCommand();c.Transaction=db.Database.CurrentTransaction?.GetDbTransaction();c.CommandText=sql;Add(c,"org",tenant.OrganizationId);Add(c,"branch",tenant.BranchId??Guid.Empty);Add(c,"wide",tenant.IsOrganizationWide);Add(c,"actor",user.UserId);return c;}
 static void Add(DbCommand c,string name,object?value){var p=c.CreateParameter();p.ParameterName=name;p.Value=value??DBNull.Value;c.Parameters.Add(p);}
 async Task Audit(string action,Guid id,Guid branch,CancellationToken t){db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),action,user.UserName,"VatConfiguration",id,DateTimeOffset.UtcNow,tenant.OrganizationId,branch));await db.SaveChangesAsync(t);}
}
public sealed record VatAuthorityRequest(Guid RequestId,Guid BranchId,Guid AdviserUserId,string QualificationReference,DateOnly ValidFrom,DateOnly ValidTo);
public sealed record VatTreatmentRequest(Guid RequestId,Guid BranchId,string ServiceType,DateOnly ValidFrom,DateOnly ValidTo,string Treatment,decimal Rate,string TaxPoint,string RoundingMode,string Reference);
public sealed record VatApprovalRequest(Guid AuthorityId);
