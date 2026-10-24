using System.Data;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

internal static class AppointmentWorkflow
{
    private static readonly TimeZoneInfo UkZone = ResolveUkZone();

    public static DateOnly UkDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, UkZone).DateTime);

    public static DateTimeOffset UkStart(DateOnly day) => new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), UkZone));

    private static TimeZoneInfo ResolveUkZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"); }
    }

    public static Guid[] AssignedVisitIds(CareDbContext db, Guid organization, Guid branch, Guid worker)
    {
        if (!db.Database.IsNpgsql()) return [];
        var connection = db.Database.GetDbConnection(); var close = connection.State != ConnectionState.Open;
        if (close) connection.Open();
        try
        {
            using var command = connection.CreateCommand(); command.CommandText = "select visit_id from visit_care_worker_assignments where organization_id=@org and branch_id=@branch and care_worker_id=@worker";
            Add(command,"org",organization); Add(command,"branch",branch); Add(command,"worker",worker);
            using var reader = command.ExecuteReader(); var ids = new List<Guid>(); while (reader.Read()) ids.Add(reader.GetGuid(0)); return ids.ToArray();
        }
        finally { if (close) connection.Close(); }
    }

    public static (string[] Blocks, string[] Warnings) Check(CareDbContext db, ITenantContext tenant, Guid branch, Guid worker, DateTimeOffset start, int duration, Guid? exclude = null)
    {
        var config = AppointmentConfigurationStore.Load(db, tenant.OrganizationId, branch);
        var blocks = new List<string>(); var warnings = new List<string>();
        if (config.BreaksEnabled && AppointmentConfigurationStore.HasBreak(db, tenant.OrganizationId, branch, worker, start, duration)) blocks.Add("Appointment overlaps the care worker's configured break.");
        var rules = config.BookingRules;
        if (rules.MaxWorkerVisitsPerDay > 0 || rules.WarnWorkerVisitsPerDay > 0)
        {
            var day = UkDate(start);
            var additional = AssignedVisitIds(db, tenant.OrganizationId, branch, worker);
            var count = db.Visits.AsNoTracking().Where(x => x.OrganizationId == tenant.OrganizationId && x.BranchId == branch && (x.CareWorkerId == worker || additional.Contains(x.Id)) && x.Status != VisitStatus.Cancelled && (exclude == null || x.Id != exclude)).AsEnumerable().Count(x => UkDate(x.StartsAt) == day);
            if (rules.MaxWorkerVisitsPerDay > 0 && count >= rules.MaxWorkerVisitsPerDay) blocks.Add($"Care worker has reached the branch limit of {rules.MaxWorkerVisitsPerDay} visits that day.");
            else if (rules.WarnWorkerVisitsPerDay > 0 && count >= rules.WarnWorkerVisitsPerDay) warnings.Add($"Care worker already has {count} visits that day.");
        }
        if (rules.WarnShortNoticeHours > 0 && start < DateTimeOffset.UtcNow.AddHours(rules.WarnShortNoticeHours)) warnings.Add("This appointment is being scheduled at short notice.");
        return (blocks.ToArray(), warnings.ToArray());
    }

    public static string? RunName(CareDbContext db, Guid organization, Guid branch, Guid visit)
    {
        if (!db.Database.IsNpgsql()) return null;
        var connection = db.Database.GetDbConnection(); var close = connection.State != ConnectionState.Open;
        if (close) connection.Open();
        try
        {
            using var command = connection.CreateCommand(); command.CommandText = "select r.name from appointment_run_visits a join appointment_runs r on r.id=a.run_id and r.organization_id=a.organization_id and r.branch_id=a.branch_id where a.visit_id=@visit and a.organization_id=@org and a.branch_id=@branch";
            Add(command,"visit",visit); Add(command,"org",organization); Add(command,"branch",branch);
            return command.ExecuteScalar() as string;
        }
        finally { if (close) connection.Close(); }
    }

    public static object Monitoring(Visit visit, AppointmentMonitoring setting, DateTimeOffset now)
    {
        var state = MonitoringState(visit, setting, now);
        return new { state, lateMinutes = setting.LateMinutes, missedMinutes = setting.MissedMinutes, shortenedMinutes = setting.ShortenedMinutes, deviceOptions = setting.DeviceOptions ?? ["Mobile app", "Manual entry"] };
    }

    public static string MonitoringState(Visit visit, AppointmentMonitoring setting, DateTimeOffset now)
    {
        var scheduledEnd = visit.StartsAt.AddMinutes(visit.DurationMinutes);
        var state = visit.Status == VisitStatus.Cancelled ? "Cancelled" :
            visit.Status == VisitStatus.Missed ? "Missed visit" :
            visit.CheckedInAt is null && now >= visit.StartsAt.AddMinutes(setting.MissedMinutes) ? "Missed check-in" :
            visit.CheckedInAt is null && now >= visit.StartsAt.AddMinutes(setting.LateMinutes) ? "Late check-in" :
            visit.CheckedInAt is null ? "Awaiting check-in" :
            visit.CheckedOutAt is not null && (visit.CheckedOutAt.Value - visit.CheckedInAt.Value).TotalMinutes < visit.DurationMinutes - setting.ShortenedMinutes ? "Shortened care" :
            visit.CheckedOutAt is null && now > scheduledEnd.AddMinutes(setting.MissedMinutes) ? "Missing check-out" :
            visit.CheckedInAt > visit.StartsAt.AddMinutes(setting.LateMinutes) ? "Late check-in" : "On time";
        return state;
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object value)
    { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter); }
}
