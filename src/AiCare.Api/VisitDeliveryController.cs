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
[Route("api/phase1/visits/{visitId:guid}/delivery")]
public sealed class VisitDeliveryController : ControllerBase
{
    private readonly CareDbContext _context;
    private readonly ITenantContext _tenant;
    private readonly ICurrentUserContext _currentUser;

    public VisitDeliveryController(CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser)
    {
        _context = context;
        _tenant = tenant;
        _currentUser = currentUser;
    }

    [HttpPost("location")]
    public async Task<IActionResult> RecordLocation(Guid visitId, RecordVisitLocationRequest request, CancellationToken cancellationToken)
    {
        var visit = await FindVisit(visitId, cancellationToken);
        if (visit is null) return NotFound();
        if (!CanDeliver(visit)) return Forbid();
        if (request.Latitude is < -90 or > 90 || request.Longitude is < -180 or > 180)
            return BadRequest(new { message = "Coordinates are invalid." });
        if (request.AccuracyMeters is < 0) return BadRequest(new { message = "Location accuracy cannot be negative." });
        if (request.EventType is not ("CheckIn" or "CheckOut" or "Evidence"))
            return BadRequest(new { message = "Event type must be CheckIn, CheckOut, or Evidence." });

        var id = Guid.NewGuid();
        await Execute("""
            insert into visit_location_events
              (id,visit_id,service_user_id,care_worker_id,organization_id,branch_id,event_type,latitude,longitude,accuracy_meters,captured_at,source,notes,created_at)
            values (@id,@visit,@person,@worker,@organization,@branch,@type,@lat,@lng,@accuracy,@captured,@source,@notes,now())
            """, command =>
        {
            Add(command,"id",id); Add(command,"visit",visit.Id); Add(command,"person",visit.ServiceUserId); Add(command,"worker",visit.CareWorkerId);
            Add(command,"organization",visit.OrganizationId ?? _tenant.OrganizationId); Add(command,"branch",visit.BranchId ?? _tenant.BranchId ?? TenantDefaults.BranchId);
            Add(command,"type",request.EventType); Add(command,"lat",request.Latitude); Add(command,"lng",request.Longitude); Add(command,"accuracy",request.AccuracyMeters);
            Add(command,"captured",request.CapturedAt.UtcDateTime); Add(command,"source",string.IsNullOrWhiteSpace(request.Source)?"Browser geolocation":request.Source.Trim());
            Add(command,"notes",request.Notes?.Trim() ?? "");
        }, cancellationToken);

        _context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), $"visit.location_{request.EventType.ToLowerInvariant()}", _currentUser.UserName, "VisitLocationEvent", id, DateTimeOffset.UtcNow, visit.OrganizationId, visit.BranchId));
        await _context.SaveChangesAsync(cancellationToken);
        return Created($"/api/phase1/visits/{visitId}/delivery/location/{id}", new { id });
    }

    [HttpGet("location")]
    public async Task<IActionResult> GetLocations(Guid visitId, CancellationToken cancellationToken)
    {
        var visit = await FindVisit(visitId, cancellationToken);
        if (visit is null) return NotFound();
        if (!CanView(visit)) return Forbid();
        return Ok(await QueryLocations(visitId, cancellationToken));
    }

    [HttpGet("observations")]
    public async Task<IActionResult> GetObservations(Guid visitId, CancellationToken cancellationToken)
    {
        var visit = await FindVisit(visitId, cancellationToken);
        if (visit is null) return NotFound();
        if (!CanView(visit)) return Forbid();
        var observations = await _context.HealthObservations
            .Where(item => item.VisitId == visitId && item.OrganizationId == visit.OrganizationId)
            .OrderByDescending(item => item.RecordedAt)
            .ToListAsync(cancellationToken);
        return Ok(observations);
    }

    [HttpPost("observations")]
    public async Task<IActionResult> RecordObservation(Guid visitId, CreateStructuredObservationRequest request, CancellationToken cancellationToken)
    {
        var visit = await FindVisit(visitId, cancellationToken);
        if (visit is null) return NotFound();
        if (!CanDeliver(visit)) return Forbid();
        if (string.IsNullOrWhiteSpace(request.ObservationType) || string.IsNullOrWhiteSpace(request.Value))
            return BadRequest(new { message = "Observation type and value are required." });

        var allowedTypes = new[] { "Blood pressure", "Pulse", "Temperature", "Weight", "Blood glucose", "Fluid intake", "Food intake", "Bowel", "Urine", "Pain", "Mood", "Skin", "Sleep", "Other" };
        if (!allowedTypes.Contains(request.ObservationType, StringComparer.OrdinalIgnoreCase))
            return BadRequest(new { message = "Unsupported observation type." });

        var observation = new HealthObservation(
            Guid.NewGuid(), visit.Id, visit.ServiceUserId, request.ObservationType.Trim(), request.Value.Trim(), request.Unit?.Trim() ?? "",
            request.Notes?.Trim() ?? "", request.RecordedAt ?? DateTimeOffset.UtcNow, visit.OrganizationId, visit.BranchId);
        _context.HealthObservations.Add(observation);
        _context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "visit.observation_recorded", _currentUser.UserName, nameof(HealthObservation), observation.Id, DateTimeOffset.UtcNow, visit.OrganizationId, visit.BranchId));
        await _context.SaveChangesAsync(cancellationToken);
        return Created($"/api/phase1/visits/{visitId}/delivery/observations/{observation.Id}", observation);
    }

    private async Task<Visit?> FindVisit(Guid id, CancellationToken cancellationToken)
    {
        var visit = await _context.Visits.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return visit is not null && _tenant.CanAccess(visit.OrganizationId, visit.BranchId) ? visit : null;
    }

    private bool CanDeliver(Visit visit) => _currentUser.IsAdministrator || _currentUser.IsCareManager || _currentUser.IsCareCoordinator || (_currentUser.IsCareWorker && _currentUser.CareWorkerId == visit.CareWorkerId);
    private bool CanView(Visit visit) => CanDeliver(visit);

    private async Task<List<VisitLocationResponse>> QueryLocations(Guid visitId, CancellationToken cancellationToken)
    {
        var result = new List<VisitLocationResponse>();
        var connection = _context.Database.GetDbConnection(); var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "select id,event_type,latitude,longitude,accuracy_meters,captured_at,source,notes from visit_location_events where visit_id=@visit and organization_id=@organization order by captured_at desc";
            Add(command,"visit",visitId); Add(command,"organization",_tenant.OrganizationId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) result.Add(new VisitLocationResponse(reader.GetGuid(0),reader.GetString(1),reader.GetDecimal(2),reader.GetDecimal(3),reader.IsDBNull(4)?null:reader.GetDecimal(4),reader.GetDateTime(5),reader.GetString(6),reader.GetString(7)));
            return result;
        }
        finally { if (opened) await connection.CloseAsync(); }
    }

    private async Task Execute(string sql, Action<DbCommand> bind, CancellationToken cancellationToken)
    {
        var connection=_context.Database.GetDbConnection(); var opened=connection.State!=ConnectionState.Open; if(opened) await connection.OpenAsync(cancellationToken);
        try { await using var command=connection.CreateCommand(); command.CommandText=sql; bind(command); await command.ExecuteNonQueryAsync(cancellationToken); }
        finally { if(opened) await connection.CloseAsync(); }
    }

    private static void Add(DbCommand command,string name,object? value) { var parameter=command.CreateParameter(); parameter.ParameterName=name; parameter.Value=value??DBNull.Value; command.Parameters.Add(parameter); }
}

public sealed record RecordVisitLocationRequest(string EventType,decimal Latitude,decimal Longitude,decimal? AccuracyMeters,DateTimeOffset CapturedAt,string? Source,string? Notes);
public sealed record VisitLocationResponse(Guid Id,string EventType,decimal Latitude,decimal Longitude,decimal? AccuracyMeters,DateTimeOffset CapturedAt,string Source,string Notes);
public sealed record CreateStructuredObservationRequest(string ObservationType,string Value,string? Unit,string? Notes,DateTimeOffset? RecordedAt);
