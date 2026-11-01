using System.Data;using System.Data.Common;using AiCare.Application;using AiCare.Domain;using AiCare.Infrastructure;using Microsoft.AspNetCore.Authorization;using Microsoft.AspNetCore.Mvc;using Microsoft.EntityFrameworkCore;using Microsoft.EntityFrameworkCore.Storage;
namespace AiCare.Api;
[ApiController,Authorize(Policy="Phase1User")][Route("api/phase1/finance/workforce-claims")]
public sealed partial class WorkforceFinanceController(CareDbContext db,ITenantContext tenant,ICurrentUserContext user):ControllerBase
{
 Guid Branch=>tenant.BranchId??TenantDefaults.BranchId;bool Review=>user.IsAdministrator||user.IsBackOffice||user.IsCareManager;
 [HttpGet]public async Task<IActionResult>List([FromQuery]string?status,CancellationToken t){if(!Review&&!user.IsCareWorker)return Forbid();return Ok(await Rows("select id,care_worker_id,visit_id,claim_type,claim_date,quantity,unit_rate,amount,category,notes,evidence_reference,status,policy_id,source_reference,currency,submitted_by_user_id,evidence_id,receipt_issuer,receipt_number from finance_workforce_claims where organization_id=@org and (@wide or branch_id=@branch) and (@review or care_worker_id=@worker) and (coalesce(@status,'')='' or status=@status) order by claim_date desc",c=>{Add(c,"review",Review);Add(c,"worker",user.CareWorkerId??Guid.Empty);Add(c,"status",status??"");},t));}
 [HttpGet("policies")]
 public async Task<IActionResult> Policies(CancellationToken t)
 {
  if(!Review&&!user.IsCareWorker)return Forbid();
  return Ok(await Rows("select * from finance_claim_policies where organization_id=@org and (@wide or branch_id=@branch) and (@review or approved_by is not null) order by valid_from desc",c=>Add(c,"review",Review),t));
 }
 [HttpPost("policies")]
 public Task<IActionResult> CreatePolicy(ClaimPolicyRequest r,CancellationToken t)=>Tx(async()=>
 {
  if(!user.IsAdministrator&&!user.IsBackOffice)return Forbid();if(user.UserId is null)return Unauthorized();
  if(string.IsNullOrWhiteSpace(r.Reference)||string.IsNullOrWhiteSpace(r.Category)||r.ClaimType is not ("Mileage" or "Expense")||r.ValidFrom==default||r.ValidTo<r.ValidFrom||r.RoundingMode is not ("AwayFromZero" or "ToEven")||r.UnitRate<=0||decimal.Round(r.UnitRate,4)!=r.UnitRate||r.MaximumAmount<=0||decimal.Round(r.MaximumAmount,2)!=r.MaximumAmount||r.Currency.Length!=3||!r.Currency.All(char.IsAsciiLetter))return BadRequest(new{message="Explicit policy reference, dates, category, currency, positive rate and maximum are required."});
  if(!tenant.IsOrganizationWide&&r.BranchId!=tenant.BranchId)return NotFound();
  var id=Guid.NewGuid();await Exec("insert into finance_claim_policies(id,organization_id,branch_id,reference,claim_type,category,valid_from,valid_to,currency,unit_rate,maximum_amount,evidence_required,visit_required,rounding_mode,created_by) values(@id,@org,@target,@reference,@type,@category,@start,@end,@currency,@rate,@maximum,@evidence,@visit,@rounding,@actorId)",c=>{Add(c,"id",id);Add(c,"target",r.BranchId);Add(c,"reference",r.Reference.Trim());Add(c,"type",r.ClaimType);Add(c,"category",r.Category.Trim());Add(c,"start",r.ValidFrom);Add(c,"end",r.ValidTo);Add(c,"currency",r.Currency.ToUpperInvariant());Add(c,"rate",r.UnitRate);Add(c,"maximum",r.MaximumAmount);Add(c,"evidence",r.EvidenceRequired);Add(c,"visit",r.VisitRequired);Add(c,"rounding",r.RoundingMode);},t);
  Audit("CLAIM_POLICY_CREATED",id,r.BranchId);await db.SaveChangesAsync(t);return Created($"/api/phase1/finance/workforce-claims/policies/{id}",new{id});
 },t);
 [HttpPost("policies/{id:guid}/approve")]
 public Task<IActionResult> ApprovePolicy(Guid id,CancellationToken t)=>Tx(async()=>
 {
  if(!user.IsAdministrator&&!user.IsBackOffice)return Forbid();
  await using var command=await Cmd("update finance_claim_policies set approved_by=@actorId,approved_at=now() where id=@id and organization_id=@org and (@wide or branch_id=@branch) and approved_by is null and created_by<>@actorId returning branch_id",t);Add(command,"id",id);
  if(await command.ExecuteScalarAsync(t) is not Guid branch)return Conflict(new{message="An independent reviewer must approve a current policy draft."});
  Audit("CLAIM_POLICY_APPROVED",id,branch);await db.SaveChangesAsync(t);return Ok(new{id});
 },t);
 [HttpPost]
 public Task<IActionResult> Create(WorkforceClaimRequest r,CancellationToken t)=>Tx(async()=>
 {
  if(user.UserId is null)return Unauthorized();if(user.CareWorkerId is null&&!Review)return Forbid();var worker=r.CareWorkerId??user.CareWorkerId;
  if(worker is null)return BadRequest(new{message="Care worker is required."});if(!Review&&worker!=user.CareWorkerId)return Forbid();
  if(r.PolicyId is null||string.IsNullOrWhiteSpace(r.SourceReference)||r.Quantity<=0||decimal.Round(r.Quantity,2)!=r.Quantity||string.IsNullOrWhiteSpace(r.Notes))return BadRequest(new{message="Approved policy, unique trip/expense source reference, positive quantity and notes are required."});
  var workerRow=await db.CareWorkers.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==worker&&x.OrganizationId==tenant.OrganizationId&&(tenant.IsOrganizationWide||x.BranchId==tenant.BranchId),t);if(workerRow?.BranchId is not Guid target)return NotFound();
  var policies=await Rows("select * from finance_claim_policies where id=@policy and organization_id=@org and branch_id=@target and approved_by is not null and @date between valid_from and valid_to",c=>{Add(c,"policy",r.PolicyId);Add(c,"target",target);Add(c,"date",r.ClaimDate);},t);
  if(policies.Count!=1)return Conflict(new{message="No approved policy covers this worker branch and claim date."});
  var policy=policies[0];var rate=Convert.ToDecimal(policy["unit_rate"]);var type=Convert.ToString(policy["claim_type"])!;var category=Convert.ToString(policy["category"])!;var currency=Convert.ToString(policy["currency"])!.Trim();
  if(type!=r.ClaimType||category!=(r.Category??""))return BadRequest(new{message="Claim category/type must match the selected policy."});
  var amount=Math.Round(r.Quantity*rate,2,Convert.ToString(policy["rounding_mode"])=="ToEven"?MidpointRounding.ToEven:MidpointRounding.AwayFromZero);var id=Guid.NewGuid();
  var evidenceReference=string.IsNullOrWhiteSpace(r.EvidenceReference)?r.EvidenceId?.ToString()??"":r.EvidenceReference.Trim();
  var existing=await Rows("select id,quantity,policy_id,visit_id,claim_date,notes,evidence_reference,evidence_id,receipt_issuer,receipt_number,submitted_by_user_id from finance_workforce_claims where organization_id=@org and care_worker_id=@worker and claim_type=@type and source_reference=@source",c=>{Add(c,"worker",worker);Add(c,"type",type);Add(c,"source",r.SourceReference.Trim());},t);
  if(existing.Count>0){var prior=existing[0];return Equals(prior["submitted_by_user_id"],user.UserId)&&Convert.ToDecimal(prior["quantity"])==r.Quantity&&Equals(prior["policy_id"],r.PolicyId)&&Equals(prior["visit_id"],r.VisitId)&&(prior["claim_date"] is DateOnly savedDate?savedDate:DateOnly.FromDateTime(Convert.ToDateTime(prior["claim_date"])))==r.ClaimDate&&Convert.ToString(prior["notes"])==r.Notes.Trim()&&Convert.ToString(prior["evidence_reference"])==evidenceReference&&Equals(prior["evidence_id"],r.EvidenceId)&&Convert.ToString(prior["receipt_issuer"])==(r.ReceiptIssuer??"").Trim()&&Convert.ToString(prior["receipt_number"])==(r.ReceiptNumber??"").Trim()?Ok(new{id=prior["id"],replayed=true}):Conflict(new{message="Trip or expense reference already exists with different details or submitter."});}
  if(amount<=0||amount>Convert.ToDecimal(policy["maximum_amount"])||amount>9999999999.99m)return Conflict(new{message="Claim amount exceeds the approved policy or ledger limit."});
  if(type=="Expense"&&(string.IsNullOrWhiteSpace(r.ReceiptIssuer)||string.IsNullOrWhiteSpace(r.ReceiptNumber)))return BadRequest(new{message="Expense receipt issuer and number are required."});
  if((r.ReceiptIssuer??"").Length>200||(r.ReceiptNumber??"").Length>200||(r.ReceiptIssuer??"").Any(char.IsControl)||(r.ReceiptNumber??"").Any(char.IsControl))return BadRequest(new{message="Receipt identity must contain at most 200 printable characters per field."});
  if(Convert.ToBoolean(policy["evidence_required"])&&r.EvidenceId is null)return Conflict(new{message="Upload the receipt evidence document before submitting this claim."});
  if(r.EvidenceId is Guid evidenceId){var evidence=await Rows("select id from finance_claim_evidence where id=@evidence and organization_id=@org and branch_id=@target and care_worker_id=@worker",c=>{Add(c,"evidence",evidenceId);Add(c,"target",target);Add(c,"worker",worker);},t);if(evidence.Count!=1)return NotFound();}
  await Exec("insert into finance_workforce_claims(id,organization_id,branch_id,care_worker_id,visit_id,claim_type,claim_date,quantity,unit_rate,amount,category,notes,evidence_reference,submitted_by,submitted_by_user_id,policy_id,source_reference,currency,evidence_id,receipt_issuer,receipt_number) values(@id,@org,@target,@worker,@visit,@type,@date,@quantity,@rate,@amount,@category,@notes,@evidence,@actor,@actorId,@policy,@source,@currency,@evidenceId,@issuer,@receipt)",c=>{Add(c,"id",id);Add(c,"target",target);Add(c,"worker",worker);Add(c,"visit",r.VisitId);Add(c,"type",type);Add(c,"date",r.ClaimDate);Add(c,"quantity",r.Quantity);Add(c,"rate",rate);Add(c,"amount",amount);Add(c,"category",category);Add(c,"notes",r.Notes.Trim());Add(c,"evidence",evidenceReference);Add(c,"policy",r.PolicyId);Add(c,"source",r.SourceReference.Trim());Add(c,"currency",currency);Add(c,"evidenceId",r.EvidenceId);Add(c,"issuer",r.ReceiptIssuer?.Trim());Add(c,"receipt",r.ReceiptNumber?.Trim());},t);
  Audit("WORKFORCE_CLAIM_SUBMITTED",id,target);await db.SaveChangesAsync(t);return Created($"/api/phase1/finance/workforce-claims/{id}",new{id,amount,currency,status="Submitted"});
 },t);
 [HttpPost("{id:guid}/review")]
 public Task<IActionResult> ReviewClaim(Guid id,ReviewClaimRequest r,CancellationToken t)=>Tx(async()=>
 {
  if(!Review)return Forbid();if(r.Decision is not ("Approve" or "Reject")||string.IsNullOrWhiteSpace(r.Reason))return BadRequest(new{message="Decision and reason are required."});
  var status=r.Decision=="Approve"?"Approved":"Rejected";
  await using var command=await Cmd("update finance_workforce_claims set status=@status,reviewed_by=@actor,reviewed_by_user_id=@actorId,reviewed_at=now(),review_reason=@reason where id=@id and organization_id=@org and (@wide or branch_id=@branch) and status='Submitted' and submitted_by_user_id<>@actorId and not exists(select 1 from \"AppUsers\" u where u.\"Id\"=@actorId and u.\"CareWorkerId\"=care_worker_id) returning branch_id",t);
  Add(command,"id",id);Add(command,"status",status);Add(command,"reason",r.Reason.Trim());
  if(await command.ExecuteScalarAsync(t) is not Guid target)return Conflict(new{message="An independent reviewer must review a current submitted policy-backed claim."});
  Audit(status=="Approved"?"WORKFORCE_CLAIM_APPROVED":"WORKFORCE_CLAIM_REJECTED",id,target);await db.SaveChangesAsync(t);return NoContent();
 },t);
 async Task<IActionResult> Tx(Func<Task<IActionResult>> work,CancellationToken t)
 {
  try{return await db.Database.CreateExecutionStrategy().ExecuteAsync(async()=>{await using var tx=await db.Database.BeginTransactionAsync(t);await Exec("select pg_advisory_xact_lock(hashtextextended(@lock,0))",c=>Add(c,"lock",$"finance-claims:{tenant.OrganizationId}"),t);var result=await work();await tx.CommitAsync(t);return result;});}
  catch(Npgsql.PostgresException e) when(e.SqlState is "23514" or "23505"){return Conflict(new{message="Claim policy, evidence, scope, duplicate source or approval conflict. Reload and review the existing source."});}
 }
 async Task<List<Dictionary<string,object?>>>Rows(string s,Action<DbCommand>b,CancellationToken t){var a=new List<Dictionary<string,object?>>();await using var c=await Cmd(s,t);b(c);await using var r=await c.ExecuteReaderAsync(t);while(await r.ReadAsync(t)){var d=new Dictionary<string,object?>();for(var i=0;i<r.FieldCount;i++)d[r.GetName(i)]=r.IsDBNull(i)?null:r.GetValue(i);a.Add(d);}return a;}async Task<int>Exec(string s,Action<DbCommand>b,CancellationToken t){await using var c=await Cmd(s,t);b(c);return await c.ExecuteNonQueryAsync(t);}async Task<DbCommand>Cmd(string s,CancellationToken t){var n=db.Database.GetDbConnection();if(n.State!=ConnectionState.Open)await n.OpenAsync(t);var c=n.CreateCommand();c.Transaction=db.Database.CurrentTransaction?.GetDbTransaction();c.CommandText=s;Add(c,"org",tenant.OrganizationId);Add(c,"branch",Branch);Add(c,"wide",tenant.IsOrganizationWide);Add(c,"actor",user.UserName);Add(c,"actorId",user.UserId);return c;}static void Add(DbCommand c,string n,object?v){if(c.Parameters.Contains(n))return;var p=c.CreateParameter();p.ParameterName=n;p.Value=v??DBNull.Value;c.Parameters.Add(p);}void Audit(string a,Guid id,Guid? target=null)=>db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),a,user.UserName,"WorkforceClaim",id,DateTimeOffset.UtcNow,tenant.OrganizationId,target??Branch));
}
public sealed record WorkforceClaimRequest(Guid? CareWorkerId,Guid? VisitId,string ClaimType,DateOnly ClaimDate,decimal Quantity,decimal UnitRate,string? Category,string Notes,string? EvidenceReference,Guid? PolicyId=null,string? SourceReference=null,Guid? EvidenceId=null,string? ReceiptIssuer=null,string? ReceiptNumber=null);public sealed record ReviewClaimRequest(string Decision,string Reason);

public sealed record ClaimPolicyRequest(Guid BranchId,string Reference,string ClaimType,string Category,DateOnly ValidFrom,DateOnly ValidTo,string Currency,decimal UnitRate,decimal MaximumAmount,bool EvidenceRequired,bool VisitRequired,string RoundingMode);
