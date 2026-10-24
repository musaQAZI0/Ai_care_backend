using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

public sealed class VisitAlertMonitoringService(CareDbContext db)
{
    public async Task<int> ScanAsync(CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var visits = await db.Visits.AsNoTracking().Where(visit => visit.StartsAt >= now.AddDays(-7)
            && visit.StartsAt <= now && visit.Status != VisitStatus.Cancelled).ToListAsync(token);
        var created = 0;
        foreach (var visit in visits)
        {
            var branch = visit.BranchId ?? TenantDefaults.BranchId;
            var settings = AppointmentConfigurationStore.Load(db, visit.OrganizationId ?? TenantDefaults.OrganizationId, branch).MonitoringSettings;
            var state = AppointmentWorkflow.MonitoringState(visit, settings, now);
            var kind = state switch
            {
                "Late check-in" => "Late",
                "Missed check-in" or "Missed visit" => "Missed",
                "Missing check-out" => "MissingCheckOut",
                "Shortened care" => "Shortened",
                _ => null
            };
            if (kind is null) continue;
            var severity = kind == "Missed" ? "High" : "Medium";
            var id = Guid.NewGuid();
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                insert into visit_exceptions(id,visit_id,service_user_id,organization_id,branch_id,exception_type,severity,
                  reason,immediate_action,notify_manager,follow_up_owner,escalation_due_at,status,created_by)
                values({id},{visit.Id},{visit.ServiceUserId},{visit.OrganizationId ?? TenantDefaults.OrganizationId},{branch},
                  {kind},{severity},{state},{"Contact the worker or client and record the follow-up."},{true},
                  {"Care coordinator"},{now.Add(kind == "Missed" ? TimeSpan.FromMinutes(15) : TimeSpan.FromHours(1))},
                  {"Open"},{"System monitoring"})
                on conflict do nothing
                """, token);
            if (inserted == 0) continue;
            created++;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                insert into visit_escalation_events(id,visit_exception_id,organization_id,action,detail,actor)
                values({Guid.NewGuid()},{id},{visit.OrganizationId ?? TenantDefaults.OrganizationId},{"Created"},{state},{"System monitoring"})
                """, token);
        }
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into visit_escalation_events(id,visit_exception_id,organization_id,action,detail,actor)
            select gen_random_uuid(),e.id,e.organization_id,'Overdue','Follow-up is past its due time','System monitoring'
            from visit_exceptions e where e.created_by='System monitoring' and e.status in ('Open','Acknowledged')
              and e.escalation_due_at<{now}
            on conflict do nothing
            """, token);
        return created;
    }
}

internal sealed class VisitAlertMonitoringWorker(IServiceScopeFactory scopes, ILogger<VisitAlertMonitoringWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<VisitAlertMonitoringService>().ScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Visit alert monitoring cycle failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
