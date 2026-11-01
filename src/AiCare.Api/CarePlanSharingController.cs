using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiCare.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;
public sealed partial class EmergencyPlanSectionsController
{
    [HttpGet("shares"), Authorize(Roles="CareCoordinator,CareManager,Administrator")]
    public async Task<IActionResult> Shares(Guid planId,CancellationToken token)
    {
        var plan=await Plan(planId,"",token);if(plan is null)return NotFound();
        var shares=await Rows("""
            select s.id,s.recipient,s.relationship,s.purpose,s.authority_reference,s.sections_json,s.expires_at,s.created_at,
              r.reason revocation_reason,r.created_at revoked_at,b.family_member_id,b.consent_id,receipt.acknowledged_at,
              f."FullName" bound_recipient_name,
              case when r.id is not null then 'Revoked' when s.expires_at<=now() then 'Expired'
                when v.status<>'Active' then 'Superseded'
                when b.share_id is not null and not family_plan_share_authorized(s.id,b.family_member_id,@org) then 'AuthorityUnavailable'
                else 'Available' end status
            from care_plan_shares s join care_plan_versions v on v.care_plan_id=s.care_plan_id
            left join care_plan_share_revocations r on r.share_id=s.id
            left join care_plan_share_recipients b on b.share_id=s.id
            left join "FamilyMembers" f on f."Id"=b.family_member_id
            left join care_plan_share_receipts receipt on receipt.share_id=s.id
            where s.care_plan_id=@plan and s.organization_id=@org order by s.created_at desc,s.id
            """,c=>Add(c,"plan",planId),token);
        var accessHistory=await Rows("""
            select a."Id" id,a."EntityId" share_id,a."Action" action,a."Actor" actor,a."CreatedAt" created_at
            from "AuditEvents" a join care_plan_shares s on s.id=a."EntityId" and s.organization_id=a."OrganizationId"
            where s.care_plan_id=@plan and s.organization_id=@org
              and a."Action" in ('family.plan_share_downloaded','family.plan_share_acknowledged','care_plan.share_copy_downloaded','care_plan.share_recipient_bound','care_plan.share_revoked')
            order by a."CreatedAt" desc,a."Id" limit 500
            """,c=>Add(c,"plan",planId),token);
        return Ok(new{planStatus=plan.Status,revision=plan.Revision,shares,accessHistory,sections=EmergencyPlanCopy.Sections.Select(s=>new{key=s.Key,label=s.Value})});
    }

