using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[ApiController, Authorize(Policy = "Phase1User")]
[Route("api/phase1/admin/appointment-start-variance")]
public sealed class AppointmentStartVarianceController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user) : ControllerBase
{
    [HttpGet, Authorize(Roles = "Administrator,CareManager,CareCoordinator")]
    public async Task<IActionResult> Get(CancellationToken token)
    {
        var minutes = await db.Database.SqlQuery<int>($"""select coalesce(max_change_minutes,0) as "Value" from appointment_start_variance_settings where organization_id={tenant.OrganizationId}""").SingleOrDefaultAsync(token);
        return Ok(new AppointmentStartVarianceRequest(minutes == 0 ? null : minutes));
    }

    [HttpPut, Authorize(Roles = "Administrator")]
    public async Task<IActionResult> Save(AppointmentStartVarianceRequest request, CancellationToken token)
    {
        if (!tenant.IsOrganizationWide) return Forbid();
        if (request.MaxChangeMinutes is < 1 or > 10080) return BadRequest(new { message = "Choose 1 to 10080 minutes, or leave empty to allow any reschedule." });
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                insert into appointment_start_variance_settings(organization_id,max_change_minutes,updated_by)
                values({tenant.OrganizationId},{request.MaxChangeMinutes},{user.UserName})
                on conflict(organization_id) do update set max_change_minutes=excluded.max_change_minutes,updated_by=excluded.updated_by,updated_at=now()
                """, token);
            db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "appointments.start_variance_updated", user.UserName, "AppointmentStartVariance", tenant.OrganizationId, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        });
        return NoContent();
    }
}

public sealed record AppointmentStartVarianceRequest(int? MaxChangeMinutes);
