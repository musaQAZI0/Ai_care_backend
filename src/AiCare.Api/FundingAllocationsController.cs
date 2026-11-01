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

[ApiController,Authorize(Roles="Administrator,BackOffice,CareManager")]
[Route("api/phase1/finance/funding-allocations")]
public sealed class FundingAllocationsController(CareDbContext db,ITenantContext tenant,ICurrentUserContext user):ControllerBase
{
 [HttpGet]
 public async Task<IActionResult> List(CancellationToken t)=>Ok(await Rows("""
  select a.*, (select jsonb_agg(jsonb_build_object('funderId',r.funder_id,'funder',f.name,'type',r.rule_type,'value',r.value,'priority',r.priority) order by r.priority)
   from finance_allocation_rules r join finance_funders f on f.id=r.funder_id and f.organization_id=a.organization_id where r.allocation_version_id=a.id) rules
  from finance_allocation_versions a where a.organization_id=@org and (@wide or a.branch_id=@branch) order by a.created_at desc
  """,_=>{},t));

 [HttpPost("preview")]
 public IActionResult Preview(AllocationPreviewRequest r)
 {
  try{return Ok(new{total=r.Total,shares=FundingShareCalculation.Calculate(r.Total,r.RoundingPolicy,r.Rules)});}
  catch(ArgumentException e){return BadRequest(new{message=e.Message});}
 }
 [HttpPost]
 public Task<IActionResult> Create(FundingAllocationRequest r,CancellationToken t)=>Tx(async()=>
 {
  if(!user.IsAdministrator&&!user.IsBackOffice)return Forbid();if(user.UserId is null)return Unauthorized();
  if(r.RequestId==Guid.Empty||r.ValidFrom==default||r.ValidTo<r.ValidFrom||string.IsNullOrWhiteSpace(r.Reference)||r.Rules is null)return BadRequest(new{message="Request ID, effective dates, policy reference and allocation rules are required."});
  try{FundingShareCalculation.ValidateRules(r.RoundingPolicy,r.Rules);}catch(ArgumentException e){return BadRequest(new{message=e.Message});}
  var contracts=await Rows("select id,branch_id,status from finance_contract_versions where id=@contract and organization_id=@org and (@wide or branch_id=@branch) for update",c=>Add(c,"contract",r.ContractVersionId),t);
  if(contracts.Count!=1||contracts[0].GetProperty("status").GetString()!="Approved")return Conflict(new{message="Select an accessible approved contract version."});
  var branch=contracts[0].GetProperty("branch_id").GetGuid();var snapshot=JsonSerializer.Serialize(r);
  await using(var replay=await Command("select request_snapshot=cast(@request as jsonb) from finance_allocation_versions where id=@id and organization_id=@org and branch_id=@target",t))
  {
   Add(replay,"request",snapshot);Add(replay,"id",r.RequestId);Add(replay,"target",branch);var match=await replay.ExecuteScalarAsync(t);
   if(match is bool matches)return matches?Ok(new{id=r.RequestId,replayed=true}):Conflict(new{message="Allocation request ID already has different details."});
  }
  await Exec("""
   insert into finance_allocation_versions(id,contract_version_id,organization_id,branch_id,version,valid_from,valid_to,reference,rounding_policy,request_snapshot,created_by)
   values(@id,@contract,@org,@target,(select coalesce(max(version),0)+1 from finance_allocation_versions where contract_version_id=@contract),@start,@end,@reference,@policy,cast(@request as jsonb),@actor)
   """,c=>{Add(c,"id",r.RequestId);Add(c,"contract",r.ContractVersionId);Add(c,"target",branch);Add(c,"start",r.ValidFrom);Add(c,"end",r.ValidTo);Add(c,"reference",r.Reference.Trim());Add(c,"policy",r.RoundingPolicy);Add(c,"request",snapshot);},t);
  foreach(var rule in r.Rules)await Exec("insert into finance_allocation_rules(id,allocation_version_id,funder_id,rule_type,value,priority) values(@id,@allocation,@funder,@type,@value,@priority)",c=>{Add(c,"id",Guid.NewGuid());Add(c,"allocation",r.RequestId);Add(c,"funder",rule.FunderId);Add(c,"type",rule.Type);Add(c,"value",rule.Value);Add(c,"priority",rule.Priority);},t);
  Audit("FUNDING_ALLOCATION_DRAFTED",r.RequestId,branch);await db.SaveChangesAsync(t);return Created($"/api/phase1/finance/funding-allocations/{r.RequestId}",new{id=r.RequestId});
 },t);