    [HttpPost("shares"), Authorize(Roles="CareManager,Administrator")]
    public Task<IActionResult> Share(Guid planId,CarePlanShareRequest r,CancellationToken token)
    {
        static bool Text(string? value,int limit)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=limit;
        if(r.RequestId==Guid.Empty||r.ExpectedPlanRevision<1||!EmergencyPlanCopy.ValidSections(r.Sections)||r.Sections.Length==0
            ||!Text(r.Recipient,300)||!Text(r.Relationship,300)||!Text(r.Purpose,2000)||!Text(r.AuthorityReference,2000))
            return Task.FromResult<IActionResult>(BadRequest(new{message="Provide recipient, relationship, purpose, reviewed authority reference, selected sections and current plan revision."}));
        return Transaction(async()=>{
            var plan=await Plan(planId," for update",token);if(plan is null)return NotFound();
            var snapshot=JsonSerializer.Serialize(r);
            var replay=await Rows("select request_snapshot=cast(@snapshot as jsonb) and created_by=@actor same from care_plan_shares where id=@id and care_plan_id=@plan and organization_id=@org",
                c=>{Add(c,"snapshot",snapshot);Add(c,"id",r.RequestId);Add(c,"plan",planId);},token);
            if(replay.Count>0)return replay[0].GetProperty("same").GetBoolean()?Ok(new{id=r.RequestId,replayed=true}):Conflict(new{message="Request ID has different content or author."});
            if(plan.Status!="Active"||plan.Revision!=r.ExpectedPlanRevision)return Conflict(new{message="Register sharing only from the current active plan revision."});
            if(r.ExpiresAt<=DateTimeOffset.UtcNow||r.ExpiresAt>DateTimeOffset.UtcNow.AddDays(90))return BadRequest(new{message="Choose an expiry within the next 90 days."});
            var person=await db.ServiceUsers.AsNoTracking().SingleOrDefaultAsync(p=>p.Id==plan.Content.ServiceUserId&&p.OrganizationId==tenant.OrganizationId&&p.BranchId==plan.Branch,token);
            if(person is null)return NotFound();
            var content=r.Sections.ToDictionary(s=>s,s=>s switch{"PersonalCare"=>plan.Content.PersonalCare,"MedicationSupport"=>plan.Content.MedicationSupport,"MobilityAndTransfers"=>plan.Content.MobilityAndTransfers,"Nutrition"=>plan.Content.Nutrition,_=>throw new InvalidOperationException()});
            var html=EmergencyPlanCopy.Render(person.FullName,person.DateOfBirth,plan.Version,plan.Revision,r.RequestId,DateTimeOffset.UtcNow,plan.Content.ReviewDueAt,content,"Selected care-plan copy");
            var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html))).ToLowerInvariant();
            await Exec("""
                insert into care_plan_shares(id,care_plan_id,service_user_id,organization_id,branch_id,plan_revision,recipient,relationship,purpose,authority_reference,
                  sections_json,expires_at,html,sha256,file_name,created_by,request_snapshot)
                values(@id,@plan,@person,@org,@branch,@revision,@recipient,@relationship,@purpose,@authority,cast(@sections as jsonb),@expiry,@html,@hash,@file,@actor,cast(@snapshot as jsonb))
                """,c=>{Add(c,"id",r.RequestId);Add(c,"plan",planId);Add(c,"person",person.Id);Add(c,"branch",plan.Branch);Add(c,"revision",plan.Revision);
                    Add(c,"recipient",r.Recipient.Trim());Add(c,"relationship",r.Relationship.Trim());Add(c,"purpose",r.Purpose.Trim());Add(c,"authority",r.AuthorityReference.Trim());
                    Add(c,"sections",JsonSerializer.Serialize(r.Sections));Add(c,"expiry",r.ExpiresAt.ToUniversalTime());Add(c,"html",html);Add(c,"hash",hash);
                    Add(c,"file",$"care-plan-share-{r.RequestId:N}.html");Add(c,"snapshot",snapshot);},token);
            await Audit("care_plan.share_registered",r.RequestId,plan.Branch,token);
            return Created($"/api/phase1/care-plans/{planId}/emergency-sections/shares/{r.RequestId}",new{id=r.RequestId});
        },token);
    }

    [HttpPost("shares/{shareId:guid}/copy"), Authorize(Roles="CareCoordinator,CareManager,Administrator")]
    public Task<IActionResult> SharedCopy(Guid planId,Guid shareId,CancellationToken token)=>Transaction(async()=>{
        var plan=await Plan(planId," for update",token);if(plan is null)return NotFound();
        var rows=await Rows("""
            select html,file_name,sha256 from care_plan_shares s where id=@share and care_plan_id=@plan and organization_id=@org
              and expires_at>now() and not exists(select 1 from care_plan_share_revocations r where r.share_id=s.id)
              and not exists(select 1 from care_plan_share_recipients b where b.share_id=s.id and not family_plan_share_authorized(s.id,b.family_member_id,@org))
            """,c=>{Add(c,"share",shareId);Add(c,"plan",planId);},token);
        if(plan.Status!="Active"||rows.Count!=1)return Conflict(new{message="The sharing record is unavailable, expired, revoked or its plan is no longer active."});
        await Audit("care_plan.share_copy_downloaded",shareId,plan.Branch,token);
        return Ok(new{id=shareId,html=rows[0].GetProperty("html").GetString(),fileName=rows[0].GetProperty("file_name").GetString(),sha256=rows[0].GetProperty("sha256").GetString()});
    },token);

    [HttpPost("shares/{shareId:guid}/revoke"), Authorize(Roles="CareManager,Administrator")]
    public Task<IActionResult> RevokeShare(Guid planId,Guid shareId,RevokePlanShareRequest r,CancellationToken token)
    {
        if(r.RequestId==Guid.Empty||string.IsNullOrWhiteSpace(r.Reason)||r.Reason.Length>2000)return Task.FromResult<IActionResult>(BadRequest(new{message="A request ID and revocation reason are required."}));
        return Transaction(async()=>{
            var plan=await Plan(planId," for update",token);if(plan is null)return NotFound();
            var shares=await Rows("select id from care_plan_shares where id=@share and care_plan_id=@plan and organization_id=@org",c=>{Add(c,"share",shareId);Add(c,"plan",planId);},token);
            if(shares.Count!=1)return NotFound();
            var snapshot=JsonSerializer.Serialize(new{shareId,request=r});
            var replay=await Rows("select request_snapshot=cast(@snapshot as jsonb) and created_by=@actor same from care_plan_share_revocations where id=@id",c=>{Add(c,"id",r.RequestId);Add(c,"snapshot",snapshot);},token);
            if(replay.Count>0)return replay[0].GetProperty("same").GetBoolean()?Ok(new{id=r.RequestId,replayed=true}):Conflict(new{message="Request ID has different content or author."});
            await Exec("insert into care_plan_share_revocations(id,share_id,reason,created_by,request_snapshot) values(@id,@share,@reason,@actor,cast(@snapshot as jsonb))",c=>{Add(c,"id",r.RequestId);Add(c,"share",shareId);Add(c,"reason",r.Reason.Trim());Add(c,"snapshot",snapshot);},token);
            await Audit("care_plan.share_revoked",shareId,plan.Branch,token);return Ok(new{id=r.RequestId});
        },token);
    }
}
public sealed record CarePlanShareRequest(Guid RequestId,long ExpectedPlanRevision,string Recipient,string Relationship,string Purpose,string AuthorityReference,string[] Sections,DateTimeOffset ExpiresAt);
public sealed record RevokePlanShareRequest(Guid RequestId,string Reason);
