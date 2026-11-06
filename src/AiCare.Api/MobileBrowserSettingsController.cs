using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[ApiController, Authorize(Policy="Phase1User")]
[Route("api/phase1/mobile-browser-settings")]
public sealed class MobileBrowserSettingsController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken token)
    {
        var rows = await db.Database.SqlQuery<MobileBrowserSettingsResponse>($"""
            select visit_alerts_enabled as "VisitAlertsEnabled",alert_lead_minutes as "AlertLeadMinutes",
              show_completed_visits as "ShowCompletedVisits",show_visit_type as "ShowVisitType",
              updated_by as "UpdatedBy",updated_at as "UpdatedAt"
            from mobile_browser_settings where organization_id={tenant.OrganizationId}
            """).ToListAsync(token);
        return Ok(rows.SingleOrDefault() ?? new MobileBrowserSettingsResponse(true,30,true,true,null,null));
    }

    [HttpPut, Authorize(Roles="Administrator")]
    public async Task<IActionResult> Save(MobileBrowserSettingsRequest request, CancellationToken token)
    {
        if (!tenant.IsOrganizationWide) return Forbid();
        if (request.AlertLeadMinutes is < 0 or > 240) return BadRequest(new { message="Reminder lead time must be between 0 and 240 minutes." });
        return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                insert into mobile_browser_settings(organization_id,visit_alerts_enabled,alert_lead_minutes,show_completed_visits,show_visit_type,updated_by)
                values({tenant.OrganizationId},{request.VisitAlertsEnabled},{request.AlertLeadMinutes},{request.ShowCompletedVisits},{request.ShowVisitType},{user.UserName})
                on conflict(organization_id) do update set visit_alerts_enabled=excluded.visit_alerts_enabled,
                  alert_lead_minutes=excluded.alert_lead_minutes,show_completed_visits=excluded.show_completed_visits,
                  show_visit_type=excluded.show_visit_type,updated_by=excluded.updated_by,updated_at=now()
                """, token);
            db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),"admin.mobile_browser_settings_saved",user.UserName,"Organization",
                tenant.OrganizationId,DateTimeOffset.UtcNow,tenant.OrganizationId,tenant.BranchId ?? TenantDefaults.BranchId));
            await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return NoContent();
        });
    }
}
public sealed record MobileBrowserSettingsRequest(bool VisitAlertsEnabled,int AlertLeadMinutes,bool ShowCompletedVisits,bool ShowVisitType);
public sealed record MobileBrowserSettingsResponse(bool VisitAlertsEnabled,int AlertLeadMinutes,bool ShowCompletedVisits,bool ShowVisitType,string? UpdatedBy,DateTimeOffset? UpdatedAt);
