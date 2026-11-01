using System.Data;
using System.Data.Common;
using System.Text.Json;
using AiCare.Application;
using AiCare.Application.FamilyPortal;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
namespace AiCare.Api;
[ApiController,Authorize(Roles="FamilyMember")]
[Route("api/family/service-users/{personId:guid}/plan-shares")]
[ResponseCache(NoStore=true,Location=ResponseCacheLocation.None)]
public sealed class FamilyPlanSharesController(CareDbContext db,ITenantContext tenant,ICurrentUserContext user,IFamilyPortalService access):ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid personId,CancellationToken token)
    {
        if(!await Allowed(personId,token))return Forbid();
        var rows=await Rows("""
            select s.id,s.purpose,s.expires_at,s.sections_json,p.version_number,r.acknowledged_at
            from care_plan_shares s join care_plan_versions p on p.care_plan_id=s.care_plan_id
            left join care_plan_share_receipts r on r.share_id=s.id and r.family_member_id=@family
            where s.service_user_id=@person and s.organization_id=@org and family_plan_share_authorized(s.id,@family,@org)
            order by s.created_at desc,s.id
            """,personId,_=>{},token);
        return Ok(rows);
    }
    [HttpPost("{shareId:guid}/copy")]
    public Task<IActionResult> Copy(Guid personId,Guid shareId,CancellationToken token)=>Action(personId,shareId,false,token);
    [HttpPost("{shareId:guid}/acknowledge")]
    public Task<IActionResult> Acknowledge(Guid personId,Guid shareId,CancellationToken token)=>Action(personId,shareId,true,token);
    private async Task<IActionResult> Action(Guid person,Guid share,bool acknowledge,CancellationToken token)
    {
        if(!await Allowed(person,token))return Forbid();
        return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async()=>{
            await using var tx=await db.Database.BeginTransactionAsync(token);
            var rows=await Rows("""
                select s.html,s.file_name,s.sha256,s.branch_id from care_plan_shares s
                join care_plan_versions p on p.care_plan_id=s.care_plan_id
                join care_plan_share_recipients b on b.share_id=s.id
                join consent_records consent on consent.id=b.consent_id
                join family_access_grants g on g.family_member_id=b.family_member_id and g.service_user_id=s.service_user_id and g.organization_id=s.organization_id
                where s.id=@share and s.service_user_id=@person and s.organization_id=@org and family_plan_share_authorized(s.id,@family,@org)
                for share of s,p,consent,g
                """,person,c=>Add(c,"share",share),token);
            if(rows.Count!=1)return NotFound();
            var branch=rows[0].GetProperty("branch_id").GetGuid();
            if(acknowledge){
                // Acknowledgement is an explicit receipt confirmation, never a clinical signature.
                await using var c=await Command("""
                    insert into care_plan_share_receipts(share_id,family_member_id,acknowledged_by)
                    values(@share,@family,@actor) on conflict(share_id) do nothing
                    """,person,token);Add(c,"share",share);
                if(await c.ExecuteNonQueryAsync(token)>0)db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),"family.plan_share_acknowledged",user.UserName,"CarePlanShare",share,DateTimeOffset.UtcNow,tenant.OrganizationId,branch));
            }else db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),"family.plan_share_downloaded",user.UserName,"CarePlanShare",share,DateTimeOffset.UtcNow,tenant.OrganizationId,branch));
            await db.SaveChangesAsync(token);await tx.CommitAsync(token);
            return acknowledge?Ok(new{acknowledged=true}):Ok(new{id=share,html=rows[0].GetProperty("html").GetString(),fileName=rows[0].GetProperty("file_name").GetString(),sha256=rows[0].GetProperty("sha256").GetString()});
        });
    }
    private async Task<bool> Allowed(Guid person,CancellationToken token)
    {
        if(user.FamilyMemberId is not Guid family||user.UserId is null)return false;
        try{await access.EnsurePermissionAsync(tenant.OrganizationId,family,person,FamilyPermissions.ViewCarePlan,token);return true;}
        catch(UnauthorizedAccessException){return false;}
    }
    private async Task<List<JsonElement>> Rows(string sql,Guid person,Action<DbCommand> bind,CancellationToken token)
    {await using var c=await Command($"select row_to_json(q)::text from ({sql})q",person,token);bind(c);await using var r=await c.ExecuteReaderAsync(token);var rows=new List<JsonElement>();while(await r.ReadAsync(token))rows.Add(JsonSerializer.Deserialize<JsonElement>(r.GetString(0)));return rows;}
    private async Task<DbCommand> Command(string sql,Guid person,CancellationToken token)
    {var connection=db.Database.GetDbConnection();if(connection.State!=ConnectionState.Open)await connection.OpenAsync(token);var c=connection.CreateCommand();c.CommandText=sql;c.Transaction=db.Database.CurrentTransaction?.GetDbTransaction();Add(c,"person",person);Add(c,"org",tenant.OrganizationId);Add(c,"family",user.FamilyMemberId);Add(c,"actor",user.UserId);return c;}
    private static void Add(DbCommand c,string name,object? value){var p=c.CreateParameter();p.ParameterName=name;p.Value=value??DBNull.Value;c.Parameters.Add(p);}
}
