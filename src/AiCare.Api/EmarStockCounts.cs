using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

public sealed partial class EmarOperationsController
{
    [HttpPost("medications/{medicationId:guid}/stock-count")]
    [Authorize(Roles="Administrator,CareManager,CareCoordinator")]
    public Task<IActionResult> CountStock(Guid medicationId,EmarStockCount request,CancellationToken token)
        =>db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async()=>
        {
            var key=Request.Headers["Idempotency-Key"].ToString().Trim();
            if(string.IsNullOrWhiteSpace(key)||key.Length>200||string.IsNullOrWhiteSpace(request.Reason)||request.Reason.Length>4000||request.Reference.Length>500 ||
                request.CountedBalance<0||request.CountedBalance>9999999999.99m||decimal.Round(request.CountedBalance,2)!=request.CountedBalance)
                return BadRequest(new { message="An original retry key, non-negative count (at most two decimals), bounded reference and reason are required." });
            var fingerprint=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
            var endpoint=$"POST /api/phase1/emar-safety/medications/{medicationId}/stock-count";
            await using var tx=await db.Database.BeginTransactionAsync(token);
            await Exec("select pg_advisory_xact_lock(hashtextextended(@countLock,0))",c=>Add(c,"countLock",$"emar-stock-count:{tenant.OrganizationId}:{user.UserId}:{key}"),token);
            var existing=await Query("""
                select s.id,s.balance_after,s.counted_balance,s.submission_fingerprint from api_idempotency_keys k
                join medication_stock_transactions s on s.id=k.resource_id and s.organization_id=k.organization_id and s.branch_id=k.branch_id
                where k.organization_id=@organization and k.branch_id=@branch and k.actor_user_id=@actorId and k.endpoint=@endpoint and k.idempotency_key=@key
                  and s.medication_id=@medication
                """,c=>{Add(c,"endpoint",endpoint);Add(c,"key",key);Add(c,"medication",medicationId);},r=>new {Id=r.GetGuid(0),Balance=r.GetDecimal(1),Count=r.GetDecimal(2),Fingerprint=r.GetString(3)},token);
            if(existing.Count>0)
            {
                if(existing[0].Fingerprint!=fingerprint)return Conflict(new {message="This retry key belongs to a different stock count."});
                await tx.CommitAsync(token);return Ok(new {id=existing[0].Id,balance=existing[0].Balance,countedBalance=existing[0].Count,discrepancy=existing[0].Count-existing[0].Balance,replayed=true});
            }
            var profile=await Profile(medicationId,token,true);if(profile is null)return NotFound();
            if(profile.Value.Stock is null||profile.Value.Stock!=request.ExpectedBalance)return Conflict(new {message="Recorded stock changed or is unreconciled. Reload and repeat the physical check before submitting."});
            var balance=profile.Value.Stock.Value;var difference=request.CountedBalance-balance;var id=Guid.NewGuid();
            await Exec("""
                insert into medication_stock_transactions(id,medication_id,organization_id,branch_id,transaction_type,quantity,balance_before,balance_after,
                  reference,reason,created_by_user_id,created_by,counted_balance,submission_fingerprint)
                values(@id,@medication,@organization,@branch,'Count',0,@balance,@balance,@reference,@reason,@actorId,@actor,@count,@fingerprint);
                insert into api_idempotency_keys(id,organization_id,branch_id,actor_user_id,endpoint,idempotency_key,resource_type,resource_id)
                values(@keyId,@organization,@branch,@actorId,@endpoint,@key,'MedicationStockCount',@id)
                """,c=>{Add(c,"id",id);Add(c,"medication",medicationId);Add(c,"balance",balance);Add(c,"reference",request.Reference.Trim());Add(c,"reason",request.Reason.Trim());
                    Add(c,"count",request.CountedBalance);Add(c,"fingerprint",fingerprint);Add(c,"keyId",Guid.NewGuid());Add(c,"endpoint",endpoint);Add(c,"key",key);},token);
            if(difference!=0)await Exec("""
                insert into medication_monitoring_alerts(id,medication_id,organization_id,branch_id,alert_type,due_at,severity,status,message,owner)
                values(@alert,@medication,@organization,@branch,'StockDiscrepancy',now(),'High','Open',@message,'Medication lead')
                """,c=>{Add(c,"alert",Guid.NewGuid());Add(c,"medication",medicationId);Add(c,"message",$"Stock count {id}: ledger {balance}, counted {request.CountedBalance}, difference {difference}. Investigate and document the outcome; no stock adjustment was made.");},token);
            Audit("emar.stock_count_recorded","Medication",medicationId);await db.SaveChangesAsync(token);await tx.CommitAsync(token);
            return Ok(new {id,balance,countedBalance=request.CountedBalance,discrepancy=difference});
        });
}

public sealed record EmarStockCount(decimal ExpectedBalance,decimal CountedBalance,string Reference,string Reason);
