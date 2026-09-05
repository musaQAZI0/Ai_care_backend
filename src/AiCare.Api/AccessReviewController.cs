using System.Data;
using System.Data.Common;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[ApiController]
[Authorize(Policy = "Phase1User", Roles = "CareManager,Administrator")]
[Route("api/security/access-reviews")]
public sealed class AccessReviewController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken t) => Ok(await Rows("select id,name,branch_id,dormant_days,status,due_at,created_by,created_at,completed_at from access_review_campaigns where organization_id=@organization and (@wide or branch_id=@branch) order by created_at desc", _ => { }, t));

    [HttpPost]
    public async Task<IActionResult> Create(CreateAccessReview request, CancellationToken t)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.DormantDays is < 30 or > 730 || request.DueAt <= DateTimeOffset.UtcNow || user.UserId is null) return BadRequest(new { message = "Name, a 30-730 day dormancy threshold, future due date, and authenticated reviewer are required." });
        var branch = request.BranchId ?? tenant.BranchId;
        if (!tenant.IsOrganizationWide && branch != tenant.BranchId) return NotFound();
        var id = Guid.NewGuid();
        await Exec("insert into access_review_campaigns(id,organization_id,branch_id,name,dormant_days,due_at,created_by_user_id,created_by) values(@id,@organization,@targetBranch,@name,@days,@due,@actorId,@actor)", c => { Add(c,"id",id); Add(c,"targetBranch",branch); Add(c,"name",request.Name.Trim()); Add(c,"days",request.DormantDays); Add(c,"due",request.DueAt); }, t);
        await Event(id,null,"Created",request.Name,branch,t);
        return Created($"/api/security/access-reviews/{id}",new { id });
    }

    [HttpPost("{id:guid}/run")]
    public async Task<IActionResult> Run(Guid id,CancellationToken t)
    {
        var campaign = await One("select id,branch_id,dormant_days from access_review_campaigns where id=@id and organization_id=@organization and status in ('Draft','InProgress') and (@wide or branch_id=@branch)",c=>Add(c,"id",id),t);
        if (campaign is null) return NotFound();
        await Exec("update access_review_campaigns set status='InProgress' where id=@id",c=>Add(c,"id",id),t);
        await Exec("""
            insert into access_review_items(id,campaign_id,organization_id,branch_id,user_id,user_name,role,account_active,last_seen_at,severity,finding_codes,recommended_action)
            select gen_random_uuid(),c.id,c.organization_id,u."BranchId",u."Id",u."UserName",u."Role",u."IsActive",max(s.last_seen_at),
              case when not u."IsActive" or (u."Role"='CareWorker' and u."CareWorkerId" is null) or (u."Role"='FamilyMember' and u."FamilyMemberId" is null) then 'Critical' when max(s.last_seen_at) is null or max(s.last_seen_at)<now()-(c.dormant_days||' days')::interval then 'High' when u."Role" in ('Administrator','CareManager') then 'Medium' else 'Low' end,
              array_remove(array[case when not u."IsActive" then 'InactiveWithAccess' end,case when max(s.last_seen_at) is null or max(s.last_seen_at)<now()-(c.dormant_days||' days')::interval then 'Dormant' end,case when u."Role"='CareWorker' and u."CareWorkerId" is null then 'OrphanedWorker' end,case when u."Role"='FamilyMember' and u."FamilyMemberId" is null then 'OrphanedFamily' end,case when u."Role" in ('Administrator','CareManager') then 'PrivilegedAccessReview' end],null),
              'Confirm least-privilege access'
            from access_review_campaigns c join "AppUsers" u on u."OrganizationId"=c.organization_id and (c.branch_id is null or u."BranchId"=c.branch_id) left join auth_sessions s on s.user_id=u."Id"
            where c.id=@id group by c.id,u."Id" on conflict(campaign_id,user_id) do nothing
            """,c=>Add(c,"id",id),t);
        await Event(id,null,"Run","Account inventory evaluated",tenant.BranchId,t);
        return Ok(await Detail(id,t));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id,CancellationToken t) => (await Detail(id,t)) is { } value ? Ok(value) : NotFound();

    [HttpPost("{campaignId:guid}/items/{itemId:guid}/decide")]
    public async Task<IActionResult> Decide(Guid campaignId,Guid itemId,DecideAccessReview request,CancellationToken t)
    {
        if (request.Decision is not ("Retain" or "Reduce" or "Suspend" or "Revoke" or "Escalate") || request.Justification?.Trim().Length < 10) return BadRequest(new { message = "A supported decision and meaningful justification are required." });
        var n=await Exec("update access_review_items i set status='Decided',decision=@decision,justification=@reason,decided_by_user_id=@actorId,decided_by=@actor,decided_at=now() from access_review_campaigns c where i.id=@item and i.campaign_id=@campaign and c.id=i.campaign_id and i.organization_id=@organization and i.status='Pending' and (@wide or c.branch_id=@branch)",c=>{Add(c,"item",itemId);Add(c,"campaign",campaignId);Add(c,"decision",request.Decision);Add(c,"reason",request.Justification!.Trim());},t);
        if(n==0)return NotFound();await Event(campaignId,itemId,"Decided",$"{request.Decision}: {request.Justification}",tenant.BranchId,t);return NoContent();
    }

    [HttpPost("{campaignId:guid}/items/{itemId:guid}/remediate")]
    [Authorize(Roles="Administrator")]
    public async Task<IActionResult> Remediate(Guid campaignId,Guid itemId,CancellationToken t)
    {
        var row=await One("select user_id from access_review_items where id=@item and campaign_id=@campaign and organization_id=@organization and status='Decided' and decision in ('Suspend','Revoke')",c=>{Add(c,"item",itemId);Add(c,"campaign",campaignId);},t); if(row is null)return NotFound();var target=(Guid)row["user_id"]!;if(target==user.UserId)return Conflict(new{message="You cannot remediate your own account."});
        await Exec("update \"AppUsers\" set \"IsActive\"=false where \"Id\"=@target and \"OrganizationId\"=@organization",c=>Add(c,"target",target),t);await Exec("update auth_sessions set revoked_at=now(),revoked_by=@actor,revocation_reason='Access review remediation' where user_id=@target and organization_id=@organization and revoked_at is null",c=>Add(c,"target",target),t);await Exec("update auth_refresh_tokens set revoked_at=now() where user_id=@target and revoked_at is null",c=>Add(c,"target",target),t);await Exec("update access_review_items set status='Remediated',remediated_at=now(),remediation_detail='Account suspended and sessions revoked' where id=@item",c=>Add(c,"item",itemId),t);await Event(campaignId,itemId,"Remediated","Account suspended and sessions revoked",tenant.BranchId,t);return NoContent();
    }

    [HttpPost("{id:guid}/close")]
    public async Task<IActionResult> Close(Guid id,CancellationToken t){var pending=await Scalar("select count(*) from access_review_items where campaign_id=@id and organization_id=@organization and status='Pending'",c=>Add(c,"id",id),t);if(pending>0)return Conflict(new{message="Every review item requires a decision before closure."});var n=await Exec("update access_review_campaigns set status='Completed',completed_at=now() where id=@id and organization_id=@organization and status='InProgress' and (@wide or branch_id=@branch)",c=>Add(c,"id",id),t);if(n==0)return NotFound();await Event(id,null,"Completed","Campaign completed",tenant.BranchId,t);return NoContent();}

    private async Task<object?> Detail(Guid id,CancellationToken t){var campaign=await One("select * from access_review_campaigns where id=@id and organization_id=@organization and (@wide or branch_id=@branch)",c=>Add(c,"id",id),t);return campaign is null?null:new{campaign,items=await Rows("select * from access_review_items where campaign_id=@id order by severity desc,user_name",c=>Add(c,"id",id),t),events=await Rows("select * from access_review_events where campaign_id=@id order by occurred_at",c=>Add(c,"id",id),t)};}
    private async Task Event(Guid campaign,Guid? item,string type,string detail,Guid? branch,CancellationToken t)=>await Exec("insert into access_review_events(id,campaign_id,item_id,organization_id,branch_id,event_type,actor_user_id,actor,detail) values(@event,@campaign,@item,@organization,@eventBranch,@type,@actorId,@actor,@detail)",c=>{Add(c,"event",Guid.NewGuid());Add(c,"campaign",campaign);Add(c,"item",item);Add(c,"eventBranch",branch);Add(c,"type",type);Add(c,"detail",detail);},t);
    private async Task<Dictionary<string,object?>?> One(string sql,Action<DbCommand> bind,CancellationToken t)=>(await Rows(sql,bind,t)).FirstOrDefault();
    private async Task<List<Dictionary<string,object?>>> Rows(string sql,Action<DbCommand> bind,CancellationToken t){var l=new List<Dictionary<string,object?>>();await using var c=await Command(sql,t);bind(c);await using var r=await c.ExecuteReaderAsync(t);while(await r.ReadAsync(t)){var x=new Dictionary<string,object?>();for(var i=0;i<r.FieldCount;i++)x[r.GetName(i)]=r.IsDBNull(i)?null:r.GetValue(i);l.Add(x);}return l;}
    private async Task<long> Scalar(string sql,Action<DbCommand> bind,CancellationToken t){await using var c=await Command(sql,t);bind(c);return Convert.ToInt64(await c.ExecuteScalarAsync(t));}
    private async Task<int> Exec(string sql,Action<DbCommand> bind,CancellationToken t){await using var c=await Command(sql,t);bind(c);return await c.ExecuteNonQueryAsync(t);}
    private async Task<DbCommand> Command(string sql,CancellationToken t){var cn=db.Database.GetDbConnection();if(cn.State!=ConnectionState.Open)await cn.OpenAsync(t);var c=cn.CreateCommand();c.CommandText=sql;Add(c,"organization",tenant.OrganizationId);Add(c,"branch",tenant.BranchId);Add(c,"wide",tenant.IsOrganizationWide);Add(c,"actorId",user.UserId);Add(c,"actor",user.UserName);return c;}
    private static void Add(DbCommand c,string n,object? v){if(c.Parameters.Contains(n))return;var p=c.CreateParameter();p.ParameterName=n;p.Value=v??DBNull.Value;c.Parameters.Add(p);}
}
public sealed record CreateAccessReview(string Name,Guid? BranchId,int DormantDays,DateTimeOffset DueAt);
public sealed record DecideAccessReview(string Decision,string? Justification);
