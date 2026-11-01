using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace AiCare.Api;
public sealed partial class EmergencyPlanSectionsController
{
    [HttpGet("shares/recipient-options"),Authorize(Roles="CareManager,Administrator")]
    public async Task<IActionResult> ShareRecipientOptions(Guid planId,CancellationToken token)
    {
        var plan=await Plan(planId,"",token);if(plan is null)return NotFound();
        var people=await Rows("""
            select f."Id" id,f."FullName" name,f."Relationship" relationship,g.revision grant_revision
            from "FamilyMembers" f join family_access_grants g on g.family_member_id=f."Id" and g.organization_id=@org and g.service_user_id=@person
            where f."ServiceUserId"=@person and f."OrganizationId"=@org and f."BranchId"=@branch and f."Status"='Active'
              and g.verification_status='Verified' and g.access_status='Active' and (g.valid_from is null or g.valid_from<=now())
              and (g.valid_until is null or g.valid_until>now()) and exists(select 1 from family_access_permissions where access_grant_id=g.id and permission='ViewCarePlan')
            """,c=>{Add(c,"person",plan.Content.ServiceUserId);Add(c,"branch",plan.Branch);},token);
        var consents=await Rows("""
            select id,consent_type,scope,information_categories,sharing_parties from consent_records where service_user_id=@person and organization_id=@org and branch_id=@branch
              and status='Active' and effective_from<=now() and (expires_at is null or expires_at>now()) and (review_due_at is null or review_due_at>now())
            """,c=>{Add(c,"person",plan.Content.ServiceUserId);Add(c,"branch",plan.Branch);},token);
        return Ok(new{people,consents});
    }

    [HttpPost("shares/{shareId:guid}/recipient"),Authorize(Roles="CareManager,Administrator")]
    public Task<IActionResult> BindShareRecipient(Guid planId,Guid shareId,BindShareRecipientRequest r,CancellationToken token)
    {
        if(r.RequestId==Guid.Empty||r.FamilyMemberId==Guid.Empty||r.ConsentId==Guid.Empty||r.GrantRevision<1||string.IsNullOrWhiteSpace(r.ScopeConfirmation)||r.ScopeConfirmation.Length>2000)
            return Task.FromResult<IActionResult>(BadRequest(new{message="Select a verified recipient and current consent; confirm that its scope covers the recipient, purpose and selected sections."}));
        return Transaction(async()=>{
            var plan=await Plan(planId," for update",token);if(plan is null)return NotFound();
            var shares=await Rows("select id from care_plan_shares where id=@share and care_plan_id=@plan and organization_id=@org",c=>{Add(c,"share",shareId);Add(c,"plan",planId);},token);
            if(shares.Count!=1)return NotFound();
            var snapshot=JsonSerializer.Serialize(new{shareId,request=r});
            var replay=await Rows("select request_snapshot=cast(@snapshot as jsonb) and created_by=@actor same from care_plan_share_recipients where request_id=@id",c=>{Add(c,"snapshot",snapshot);Add(c,"id",r.RequestId);},token);
            if(replay.Count>0)return replay[0].GetProperty("same").GetBoolean()?Ok(new{replayed=true}):Conflict(new{message="Request ID has different content or author."});
            await Exec("""
                insert into care_plan_share_recipients(share_id,request_id,family_member_id,consent_id,grant_revision,scope_confirmation,created_by,request_snapshot)
                values(@share,@id,@family,@consent,@revision,@scope,@actor,cast(@snapshot as jsonb))
                """,c=>{Add(c,"share",shareId);Add(c,"id",r.RequestId);Add(c,"family",r.FamilyMemberId);Add(c,"consent",r.ConsentId);Add(c,"revision",r.GrantRevision);Add(c,"scope",r.ScopeConfirmation.Trim());Add(c,"snapshot",snapshot);},token);
            var valid=await Rows("select family_plan_share_authorized(@share,@family,@org) valid",c=>{Add(c,"share",shareId);Add(c,"family",r.FamilyMemberId);},token);
            if(!valid[0].GetProperty("valid").GetBoolean())return Conflict(new{message="Sharing, consent or recipient authority is no longer current. Refresh before enabling portal access."});
            await Audit("care_plan.share_recipient_bound",shareId,plan.Branch,token);return Ok(new{shareId});
        },token);
    }
}
public sealed record BindShareRecipientRequest(Guid RequestId,Guid FamilyMemberId,Guid ConsentId,long GrantRevision,string ScopeConfirmation);
