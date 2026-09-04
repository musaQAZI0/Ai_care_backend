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
[Authorize(Policy = "Phase1User")]
[Route("api/security/privileged-access")]
public sealed class PrivilegedAccessController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user) : ControllerBase
{
    [HttpGet]
    [Authorize(Roles = "CareManager,Administrator")]
    public async Task<IActionResult> List(CancellationToken token) => Ok(new
    {
        delegations = await Rows("select id,branch_id,person_id,granted_to_user_id,delegated_role,action_scope,route_prefix,http_methods,reason,starts_at,expires_at,status,granted_by,revoked_at,revocation_reason,reviewed_at,reviewed_by,review_decision,review_notes from access_delegations where organization_id=@organization and (@organizationWide or branch_id is null or branch_id=@branch) order by created_at desc", token),
        emergencies = await Rows("select id,branch_id,person_id,user_id,action_scope,route_prefix,http_methods,justification,status,activated_at,expires_at,closed_at,reviewed_at,reviewed_by,review_decision,review_notes from emergency_access_grants where organization_id=@organization and (@organizationWide or branch_id=@branch) order by activated_at desc", token),
        alerts = await Rows("select access_type,access_id,event_type,actor,detail,occurred_at from privileged_access_events where organization_id=@organization and event_type in ('EmergencyActivated','Expired','Revoked') and (@organizationWide or branch_id is null or branch_id=@branch) order by occurred_at desc limit 100", token)
    });

