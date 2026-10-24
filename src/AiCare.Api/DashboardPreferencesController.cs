using System.Text.Json;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[ApiController, Authorize(Policy = "Phase1User")]
[Route("api/phase1/dashboard/preferences")]
public sealed class DashboardPreferencesController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user) : ControllerBase
{
    private static readonly string[] Defaults = ["stats", "operations", "people", "visits", "workforce", "reviews"];

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken token)
    {
        if (user.UserId is not Guid userId) return Unauthorized();
        var saved = await db.Database.SqlQuery<string>($"select widgets as \"Value\" from dashboard_preferences where organization_id={tenant.OrganizationId} and user_id={userId}").FirstOrDefaultAsync(token);
        return Ok(new { widgets = saved is null ? Defaults : JsonSerializer.Deserialize<string[]>(saved) ?? Defaults });
    }

    [HttpPut]
    public async Task<IActionResult> Save(DashboardPreferenceRequest request, CancellationToken token)
    {
        if (user.UserId is not Guid userId) return Unauthorized();
        if (request.Widgets is null || request.Widgets.Length > Defaults.Length || request.Widgets.Distinct(StringComparer.Ordinal).Count() != request.Widgets.Length || request.Widgets.Any(widget => !Defaults.Contains(widget, StringComparer.Ordinal)))
            return BadRequest(new { message = "Choose only the available dashboard sections." });
        var value = JsonSerializer.Serialize(request.Widgets);
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await db.Database.ExecuteSqlInterpolatedAsync($"insert into dashboard_preferences(organization_id,user_id,widgets) values({tenant.OrganizationId},{userId},{value}) on conflict(organization_id,user_id) do update set widgets=excluded.widgets,updated_at=now()", token);
            db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "dashboard.preferences_saved", user.UserName, "DashboardPreferences", userId, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        });
        return NoContent();
    }
}

public sealed record DashboardPreferenceRequest(string[] Widgets);
