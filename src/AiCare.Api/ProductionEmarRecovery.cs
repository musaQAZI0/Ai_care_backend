using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

public sealed partial class ProductionEmarController
{
    private async Task<bool> RecoverySubmissionClosed(Guid marId, EmarRecordRequest request, CancellationToken token)
    {
        var matches=await Query("""
            select q.id from emar_ledger q join emar_ledger d on d.corrects_ledger_id=q.id and d.event_type='RecoveryDecision'
            where q.organization_id=@organization and q.branch_id=@branch and q.mar_record_id=@mar
              and q.event_type='RecoveryRequest' and q.created_by_user_id=@actorId and q.submission_fingerprint=@fingerprint
            """,c=>{ Add(c,"mar",marId); Add(c,"fingerprint",SubmissionFingerprint(request)); },r=>r.GetGuid(0),token);
        return matches.Count>0;
    }

    [HttpPost("mar/{marId:guid}/recovery-review")]
    public Task<IActionResult> RequestRecoveryReview(Guid marId, EmarRecoveryReviewRequest request, CancellationToken token)
        => db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () =>
        {
            if (user.UserId is null) return Forbid();
            if (string.IsNullOrWhiteSpace(request.RetryKey) || request.RetryKey.Length > 200)
                return BadRequest(new { message = "The original retry key is required (maximum 200 characters)." });
            await using var tx = await db.Database.BeginTransactionAsync(token);
            var mar = await Mar(marId, token);
            if (mar is null) return NotFound();
            if (!CanRecord(mar.Value.Worker)) return Forbid();
            var fingerprint = SubmissionFingerprint(request.Submission);
            await Exec("select pg_advisory_xact_lock(hashtextextended(@reviewLock,0))",
                c => Add(c, "reviewLock", $"emar-recovery:{tenant.OrganizationId}:{user.UserId}:{marId}:{fingerprint}"), token);
            var existing = await Query("""
                select id from emar_ledger where organization_id=@organization and branch_id=@branch
                  and mar_record_id=@mar and created_by_user_id=@actorId
                  and submission_fingerprint=@fingerprint and event_type='RecoveryRequest'
                """, c => { Add(c, "mar", marId); Add(c, "fingerprint", fingerprint); }, r => r.GetGuid(0), token);
            var id = existing.FirstOrDefault();
            if (id == Guid.Empty)
            {
                id = Guid.NewGuid();
                await Exec("""
                    insert into emar_ledger(id,mar_record_id,medication_id,service_user_id,organization_id,branch_id,
                      event_type,outcome,reason_code,reason_detail,submission_fingerprint,occurred_at,created_by_user_id,created_by)
                    values(@id,@mar,@medication,@person,@organization,@branch,'RecoveryRequest','Pending',@retryKey,@detail,@fingerprint,now(),@actorId,@actor)
                    """, c => { Add(c,"id",id); Add(c,"mar",marId); Add(c,"medication",mar.Value.Medication);
                        Add(c,"person",mar.Value.Person); Add(c,"retryKey",request.RetryKey.Trim());
                        Add(c,"detail",JsonSerializer.Serialize(request.Submission)); Add(c,"fingerprint",fingerprint); }, token);
                Audit("emar.recovery_review_requested", marId);
                await db.SaveChangesAsync(token);
            }
            var decisions = await Query("""
                select id,outcome,reason_detail from emar_ledger where organization_id=@organization and branch_id=@branch
                  and corrects_ledger_id=@request and event_type='RecoveryDecision'
                """, c => Add(c,"request",id), r => new { id=r.GetGuid(0), resolution=r.GetString(1), evidence=r.GetString(2) }, token);
            await tx.CommitAsync(token);
            return Ok(new { id, status=decisions.Count==0 ? "Pending" : "Resolved", decision=decisions.FirstOrDefault() });
        });

    [HttpGet("recovery-reviews")]
    [Authorize(Roles="Administrator,CareManager")]
    public async Task<IActionResult> RecoveryReviews(CancellationToken token)
        => Ok(await Query("""
            select q.id,q.mar_record_id,q.service_user_id,m."Name",q.created_by,q.created_at,q.reason_detail,q.reason_code
            from emar_ledger q join "Medications" m on m."Id"=q.medication_id and m."OrganizationId"=q.organization_id and m."BranchId"=q.branch_id
            where q.organization_id=@organization and q.branch_id=@branch and q.event_type='RecoveryRequest'
              and not exists(select 1 from emar_ledger d where d.corrects_ledger_id=q.id and d.event_type='RecoveryDecision')
            order by q.created_at limit 200
            """, _ => { }, r => new { id=r.GetGuid(0), marId=r.GetGuid(1), personId=r.GetGuid(2), medicationName=r.GetString(3),
                requestedBy=r.GetString(4), createdAt=r.GetFieldValue<DateTimeOffset>(5), submission=JsonSerializer.Deserialize<EmarRecordRequest>(r.GetString(6)), retryKey=r.GetString(7) }, token));

    [HttpPost("recovery-reviews/{id:guid}/resolve")]
    [Authorize(Roles="Administrator,CareManager")]
    public Task<IActionResult> ResolveRecoveryReview(Guid id, EmarRecoveryDecision request, CancellationToken token)
        => db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () =>
        {
            if (request.Resolution is not ("AlreadyRecorded" or "NotApplied") || string.IsNullOrWhiteSpace(request.Evidence) || request.Evidence.Length > 4000)
                return BadRequest(new { message="A supported resolution and evidence (maximum 4000 characters) are required." });
            await using var tx = await db.Database.BeginTransactionAsync(token);
            await Exec("select pg_advisory_xact_lock(hashtextextended(@reviewLock,0))", c=>Add(c,"reviewLock",$"emar-recovery-decision:{tenant.OrganizationId}:{id}"),token);
            var requests = await Query("""
                select mar_record_id,medication_id,service_user_id,submission_fingerprint from emar_ledger
                where id=@id and organization_id=@organization and branch_id=@branch and event_type='RecoveryRequest'
                """, c=>Add(c,"id",id),r=>new { Mar=r.GetGuid(0), Medication=r.GetGuid(1), Person=r.GetGuid(2), Fingerprint=r.GetString(3) },token);
            if(requests.Count!=1) return NotFound();
            var pending=requests[0];
            var previous=await Query("select id,outcome,reason_detail from emar_ledger where corrects_ledger_id=@request and event_type='RecoveryDecision' and organization_id=@organization and branch_id=@branch",
                c=>Add(c,"request",id),r=>new { Id=r.GetGuid(0), Resolution=r.GetString(1), Evidence=r.GetString(2) },token);
            var detail=JsonSerializer.Serialize(new { evidence=request.Evidence.Trim(), terminalLedgerId=request.TerminalLedgerId });
            if(previous.Count>0)
            {
                if(previous[0].Resolution!=request.Resolution || previous[0].Evidence!=detail) return Conflict(new { message="A different reconciliation decision already exists." });
                await tx.CommitAsync(token); return Ok(new { id=previous[0].Id, replayed=true });
            }
            await Exec("select 1 from \"MedicationAdministrationRecords\" where \"Id\"=@mar and \"OrganizationId\"=@organization and \"BranchId\"=@branch for update",c=>Add(c,"mar",pending.Mar),token);
            var terminal=await Query("""
                select id from emar_ledger where organization_id=@organization and branch_id=@branch and mar_record_id=@mar
                  and event_type in ('Administration','Omission','Refusal','Held','NotRequired')
                """,c=>Add(c,"mar",pending.Mar),r=>r.GetGuid(0),token);
            if(request.Resolution=="AlreadyRecorded" && (request.TerminalLedgerId is null || !terminal.Contains(request.TerminalLedgerId.Value)))
                return Conflict(new { message="Select the actual terminal ledger entry for this MAR after reviewing the recorded and paper evidence." });
            if(request.Resolution=="NotApplied" && (terminal.Count>0 || request.TerminalLedgerId is not null))
                return Conflict(new { message="A terminal outcome already exists. Review it using Already recorded; do not discard it as not applied." });
            var decisionId=Guid.NewGuid();
            await Exec("""
                insert into emar_ledger(id,mar_record_id,medication_id,service_user_id,organization_id,branch_id,event_type,outcome,
                  reason_detail,corrects_ledger_id,submission_fingerprint,occurred_at,created_by_user_id,created_by)
                values(@id,@mar,@medication,@person,@organization,@branch,'RecoveryDecision',@resolution,@detail,@request,@fingerprint,now(),@actorId,@actor)
                """,c=>{ Add(c,"id",decisionId); Add(c,"mar",pending.Mar); Add(c,"medication",pending.Medication); Add(c,"person",pending.Person);
                    Add(c,"resolution",request.Resolution); Add(c,"detail",detail); Add(c,"request",id); Add(c,"fingerprint",pending.Fingerprint); },token);
            Audit("emar.recovery_review_resolved",pending.Mar); await db.SaveChangesAsync(token); await tx.CommitAsync(token);
            return Ok(new { id=decisionId });
        });
}

public sealed record EmarRecoveryReviewRequest(string RetryKey, EmarRecordRequest Submission);
public sealed record EmarRecoveryDecision(string Resolution,string Evidence,Guid? TerminalLedgerId=null);