    [HttpPost("delegations")]
    [Authorize(Roles = "CareManager,Administrator")]
    public async Task<IActionResult> Delegate(DelegationRequest request, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        if (request.GrantedToUserId == user.UserId || request.DelegatedRole is not ("CareCoordinator" or "CareManager") || string.IsNullOrWhiteSpace(request.ActionScope) || !ValidPrefix(request.RoutePrefix) || request.HttpMethods.Count == 0 || string.IsNullOrWhiteSpace(request.Reason) || request.StartsAt < now.AddMinutes(-5) || request.ExpiresAt <= request.StartsAt || request.ExpiresAt > request.StartsAt.AddDays(30))
            return BadRequest(new { message = "A different active user, supported delegated role, scoped route/actions, reason, and period of at most 30 days are required." });
        if (request.DelegatedRole == "CareManager" && !user.IsAdministrator) return Forbid();
        var target = await db.AppUsers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.GrantedToUserId && x.OrganizationId == tenant.OrganizationId && x.IsActive, token);
        if (target is null || target.Role is UserRole.Administrator or UserRole.BackOffice) return NotFound();
        var branch = request.BranchId ?? tenant.BranchId;
        if (!tenant.IsOrganizationWide && branch != tenant.BranchId) return NotFound();
        if (request.PersonId is Guid person && !await VisiblePerson(person, branch, token)) return NotFound();
        var id = Guid.NewGuid();
        await Execute("insert into access_delegations(id,organization_id,branch_id,person_id,granted_to_user_id,delegated_role,action_scope,route_prefix,http_methods,reason,starts_at,expires_at,granted_by_user_id,granted_by) values(@id,@organization,@targetBranch,@person,@target,@role,@scope,@prefix,@methods,@reason,@starts,@expires,@actorId,@actor)", c => { Add(c,"id",id); Add(c,"targetBranch",branch); Add(c,"person",request.PersonId); Add(c,"target",request.GrantedToUserId); Add(c,"role",request.DelegatedRole); Add(c,"scope",request.ActionScope.Trim()); Add(c,"prefix",request.RoutePrefix.Trim()); Add(c,"methods",Methods(request.HttpMethods)); Add(c,"reason",request.Reason.Trim()); Add(c,"starts",request.StartsAt); Add(c,"expires",request.ExpiresAt); }, token);
        await Event("Delegation", id, "Granted", request.Reason, branch, token);
        return Created($"/api/security/privileged-access/delegations/{id}", new { id });
    }

    [HttpPost("emergency")]
    public async Task<IActionResult> Emergency(EmergencyAccessRequest request, CancellationToken token)
    {
        if (user.UserId is null || tenant.BranchId is null || request.PersonId == Guid.Empty || string.IsNullOrWhiteSpace(request.ActionScope) || !ValidPrefix(request.RoutePrefix) || request.HttpMethods.Count == 0 || request.DurationMinutes is < 5 or > 240 || request.Justification.Trim().Length < 20) return BadRequest(new { message = "Person, scoped route/actions, a detailed justification, and a 5-240 minute duration are required." });
        if (!await VisiblePerson(request.PersonId, tenant.BranchId, token)) return NotFound();
        var id = Guid.NewGuid(); var expires = DateTimeOffset.UtcNow.AddMinutes(request.DurationMinutes);
        await Execute("insert into emergency_access_grants(id,organization_id,branch_id,person_id,user_id,action_scope,route_prefix,http_methods,justification,expires_at) values(@id,@organization,@branch,@person,@actorId,@scope,@prefix,@methods,@reason,@expires)", c => { Add(c,"id",id); Add(c,"person",request.PersonId); Add(c,"scope",request.ActionScope.Trim()); Add(c,"prefix",request.RoutePrefix.Trim()); Add(c,"methods",Methods(request.HttpMethods)); Add(c,"reason",request.Justification.Trim()); Add(c,"expires",expires); }, token);
        await Event("Emergency", id, "EmergencyActivated", request.Justification, tenant.BranchId, token);
        return Created($"/api/security/privileged-access/emergency/{id}", new { id, expiresAt = expires });
    }

    [HttpPost("{type:regex(^delegations|emergency$)}/{id:guid}/close")]
    public async Task<IActionResult> Close(string type, Guid id, CloseAccessRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) return BadRequest(new { message = "A closure reason is required." });
        var delegation = type.Equals("delegations", StringComparison.OrdinalIgnoreCase);
        if (delegation && !user.HasAnyRole(UserRole.CareManager, UserRole.Administrator)) return Forbid();
        var table = delegation ? "access_delegations" : "emergency_access_grants";
        var owner = delegation ? "granted_to_user_id" : "user_id";
        var sql = $"update {table} set status='Closed'," + (delegation ? "revoked_at=now(),revoked_by=@actor,revocation_reason=@reason" : "closed_at=now()") + $" where id=@id and organization_id=@organization and status='Active' and (@manager or {owner}=@actorId)";
        var changed = await Execute(sql, c => { Add(c,"id",id); Add(c,"reason",request.Reason.Trim()); Add(c,"manager",user.HasAnyRole(UserRole.CareManager,UserRole.Administrator)); }, token);
        if (changed == 0) return NotFound();
        await Event(delegation ? "Delegation" : "Emergency", id, delegation ? "Revoked" : "Closed", request.Reason, tenant.BranchId, token);
        return NoContent();
    }

    [HttpPost("{type:regex(^delegations|emergency$)}/{id:guid}/review")]
    [Authorize(Roles = "CareManager,Administrator")]
    public async Task<IActionResult> Review(string type, Guid id, ReviewAccessRequest request, CancellationToken token)
    {
        if (request.Decision is not ("Approved" or "Concern" or "Escalated") || string.IsNullOrWhiteSpace(request.Notes)) return BadRequest(new { message = "A supported review decision and notes are required." });
        var table = type.Equals("delegations",StringComparison.OrdinalIgnoreCase) ? "access_delegations" : "emergency_access_grants";
        var changed = await Execute($"update {table} set reviewed_at=now(),reviewed_by=@actor,review_decision=@decision,review_notes=@notes where id=@id and organization_id=@organization and reviewed_at is null", c => { Add(c,"id",id); Add(c,"decision",request.Decision); Add(c,"notes",request.Notes.Trim()); }, token);
        if (changed == 0) return NotFound();
        await Event(table == "access_delegations" ? "Delegation" : "Emergency", id, "Reviewed", $"{request.Decision}: {request.Notes}", tenant.BranchId, token);
        return NoContent();
    }

    private async Task<bool> VisiblePerson(Guid id, Guid? branch, CancellationToken token) => await db.ServiceUsers.AsNoTracking().AnyAsync(x => x.Id == id && x.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || x.BranchId == branch), token);
    private static bool ValidPrefix(string value) => value.StartsWith("/api/phase1/", StringComparison.OrdinalIgnoreCase) && !value.Contains("..", StringComparison.Ordinal);
    private static string[] Methods(IReadOnlyCollection<string> methods) => methods.Select(x => x.Trim().ToUpperInvariant()).Where(x => x is "GET" or "POST" or "PUT" or "PATCH" or "DELETE").Distinct().ToArray();
    private async Task<List<Dictionary<string,object?>>> Rows(string sql,CancellationToken token){var rows=new List<Dictionary<string,object?>>();await using var c=await Command(sql,token);await using var r=await c.ExecuteReaderAsync(token);while(await r.ReadAsync(token)){var row=new Dictionary<string,object?>();for(var i=0;i<r.FieldCount;i++)row[r.GetName(i)]=r.IsDBNull(i)?null:r.GetValue(i);rows.Add(row);}return rows;}
    private async Task<int> Execute(string sql,Action<DbCommand> bind,CancellationToken token){await using var c=await Command(sql,token);bind(c);return await c.ExecuteNonQueryAsync(token);}
    private async Task<DbCommand> Command(string sql,CancellationToken token){var connection=db.Database.GetDbConnection();if(connection.State!=ConnectionState.Open)await connection.OpenAsync(token);var c=connection.CreateCommand();c.CommandText=sql;Add(c,"organization",tenant.OrganizationId);Add(c,"branch",tenant.BranchId);Add(c,"organizationWide",tenant.IsOrganizationWide);Add(c,"actorId",user.UserId);Add(c,"actor",user.UserName);return c;}
    private async Task Event(string type,Guid id,string eventType,string detail,Guid? branch,CancellationToken token){await Execute("insert into privileged_access_events(id,organization_id,branch_id,access_type,access_id,event_type,actor_user_id,actor,detail) values(@event,@organization,@eventBranch,@type,@access,@eventType,@actorId,@actor,@detail)",c=>{Add(c,"event",Guid.NewGuid());Add(c,"eventBranch",branch);Add(c,"type",type);Add(c,"access",id);Add(c,"eventType",eventType);Add(c,"detail",detail);},token);}
    private static void Add(DbCommand c,string name,object? value){if(c.Parameters.Contains(name))return;var p=c.CreateParameter();p.ParameterName=name;p.Value=value??DBNull.Value;c.Parameters.Add(p);}
}

public sealed record DelegationRequest(Guid GrantedToUserId,string DelegatedRole,string ActionScope,string RoutePrefix,IReadOnlyCollection<string> HttpMethods,Guid? BranchId,Guid? PersonId,string Reason,DateTimeOffset StartsAt,DateTimeOffset ExpiresAt);
public sealed record EmergencyAccessRequest(Guid PersonId,string ActionScope,string RoutePrefix,IReadOnlyCollection<string> HttpMethods,string Justification,int DurationMinutes);
public sealed record CloseAccessRequest(string Reason);
public sealed record ReviewAccessRequest(string Decision,string Notes);
