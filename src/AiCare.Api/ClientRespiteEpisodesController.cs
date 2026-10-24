using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[ApiController]
[Authorize(Policy = "Phase1User", Roles = "Administrator,CareManager,CareCoordinator")]
[Route("api/phase1/service-users/{serviceUserId:guid}/respite-episodes")]
public sealed class ClientRespiteEpisodesController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid serviceUserId, CancellationToken token)
    {
        if (await Person(serviceUserId, token) is null) return NotFound();
        return Ok(await db.Database.SqlQuery<RespiteEpisodeResponse>($"""
            select id as "Id", service_user_id as "ServiceUserId", reason as "Reason",
                   starts_at as "StartsAt", ends_at as "EndsAt", notes as "Notes",
                   status as "Status", created_by as "CreatedBy", created_at as "CreatedAt",
                   cancelled_by as "CancelledBy", cancelled_at as "CancelledAt",
                   cancellation_reason as "CancellationReason"
            from client_respite_episodes
            where organization_id={tenant.OrganizationId} and service_user_id={serviceUserId}
            order by starts_at desc
            """).ToListAsync(token));
    }

    [HttpPost]
    public async Task<IActionResult> Create(Guid serviceUserId, CreateRespiteEpisodeRequest request, CancellationToken token)
    {
        var person = await Person(serviceUserId, token);
        if (person is null) return NotFound();
        if (request.StartsAt >= request.EndsAt || request.StartsAt == default || request.EndsAt == default)
            return BadRequest(new { message = "Respite end must be after the start." });
        if (request.EndsAt <= DateTimeOffset.UtcNow)
            return BadRequest(new { message = "Respite must include a future period." });
        if (request.Notes?.Length > 2000) return BadRequest(new { message = "Notes must be 2000 characters or less." });
        var reason = request.Reason?.Trim() ?? "";
        if (reason.Length is < 1 or > 120) return BadRequest(new { message = "Choose an active respite reason." });
        var validReason = await db.Database.SqlQuery<string>($"""
            select value as "Value" from care_operation_options
            where organization_id={tenant.OrganizationId} and kind='ClientRespiteReason' and active=true
            """).ToListAsync(token);
        var configured = validReason.FirstOrDefault(value => value.Equals(reason, StringComparison.OrdinalIgnoreCase));
        if (configured is null) return BadRequest(new { message = "Choose an active respite reason configured in Administration." });

        await using var transaction = await db.Database.BeginTransactionAsync(token);
        // Serialize episode creation and visit scheduling for this client.
        await db.Database.ExecuteSqlInterpolatedAsync($"select pg_advisory_xact_lock(hashtextextended({serviceUserId.ToString()}, 0))", token);
        var overlap = await db.Database.SqlQuery<bool>($"""
            select exists(select 1 from client_respite_episodes
            where organization_id={tenant.OrganizationId} and service_user_id={serviceUserId}
              and status='Scheduled' and starts_at<{request.EndsAt} and ends_at>{request.StartsAt}) as "Value"
            """).SingleAsync(token);
        if (overlap) return Conflict(new { message = "This client already has a respite episode in that period." });
        var futureVisit = await db.Visits.AsNoTracking().AnyAsync(visit =>
            visit.OrganizationId == tenant.OrganizationId && visit.ServiceUserId == serviceUserId &&
            visit.Status != VisitStatus.Cancelled && visit.Status != VisitStatus.Completed &&
            visit.StartsAt >= DateTimeOffset.UtcNow && visit.StartsAt < request.EndsAt &&
            visit.StartsAt.AddMinutes(visit.DurationMinutes) > request.StartsAt, token);
        if (futureVisit) return Conflict(new { message = "Cancel or reschedule visits in this period before recording respite." });

        var id = Guid.NewGuid();
        var branchId = person.BranchId ?? TenantDefaults.BranchId;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into client_respite_episodes(id,organization_id,branch_id,service_user_id,reason,starts_at,ends_at,notes,created_by)
            values({id},{tenant.OrganizationId},{branchId},{serviceUserId},{configured},{request.StartsAt},{request.EndsAt},{request.Notes?.Trim() ?? ""},{user.UserName})
            """, token);
        db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "client_respite.created", user.UserName, "ClientRespiteEpisode", id, DateTimeOffset.UtcNow, tenant.OrganizationId, branchId));
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return Created($"/api/phase1/service-users/{serviceUserId}/respite-episodes/{id}", new { id });
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid serviceUserId, Guid id, CancelRespiteEpisodeRequest request, CancellationToken token)
    {
        var person = await Person(serviceUserId, token);
        if (person is null) return NotFound();
        var reason = request.Reason?.Trim() ?? "";
        if (reason.Length is < 1 or > 500) return BadRequest(new { message = "A cancellation reason up to 500 characters is required." });
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            update client_respite_episodes set status='Cancelled',cancelled_by={user.UserName},cancelled_at=now(),cancellation_reason={reason}
            where id={id} and organization_id={tenant.OrganizationId} and service_user_id={serviceUserId} and status='Scheduled'
            """, token);
        if (changed == 0) return NotFound();
        db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "client_respite.cancelled", user.UserName, "ClientRespiteEpisode", id, DateTimeOffset.UtcNow, tenant.OrganizationId, person.BranchId ?? TenantDefaults.BranchId));
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return NoContent();
    }

    private async Task<ServiceUser?> Person(Guid id, CancellationToken token)
    {
        var person = await db.ServiceUsers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, token);
        return person is not null && tenant.CanAccess(person.OrganizationId, person.BranchId) ? person : null;
    }
}

public sealed record CreateRespiteEpisodeRequest(string Reason, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Notes);
public sealed record CancelRespiteEpisodeRequest(string Reason);
public sealed record RespiteEpisodeResponse(Guid Id, Guid ServiceUserId, string Reason, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Notes, string Status, string CreatedBy, DateTimeOffset CreatedAt, string? CancelledBy, DateTimeOffset? CancelledAt, string? CancellationReason);
