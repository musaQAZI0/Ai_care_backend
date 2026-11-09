using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

public sealed partial class ProductionEmarController
{
    private async Task<bool> HasIndependentAttestation(Guid marId,EmarRecordRequest request,CancellationToken token)
    {
        var rows=await Query("""
            select a.id from emar_ledger q join emar_ledger a on a.corrects_ledger_id=q.id
              and a.event_type='WitnessAttestation' and a.outcome='Approved'
            where q.event_type='WitnessRequest' and q.organization_id=@organization and q.branch_id=@branch
              and q.mar_record_id=@mar and q.created_by_user_id=@actorId and q.witness_user_id=@witness
              and a.organization_id=q.organization_id and a.branch_id=q.branch_id and a.created_by_user_id=q.witness_user_id
              and q.submission_fingerprint=@fingerprint
            """,c=>{ Add(c,"mar",marId);Add(c,"witness",request.WitnessUserId);Add(c,"fingerprint",SubmissionFingerprint(request)); },r=>r.GetGuid(0),token);
        return rows.Count>0;
    }

    [HttpPost("mar/{marId:guid}/witness-request")]
    public Task<IActionResult> RequestWitnessAttestation(Guid marId,EmarRecordRequest request,CancellationToken token)
        =>db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async()=>
        {
            await using var tx=await db.Database.BeginTransactionAsync(token);
            var mar=await Mar(marId,token);if(mar is null)return NotFound();if(!CanRecord(mar.Value.Worker))return Forbid();
            if(request.Outcome!="Administered" || request.WitnessUserId is null || request.DoseQuantity<=0) return BadRequest(new { message="An administered submission with its quantity and selected witness is required." });
            if(mar.Value.Outcome!="Scheduled")return Conflict(new { message="This MAR already has a terminal outcome." });
            var witness=await Witness(request.WitnessUserId,true,mar.Value.Route,token);if(witness.Error is not null)return BadRequest(new {message=witness.Error});
            var fingerprint=SubmissionFingerprint(request);
            await Exec("select pg_advisory_xact_lock(hashtextextended(@witnessLock,0))",c=>Add(c,"witnessLock",$"emar-witness:{tenant.OrganizationId}:{marId}:{user.UserId}:{fingerprint}"),token);
            var existing=await Query("select id from emar_ledger where organization_id=@organization and branch_id=@branch and mar_record_id=@mar and created_by_user_id=@actorId and submission_fingerprint=@fingerprint and event_type='WitnessRequest'",
                c=>{Add(c,"mar",marId);Add(c,"fingerprint",fingerprint);},r=>r.GetGuid(0),token);
            if(existing.Count>0){await tx.CommitAsync(token);return Ok(new { id=existing[0],replayed=true });}
            var id=Guid.NewGuid();
            await Exec("""
                insert into emar_ledger(id,mar_record_id,medication_id,service_user_id,organization_id,branch_id,event_type,outcome,
                  reason_detail,witness_user_id,witness_name,submission_fingerprint,occurred_at,created_by_user_id,created_by)
                values(@id,@mar,@medication,@person,@organization,@branch,'WitnessRequest','Pending',@detail,@witness,@witnessName,@fingerprint,now(),@actorId,@actor)
                """,c=>{Add(c,"id",id);Add(c,"mar",marId);Add(c,"medication",mar.Value.Medication);Add(c,"person",mar.Value.Person);
                    Add(c,"detail",JsonSerializer.Serialize(request));Add(c,"witness",request.WitnessUserId);Add(c,"witnessName",witness.Name);Add(c,"fingerprint",fingerprint);},token);
            Audit("emar.witness_confirmation_requested",marId);await db.SaveChangesAsync(token);await tx.CommitAsync(token);return Ok(new {id});
        });

    [HttpGet("witness-requests")]
    public async Task<IActionResult> WitnessRequests(CancellationToken token)=>Ok(await Query("""
        select q.id,q.mar_record_id,q.service_user_id,m."Name",q.created_by,q.reason_detail,p."FullName",p."DateOfBirth"
        from emar_ledger q join "Medications" m on m."Id"=q.medication_id and m."OrganizationId"=q.organization_id and m."BranchId"=q.branch_id
        join "ServiceUsers" p on p."Id"=q.service_user_id and p."OrganizationId"=q.organization_id and p."BranchId"=q.branch_id
        join "MedicationAdministrationRecords" r on r."Id"=q.mar_record_id and r."OrganizationId"=q.organization_id and r."BranchId"=q.branch_id
        where q.organization_id=@organization and q.branch_id=@branch and q.witness_user_id=@actorId and q.event_type='WitnessRequest'
          and r."Outcome"='Scheduled' and not exists(select 1 from emar_ledger a where a.corrects_ledger_id=q.id and a.event_type='WitnessAttestation')
        order by q.created_at limit 200
        """,_=>{},r=>new {id=r.GetGuid(0),marId=r.GetGuid(1),personId=r.GetGuid(2),medicationName=r.GetString(3),requestedBy=r.GetString(4),submission=JsonSerializer.Deserialize<EmarRecordRequest>(r.GetString(5)),personName=r.GetString(6),dateOfBirth=r.GetFieldValue<DateOnly>(7)},token));

    [HttpPost("witness-requests/{id:guid}/attest")]
    public Task<IActionResult> AttestWitness(Guid id,EmarWitnessDecision request,CancellationToken token)
        =>db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async()=>
        {
            if(request.Decision is not("Approved" or "Rejected") || string.IsNullOrWhiteSpace(request.Evidence)||request.Evidence.Length>4000 || request.Decision=="Approved"&&!request.IndependentlyChecked)
                return BadRequest(new {message="Choose a decision, enter witness evidence and confirm an independent check before approval."});
            await using var tx=await db.Database.BeginTransactionAsync(token);
            await Exec("select pg_advisory_xact_lock(hashtextextended(@witnessLock,0))",c=>Add(c,"witnessLock",$"emar-witness-decision:{tenant.OrganizationId}:{id}"),token);
            var rows=await Query("""
                select mar_record_id,medication_id,service_user_id,submission_fingerprint,created_by_user_id from emar_ledger
                where id=@id and organization_id=@organization and branch_id=@branch and witness_user_id=@actorId and event_type='WitnessRequest'
                """,c=>Add(c,"id",id),r=>new {Mar=r.GetGuid(0),Medication=r.GetGuid(1),Person=r.GetGuid(2),Fingerprint=r.GetString(3),Operator=r.GetGuid(4)},token);
            if(rows.Count!=1)return NotFound();var q=rows[0];if(q.Operator==user.UserId)return Forbid();
            var mar=await Mar(q.Mar,token);if(mar is null)return NotFound();
            var witness=await WitnessForCurrentUser(mar.Value.Route,token);if(witness is not null)return Conflict(new {message=witness});
            var existing=await Query("select id,outcome,reason_detail from emar_ledger where corrects_ledger_id=@request and event_type='WitnessAttestation' and organization_id=@organization and branch_id=@branch",
                c=>Add(c,"request",id),r=>new {Id=r.GetGuid(0),Decision=r.GetString(1),Evidence=r.GetString(2)},token);
            if(existing.Count>0){if(existing[0].Decision!=request.Decision||existing[0].Evidence!=request.Evidence.Trim())return Conflict(new {message="A different witness decision already exists."});await tx.CommitAsync(token);return Ok(new {id=existing[0].Id,replayed=true});}
            if(mar.Value.Outcome!="Scheduled")return Conflict(new {message="This MAR already has a terminal outcome; use the correction process."});
            var result=Guid.NewGuid();
            await Exec("""
                insert into emar_ledger(id,mar_record_id,medication_id,service_user_id,organization_id,branch_id,event_type,outcome,
                  reason_detail,corrects_ledger_id,witness_user_id,witness_name,submission_fingerprint,occurred_at,created_by_user_id,created_by)
                values(@id,@mar,@medication,@person,@organization,@branch,'WitnessAttestation',@decision,@evidence,@request,@actorId,@actor,@fingerprint,now(),@actorId,@actor)
                """,c=>{Add(c,"id",result);Add(c,"mar",q.Mar);Add(c,"medication",q.Medication);Add(c,"person",q.Person);Add(c,"decision",request.Decision);
                    Add(c,"evidence",request.Evidence.Trim());Add(c,"request",id);Add(c,"fingerprint",q.Fingerprint);},token);
            Audit("emar.independent_witness_attested",q.Mar);await db.SaveChangesAsync(token);await tx.CommitAsync(token);return Ok(new {id=result});
        });

    private async Task<string?> WitnessForCurrentUser(string route,CancellationToken token)
    {
        if(!user.HasAnyRole(AiCare.Domain.UserRole.Administrator,AiCare.Domain.UserRole.CareManager,AiCare.Domain.UserRole.CareCoordinator,AiCare.Domain.UserRole.CareWorker))return "An authorised medication staff role is required to witness.";
        if(user.CareWorkerId is null)return "A linked care worker and current route competency are required to witness.";
        return await MedicationAuthorization(user.CareWorkerId.Value,route,token);
    }
}
public sealed record EmarWitnessDecision(string Decision,string Evidence,bool IndependentlyChecked);