 [HttpPost("{id:guid}/approve")]
 public Task<IActionResult> Approve(Guid id,BillingContractDecision r,CancellationToken t)=>Tx(async()=>
 {
  if(!user.IsAdministrator&&!user.IsBackOffice)return Forbid();if(string.IsNullOrWhiteSpace(r.Reason))return BadRequest(new{message="An approval reason is required."});
  await using var command=await Command("update finance_allocation_versions set approved_by=@actor,approved_at=now(),approval_reason=@reason where id=@id and organization_id=@org and (@wide or branch_id=@branch) and approved_by is null and created_by<>@actor returning branch_id",t);Add(command,"id",id);Add(command,"reason",r.Reason.Trim());
  if(await command.ExecuteScalarAsync(t) is not Guid branch)
  {
   await using var replay=await Command("select count(*) from finance_allocation_versions where id=@id and organization_id=@org and (@wide or branch_id=@branch) and approved_by=@actor and approval_reason=@reason",t);Add(replay,"id",id);Add(replay,"reason",r.Reason.Trim());
   return Convert.ToInt32(await replay.ExecuteScalarAsync(t))==1?Ok(new{id,replayed=true}):Conflict(new{message="An independent reviewer must approve the allocation draft."});
  }
  Audit("FUNDING_ALLOCATION_APPROVED",id,branch);await db.SaveChangesAsync(t);return Ok(new{id});
 },t);
 [HttpGet("period-caps")]
 public async Task<IActionResult> PeriodCaps(CancellationToken t)=>Ok(await Rows("""
  select a.*,trim(c.currency) currency,
   (select coalesce(sum(e.final_amount),0) from finance_billable_events e where e.funding_arrangement_id=a.funding_arrangement_id and e.service_date between a.valid_from and a.valid_to and e.status<>'Rejected') committed_amount,
   (select coalesce(sum(e.billing_minutes),0) from finance_billable_events e where e.funding_arrangement_id=a.funding_arrangement_id and e.service_date between a.valid_from and a.valid_to and e.status<>'Rejected') committed_minutes
  from finance_period_authorisations a join finance_contract_versions c on c.id=a.contract_version_id where a.organization_id=@org and (@wide or a.branch_id=@branch) order by a.valid_from,a.id
  """,_=>{},t));
 [HttpPost("period-caps")]
 public Task<IActionResult> CreatePeriodCap(PeriodAuthorisationRequest r,CancellationToken t)=>Tx(async()=>
 {
  if(!user.IsAdministrator&&!user.IsBackOffice)return Forbid();if(user.UserId is null)return Unauthorized();
  if(r.RequestId==Guid.Empty||r.ValidFrom==default||r.ValidTo<r.ValidFrom||string.IsNullOrWhiteSpace(r.Reference)||(r.AmountLimit is null&&r.MinutesLimit is null)||r.AmountLimit is <=0||r.MinutesLimit is <=0||(r.AmountLimit is decimal money&&(decimal.Round(money,2)!=money||money>9999999999.99m))||(r.MinutesLimit is decimal minutes&&(decimal.Round(minutes,4)!=minutes||minutes>999999999999.9999m)))return BadRequest(new{message="Explicit dates, authorisation reference and positive amount/minute limits are required."});
  var contracts=await Rows("select * from finance_contract_versions where id=@contract and organization_id=@org and (@wide or branch_id=@branch) and status='Approved'",c=>Add(c,"contract",r.ContractVersionId),t);if(contracts.Count!=1)return NotFound();var contract=contracts[0];var branch=contract.GetProperty("branch_id").GetGuid();var snapshot=JsonSerializer.Serialize(r);
  await using(var replay=await Command("select request_snapshot=cast(@request as jsonb) from finance_period_authorisations where id=@id and organization_id=@org and branch_id=@target",t))
  {Add(replay,"request",snapshot);Add(replay,"id",r.RequestId);Add(replay,"target",branch);if(await replay.ExecuteScalarAsync(t) is bool match)return match?Ok(new{id=r.RequestId,replayed=true}):Conflict(new{message="Period authorisation request ID already contains different details."});}
  await Exec("insert into finance_period_authorisations(id,contract_version_id,funding_arrangement_id,organization_id,branch_id,valid_from,valid_to,reference,amount_limit,minutes_limit,request_snapshot,created_by) values(@id,@contract,@funding,@org,@target,@start,@end,@reference,@amount,@minutes,cast(@request as jsonb),@actor)",c=>{Add(c,"id",r.RequestId);Add(c,"contract",r.ContractVersionId);Add(c,"funding",contract.GetProperty("funding_arrangement_id").GetGuid());Add(c,"target",branch);Add(c,"start",r.ValidFrom);Add(c,"end",r.ValidTo);Add(c,"reference",r.Reference.Trim());var amount=c.CreateParameter();amount.ParameterName="amount";amount.DbType=DbType.Decimal;amount.Value=(object?)r.AmountLimit??DBNull.Value;c.Parameters.Add(amount);var minutes=c.CreateParameter();minutes.ParameterName="minutes";minutes.DbType=DbType.Decimal;minutes.Value=(object?)r.MinutesLimit??DBNull.Value;c.Parameters.Add(minutes);Add(c,"request",snapshot);},t);
  Audit("PERIOD_AUTHORISATION_DRAFTED",r.RequestId,branch);await db.SaveChangesAsync(t);return Created($"/api/phase1/finance/funding-allocations/period-caps/{r.RequestId}",new{id=r.RequestId});
 },t);
 [HttpPost("period-caps/{id:guid}/approve")]
 public Task<IActionResult> ApprovePeriodCap(Guid id,BillingContractDecision r,CancellationToken t)=>Tx(async()=>
 {
  if(!user.IsAdministrator&&!user.IsBackOffice)return Forbid();if(string.IsNullOrWhiteSpace(r.Reason))return BadRequest(new{message="An independent approval reason is required."});
  await using var c=await Command("update finance_period_authorisations set approved_by=@actor,approved_at=now(),approval_reason=@reason where id=@id and organization_id=@org and (@wide or branch_id=@branch) and approved_by is null and created_by<>@actor returning branch_id",t);Add(c,"id",id);Add(c,"reason",r.Reason.Trim());
  if(await c.ExecuteScalarAsync(t) is not Guid branch){await using var replay=await Command("select count(*) from finance_period_authorisations where id=@id and organization_id=@org and (@wide or branch_id=@branch) and approved_by=@actor and approval_reason=@reason",t);Add(replay,"id",id);Add(replay,"reason",r.Reason.Trim());return Convert.ToInt32(await replay.ExecuteScalarAsync(t))==1?Ok(new{id,replayed=true}):Conflict(new{message="Independent approval of a current period draft is required."});}
  Audit("PERIOD_AUTHORISATION_APPROVED",id,branch);await db.SaveChangesAsync(t);return Ok(new{id});
 },t);
 async Task<IActionResult> Tx(Func<Task<IActionResult>> action,CancellationToken t)
 {
  try{return await db.Database.CreateExecutionStrategy().ExecuteAsync(async()=>{await using var tx=await db.Database.BeginTransactionAsync(t);await Exec("select pg_advisory_xact_lock(hashtextextended(@lock,0))",c=>Add(c,"lock",$"finance-billing:{tenant.OrganizationId}"),t);var result=await action();await tx.CommitAsync(t);return result;});}
  catch(Npgsql.PostgresException e)when(e.SqlState is "23514" or "23505" or "23P01"){return Conflict(new{message="Allocation scope, currency, effective dates or version conflict. Reload before retrying."});}
 }
 async Task<List<JsonElement>> Rows(string sql,Action<DbCommand> bind,CancellationToken t){var result=new List<JsonElement>();await using var c=await Command($"select row_to_json(q)::text from ({sql}) q",t);bind(c);await using var r=await c.ExecuteReaderAsync(t);while(await r.ReadAsync(t))result.Add(JsonSerializer.Deserialize<JsonElement>(r.GetString(0)));return result;}
 async Task<int> Exec(string sql,Action<DbCommand> bind,CancellationToken t){await using var c=await Command(sql,t);bind(c);return await c.ExecuteNonQueryAsync(t);}
 async Task<DbCommand> Command(string sql,CancellationToken t){var connection=db.Database.GetDbConnection();if(connection.State!=ConnectionState.Open)await connection.OpenAsync(t);var c=connection.CreateCommand();c.Transaction=db.Database.CurrentTransaction?.GetDbTransaction();c.CommandText=sql;Add(c,"org",tenant.OrganizationId);Add(c,"branch",tenant.BranchId??Guid.Empty);Add(c,"wide",tenant.IsOrganizationWide);Add(c,"actor",user.UserId);return c;}
 static void Add(DbCommand c,string name,object? value){var p=c.CreateParameter();p.ParameterName=name;p.Value=value??DBNull.Value;c.Parameters.Add(p);}
 void Audit(string action,Guid id,Guid branch)=>db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),action,user.UserName,"FundingAllocation",id,DateTimeOffset.UtcNow,tenant.OrganizationId,branch));
}
public sealed record FundingAllocationRequest(Guid RequestId,Guid ContractVersionId,DateOnly ValidFrom,DateOnly ValidTo,string Reference,string RoundingPolicy,List<FundingShareRule> Rules);
public sealed record AllocationPreviewRequest(decimal Total,string RoundingPolicy,List<FundingShareRule> Rules);

public sealed record PeriodAuthorisationRequest(Guid RequestId,Guid ContractVersionId,DateOnly ValidFrom,DateOnly ValidTo,string Reference,decimal? AmountLimit,decimal? MinutesLimit);
