using System.Data;
using System.Text.Json;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

public sealed record AppointmentRules(int MaxWorkerVisitsPerDay = 0, int WarnWorkerVisitsPerDay = 0, int WarnShortNoticeHours = 0);
public sealed record AppointmentAppearance(string Label = "", string Colour = "#2563eb", bool WarnUnassigned = true, bool WarnConflicts = true);
public sealed record AppointmentMonitoring(int LateMinutes = 15, int MissedMinutes = 30, int ShortenedMinutes = 15, string[]? DeviceOptions = null);
public sealed record CarerWeights(int Availability = 40, int Skills = 30, int Continuity = 20, int Workload = 10);
public sealed record AppointmentConfig(AppointmentRules? Rules = null, AppointmentAppearance? Appearance = null, bool BreaksEnabled = false, AppointmentMonitoring? Monitoring = null, CarerWeights? Weights = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public AppointmentRules BookingRules => Rules ?? new();
    [System.Text.Json.Serialization.JsonIgnore]
    public AppointmentAppearance RosterAppearance => Appearance ?? new();
    [System.Text.Json.Serialization.JsonIgnore]
    public AppointmentMonitoring MonitoringSettings => Monitoring ?? new();
    [System.Text.Json.Serialization.JsonIgnore]
    public CarerWeights RecommendationWeights => Weights ?? new();
}
public sealed record AppointmentConfigRequest(Guid BranchId, AppointmentConfig Configuration);
public sealed record PresetRequest(Guid BranchId, string Name, string VisitType, int DurationMinutes, string RequiredSkills, bool Active = true);
public sealed record RunRequest(Guid BranchId, string Name, bool Active = true);
public sealed record BreakRequest(Guid BranchId, Guid CareWorkerId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Note);
public sealed record RunAssignmentRequest(Guid RunId);
public sealed record RecommendationRequest(Guid BranchId, Guid ServiceUserId, DateTimeOffset StartsAt, int DurationMinutes, string RequiredSkills);
public sealed record RecommendationAbsence(Guid CareWorkerId, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

internal static class AppointmentConfigurationStore
{
    public static AppointmentConfig Load(CareDbContext db, Guid organization, Guid? branch)
    {
        if (branch is null || !db.Database.IsNpgsql()) return new();
        var connection = db.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close) connection.Open();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "select configuration::text from appointment_configuration where organization_id=@org and branch_id=@branch";
            Add(command, "org", organization); Add(command, "branch", branch.Value);
            var json = command.ExecuteScalar() as string;
            return json is null ? new() : JsonSerializer.Deserialize<AppointmentConfig>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new();
        }
        finally { if (close) connection.Close(); }
    }

    public static bool HasBreak(CareDbContext db, Guid organization, Guid branch, Guid worker, DateTimeOffset start, int duration)
    {
        if (!db.Database.IsNpgsql()) return false;
        var connection = db.Database.GetDbConnection(); var close = connection.State != ConnectionState.Open;
        if (close) connection.Open();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "select exists(select 1 from appointment_breaks where organization_id=@org and branch_id=@branch and care_worker_id=@worker and cancelled_at is null and starts_at<@end and ends_at>@start)";
            Add(command,"org",organization); Add(command,"branch",branch); Add(command,"worker",worker); Add(command,"start",start); Add(command,"end",start.AddMinutes(duration));
            return (bool)(command.ExecuteScalar() ?? false);
        }
        finally { if (close) connection.Close(); }
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object value)
    { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter); }
}

[ApiController, Authorize(Policy = "Phase1User")]
[Route("api/phase1/admin/appointment-configuration")]
public sealed class AppointmentConfigurationController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user) : ControllerBase
{
    private bool BranchAllowed(Guid branch) => branch != Guid.Empty && tenant.CanAccess(tenant.OrganizationId, branch) && db.Branches.Any(x => x.Id == branch && x.OrganizationId == tenant.OrganizationId);
    private void Audit(string action, Guid subject, Guid branch) => db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), action, user.UserName, "AppointmentConfiguration", subject, DateTimeOffset.UtcNow, tenant.OrganizationId, branch));
    private async Task EnsureConfig(Guid branch, CancellationToken token)
    {
        const string emptyConfiguration = "{}";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into appointment_configuration(organization_id,branch_id,configuration,updated_by)
            values({tenant.OrganizationId},{branch},{emptyConfiguration}::jsonb,{user.UserName}) on conflict do nothing
            """, token);
    }

    [HttpGet]
    [Authorize(Roles = "Administrator,CareManager,CareCoordinator")]
    public IActionResult Get([FromQuery] Guid branchId)
    {
        if (!BranchAllowed(branchId)) return NotFound();
        var config = AppointmentConfigurationStore.Load(db, tenant.OrganizationId, branchId);
        var monitoring = config.MonitoringSettings;
        return Ok(new { rules = config.BookingRules, appearance = config.RosterAppearance, config.BreaksEnabled, monitoring = new { monitoring.LateMinutes, monitoring.MissedMinutes, monitoring.ShortenedMinutes, deviceOptions = monitoring.DeviceOptions ?? ["Mobile app", "Manual entry"] }, weights = config.RecommendationWeights });
    }

    [HttpPut, Authorize(Roles = "Administrator")]
    public async Task<IActionResult> Save(AppointmentConfigRequest request, CancellationToken token)
    {
        if (!BranchAllowed(request.BranchId)) return NotFound();
        var config = request.Configuration;
        var rules = config.BookingRules; var monitoring = config.MonitoringSettings; var weights = config.RecommendationWeights;
        if (new[] { rules.MaxWorkerVisitsPerDay, rules.WarnWorkerVisitsPerDay, rules.WarnShortNoticeHours, monitoring.LateMinutes, monitoring.MissedMinutes, monitoring.ShortenedMinutes, weights.Availability, weights.Skills, weights.Continuity, weights.Workload }.Any(x => x < 0 || x > 10080)) return BadRequest(new { message = "Configuration values must be nonnegative and within the supported range." });
        if (rules.MaxWorkerVisitsPerDay > 0 && rules.WarnWorkerVisitsPerDay > rules.MaxWorkerVisitsPerDay) return BadRequest(new { message = "The warning count cannot exceed the booking limit." });
        if (weights.Availability + weights.Skills + weights.Continuity + weights.Workload != 100) return BadRequest(new { message = "Recommendation weights must total 100." });
        if (!System.Text.RegularExpressions.Regex.IsMatch(config.RosterAppearance.Colour, "^#[0-9a-fA-F]{6}$")) return BadRequest(new { message = "Use a six-digit colour value." });
        var devices = monitoring.DeviceOptions ?? [];
        if (devices.Any(x => x is not ("Mobile app" or "Manual entry"))) return BadRequest(new { message = "Only Mobile app and Manual entry are supported. No external device integration is available." });
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into appointment_configuration(organization_id,branch_id,configuration,updated_by)
            values({tenant.OrganizationId},{request.BranchId},{json}::jsonb,{user.UserName})
            on conflict(organization_id,branch_id) do update set configuration=excluded.configuration,updated_by=excluded.updated_by,updated_at=now()
            """, token);
        Audit("appointments.configuration_updated", request.BranchId, request.BranchId);
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return NoContent();
    }

    [HttpGet("presets")]
    [Authorize(Roles = "Administrator,CareManager,CareCoordinator")]
    public async Task<IActionResult> Presets([FromQuery] Guid branchId, CancellationToken token)
    {
        if (!BranchAllowed(branchId)) return NotFound();
        return Ok(await db.Database.SqlQuery<PresetRow>($"""select id as "Id",name as "Name",visit_type as "VisitType",duration_minutes as "DurationMinutes",required_skills as "RequiredSkills",active as "Active" from appointment_presets where organization_id={tenant.OrganizationId} and branch_id={branchId} order by name""").ToListAsync(token));
    }
    [HttpPut("presets/{id:guid}"), Authorize(Roles = "Administrator")]
    public async Task<IActionResult> SavePreset(Guid id, PresetRequest request, CancellationToken token)
    {
        if (!BranchAllowed(request.BranchId)) return NotFound();
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.VisitType) || request.DurationMinutes is < 1 or > 1440) return BadRequest(new { message = "Name, visit type and duration from 1 to 1440 minutes are required." });
        await using var transaction = await db.Database.BeginTransactionAsync(token); await EnsureConfig(request.BranchId, token);
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""insert into appointment_presets(id,organization_id,branch_id,name,visit_type,duration_minutes,required_skills,active,updated_by) values({id},{tenant.OrganizationId},{request.BranchId},{request.Name.Trim()},{request.VisitType.Trim()},{request.DurationMinutes},{request.RequiredSkills ?? ""},{request.Active},{user.UserName}) on conflict(id) do update set name=excluded.name,visit_type=excluded.visit_type,duration_minutes=excluded.duration_minutes,required_skills=excluded.required_skills,active=excluded.active,updated_by=excluded.updated_by,updated_at=now() where appointment_presets.organization_id={tenant.OrganizationId} and appointment_presets.branch_id={request.BranchId}""", token);
        if (changed == 0) return NotFound(); Audit("appointments.preset_saved", id, request.BranchId); await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return NoContent();
    }

    [HttpGet("runs"), Authorize(Roles = "Administrator,CareManager,CareCoordinator")]
    public async Task<IActionResult> Runs([FromQuery] Guid branchId, CancellationToken token)
    {
        if (!BranchAllowed(branchId)) return NotFound();
        return Ok(await db.Database.SqlQuery<RunRow>($"""select id as "Id",name as "Name",active as "Active" from appointment_runs where organization_id={tenant.OrganizationId} and branch_id={branchId} order by name""").ToListAsync(token));
    }
    [HttpPut("runs/{id:guid}"), Authorize(Roles = "Administrator")]
    public async Task<IActionResult> SaveRun(Guid id, RunRequest request, CancellationToken token)
    {
        if (!BranchAllowed(request.BranchId)) return NotFound();
        if (string.IsNullOrWhiteSpace(request.Name)) return BadRequest(new { message = "Run name is required." });
        await using var transaction = await db.Database.BeginTransactionAsync(token); await EnsureConfig(request.BranchId, token);
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""insert into appointment_runs(id,organization_id,branch_id,name,active,updated_by) values({id},{tenant.OrganizationId},{request.BranchId},{request.Name.Trim()},{request.Active},{user.UserName}) on conflict(id) do update set name=excluded.name,active=excluded.active,updated_by=excluded.updated_by,updated_at=now() where appointment_runs.organization_id={tenant.OrganizationId} and appointment_runs.branch_id={request.BranchId}""", token);
        if (changed == 0) return NotFound(); Audit("appointments.run_saved", id, request.BranchId); await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return NoContent();
    }

    [HttpPut("visits/{visitId:guid}/run"), Authorize(Roles = "Administrator,CareManager,CareCoordinator")]
    public async Task<IActionResult> AssignRun(Guid visitId, RunAssignmentRequest request, CancellationToken token)
    {
        var visit = await db.Visits.AsNoTracking().FirstOrDefaultAsync(x => x.Id == visitId && x.OrganizationId == tenant.OrganizationId, token);
        if (visit?.BranchId is not Guid branch || !BranchAllowed(branch)) return NotFound();
        var run = await db.Database.SqlQuery<Guid>($"""select id as "Value" from appointment_runs where id={request.RunId} and organization_id={tenant.OrganizationId} and branch_id={branch} and active=true""").SingleOrDefaultAsync(token);
        if (run == Guid.Empty) return NotFound();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await db.Database.ExecuteSqlInterpolatedAsync($"""insert into appointment_run_visits(visit_id,run_id,organization_id,branch_id) values({visitId},{run},{tenant.OrganizationId},{branch}) on conflict(visit_id) do update set run_id=excluded.run_id where appointment_run_visits.organization_id={tenant.OrganizationId} and appointment_run_visits.branch_id={branch}""", token);
        Audit("appointments.run_assigned", visitId, branch); await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return NoContent();
    }

    [HttpGet("breaks"), Authorize(Roles = "Administrator,CareManager,CareCoordinator")]
    public async Task<IActionResult> Breaks([FromQuery] Guid branchId, [FromQuery] DateTimeOffset from, [FromQuery] DateTimeOffset to, CancellationToken token)
    {
        if (!BranchAllowed(branchId)) return NotFound();
        if (to <= from || to - from > TimeSpan.FromDays(62)) return BadRequest(new { message = "Choose a range of up to 62 days." });
        return Ok(await db.Database.SqlQuery<BreakRow>($"""select id as "Id",care_worker_id as "CareWorkerId",starts_at as "StartsAt",ends_at as "EndsAt",note as "Note" from appointment_breaks where organization_id={tenant.OrganizationId} and branch_id={branchId} and cancelled_at is null and starts_at<{to} and ends_at>{from} order by starts_at""").ToListAsync(token));
    }
    [HttpPost("breaks"), Authorize(Roles = "Administrator,CareManager,CareCoordinator")]
    public async Task<IActionResult> AddBreak(BreakRequest request, CancellationToken token)
    {
        if (!BranchAllowed(request.BranchId)) return NotFound();
        if (!AppointmentConfigurationStore.Load(db, tenant.OrganizationId, request.BranchId).BreaksEnabled) return Conflict(new { message = "Appointment breaks are disabled for this branch." });
        if (request.EndsAt <= request.StartsAt || request.EndsAt - request.StartsAt > TimeSpan.FromHours(12)) return BadRequest(new { message = "Break duration must be positive and at most 12 hours." });
        if (!await db.CareWorkers.AnyAsync(x => x.Id == request.CareWorkerId && x.OrganizationId == tenant.OrganizationId && x.BranchId == request.BranchId, token)) return NotFound();
        var assigned = AppointmentWorkflow.AssignedVisitIds(db, tenant.OrganizationId, request.BranchId, request.CareWorkerId);
        var overlap = (await db.Visits.AsNoTracking().Where(x => x.OrganizationId == tenant.OrganizationId && x.BranchId == request.BranchId && (x.CareWorkerId == request.CareWorkerId || assigned.Contains(x.Id)) && x.Status != VisitStatus.Cancelled && x.StartsAt < request.EndsAt).ToListAsync(token)).Any(x => x.StartsAt.AddMinutes(x.DurationMinutes) > request.StartsAt);
        if (overlap || AppointmentConfigurationStore.HasBreak(db, tenant.OrganizationId, request.BranchId, request.CareWorkerId, request.StartsAt, (int)(request.EndsAt-request.StartsAt).TotalMinutes)) return Conflict(new { message = "Break overlaps a visit or another break." });
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var id = Guid.NewGuid(); await db.Database.ExecuteSqlInterpolatedAsync($"""insert into appointment_breaks(id,organization_id,branch_id,care_worker_id,starts_at,ends_at,note,created_by) values({id},{tenant.OrganizationId},{request.BranchId},{request.CareWorkerId},{request.StartsAt},{request.EndsAt},{request.Note ?? ""},{user.UserName})""", token);
        Audit("appointments.break_created", id, request.BranchId); await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return Created($"/api/phase1/admin/appointment-configuration/breaks/{id}", new { id });
    }
    [HttpDelete("breaks/{id:guid}"), Authorize(Roles = "Administrator,CareManager,CareCoordinator")]
    public async Task<IActionResult> CancelBreak(Guid id, CancellationToken token)
    {
        var branch = await db.Database.SqlQuery<Guid>($"""select branch_id as "Value" from appointment_breaks where id={id} and organization_id={tenant.OrganizationId} and cancelled_at is null""").SingleOrDefaultAsync(token);
        if (branch == Guid.Empty || !BranchAllowed(branch)) return NotFound();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await db.Database.ExecuteSqlInterpolatedAsync($"""update appointment_breaks set cancelled_at=now() where id={id} and organization_id={tenant.OrganizationId} and branch_id={branch}""", token);
        Audit("appointments.break_cancelled", id, branch); await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return NoContent();
    }

    [HttpPost("recommendations"), Authorize(Roles = "Administrator,CareManager,CareCoordinator")]
    public IActionResult Recommend(RecommendationRequest request)
    {
        if (!BranchAllowed(request.BranchId)) return NotFound();
        if (request.DurationMinutes is < 1 or > 1440 || request.StartsAt == default) return BadRequest(new { message = "Choose a start time and duration." });
        var client = db.ServiceUsers.AsNoTracking().SingleOrDefault(x => x.Id == request.ServiceUserId && x.OrganizationId == tenant.OrganizationId && x.BranchId == request.BranchId);
        if (client is null) return NotFound();
        var weights = AppointmentConfigurationStore.Load(db, tenant.OrganizationId, request.BranchId).RecommendationWeights;
        var workers = db.CareWorkers.AsNoTracking().Where(x => x.OrganizationId == tenant.OrganizationId && x.BranchId == request.BranchId).ToList();
        var travelBuffer = db.Database.SqlQuery<int>($"""select coalesce(travel_buffer_minutes,0) as "Value" from scheduling_policies where organization_id={tenant.OrganizationId} and branch_id={request.BranchId}""").SingleOrDefault();
        var absences = db.Database.SqlQuery<RecommendationAbsence>($"""
            select care_worker_id as "CareWorkerId",starts_at as "StartsAt",ends_at as "EndsAt"
            from worker_absences where organization_id={tenant.OrganizationId} and branch_id={request.BranchId}
              and status in ('Approved','Confirmed') and starts_at<{request.StartsAt.AddMinutes(request.DurationMinutes)} and ends_at>{request.StartsAt}
            """).ToList();
        var results = workers.Select(worker =>
        {
            var assigned = AppointmentWorkflow.AssignedVisitIds(db, tenant.OrganizationId, request.BranchId, worker.Id);
            var visits = db.Visits.AsNoTracking().Where(x => x.OrganizationId == tenant.OrganizationId && x.BranchId == request.BranchId && (x.CareWorkerId == worker.Id || assigned.Contains(x.Id)) && x.Status != VisitStatus.Cancelled).ToList();
            var scheduledEnd = request.StartsAt.AddMinutes(request.DurationMinutes);
            var travelReady = !visits.Any(x =>
                x.ServiceUserId != request.ServiceUserId &&
                (x.StartsAt.AddMinutes(x.DurationMinutes) <= request.StartsAt && request.StartsAt - x.StartsAt.AddMinutes(x.DurationMinutes) < TimeSpan.FromMinutes(travelBuffer)
                || x.StartsAt >= scheduledEnd && x.StartsAt - scheduledEnd < TimeSpan.FromMinutes(travelBuffer)));
            var absent = absences.Any(x => x.CareWorkerId == worker.Id);
            var available = !absent && travelReady && !visits.Any(x => request.StartsAt < x.StartsAt.AddMinutes(x.DurationMinutes) && scheduledEnd > x.StartsAt) && !AppointmentConfigurationStore.HasBreak(db, tenant.OrganizationId, request.BranchId, worker.Id, request.StartsAt, request.DurationMinutes);
            var skills = string.IsNullOrWhiteSpace(request.RequiredSkills) || request.RequiredSkills.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).All(skill => worker.Specialization.Contains(skill, StringComparison.OrdinalIgnoreCase));
            var continuity = visits.Any(x => x.ServiceUserId == request.ServiceUserId);
            var preferred = !string.IsNullOrWhiteSpace(client.PreferredCareWorker) && client.PreferredCareWorker.Trim().Equals(worker.FullName, StringComparison.OrdinalIgnoreCase);
            var dailyCount = visits.Count(x => AppointmentWorkflow.UkDate(x.StartsAt) == AppointmentWorkflow.UkDate(request.StartsAt));
            var score = (available ? weights.Availability : 0) + (skills ? weights.Skills : 0) + (preferred ? weights.Continuity : continuity ? weights.Continuity / 2 : 0) + (dailyCount == 0 ? weights.Workload : Math.Max(0, weights.Workload - dailyCount * 2));
            return new { workerId = worker.Id, workerName = worker.FullName, score, available, skillsMatched = skills, continuity, preferred, travelReady, absent, visitsThatDay = dailyCount };
        }).OrderByDescending(x => x.available).ThenByDescending(x => x.score).ToList();
        return Ok(results);
    }
}

public sealed record PresetRow(Guid Id, string Name, string VisitType, int DurationMinutes, string RequiredSkills, bool Active);
public sealed record RunRow(Guid Id, string Name, bool Active);
public sealed record BreakRow(Guid Id, Guid CareWorkerId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Note);
