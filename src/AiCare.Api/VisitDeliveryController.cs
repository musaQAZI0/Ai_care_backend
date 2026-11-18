using System.Data;
using System.Data.Common;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AiCare.Api;

[ApiController]
[Authorize(Policy = "Phase1User")]
[Route("api/phase1/visits/{visitId:guid}/delivery")]
public sealed class VisitDeliveryController : ControllerBase
{
    private readonly CareDbContext _context;
    private readonly ITenantContext _tenant;
    private readonly ICurrentUserContext _currentUser;
    private readonly IContextualAuthorization _authorization;

    public VisitDeliveryController(CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, IContextualAuthorization authorization)
    {
        _context = context;
        _tenant = tenant;
        _currentUser = currentUser;
        _authorization = authorization;
    }

    [HttpPost("location")]
    [TransactionalAction]
    public async Task<IActionResult> RecordLocation(Guid visitId, RecordVisitLocationRequest request, CancellationToken cancellationToken)
    {
        var visit = await FindVisit(visitId, cancellationToken);
        if (visit is null) return NotFound();
        if (!await _authorization.CanOperateVisitAsync(visit.Id, cancellationToken)) return Forbid();
        if (!ValidLocation(request, DateTimeOffset.UtcNow))
            return BadRequest(new { message = "A current location with valid coordinates and reported accuracy is required." });
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

    // Attendance and evidence share one transaction. A lost HTTP response can
    // safely be reconciled without replacing the original attendance timestamp.
    [HttpPost("attendance")]
    [TransactionalAction]
    public async Task<IActionResult> RecordAttendance(Guid visitId, RecordVisitLocationRequest request, CancellationToken cancellationToken)
    {
        if (request.EventType is not ("CheckIn" or "CheckOut") || !ValidLocation(request, DateTimeOffset.UtcNow))
            return BadRequest(new { message = "CheckIn or CheckOut and a current accurate device location are required." });
        var visit = await FindVisit(visitId, cancellationToken);
        if (visit is null) return NotFound();
        if (!await _authorization.CanOperateVisitAsync(visitId, cancellationToken)) return Forbid();
        // Serialize competing submissions, including separate browser tabs.
        await Execute("select 1 from \"Visits\" where \"Id\"=@visit for update", c => Add(c, "visit", visitId), cancellationToken);
        await _context.Entry(visit).ReloadAsync(cancellationToken);
        if (!_tenant.CanAccess(visit.OrganizationId, visit.BranchId) ||
            !await _authorization.CanOperateVisitAsync(visitId, cancellationToken)) return NotFound();
        var property = await _context.Database.SqlQueryRaw<PropertyLocation>("""
            select latitude as "Latitude", longitude as "Longitude" from subject_geocoded_locations
            where organization_id={0} and branch_id={1} and subject_type='ServiceUser'
              and subject_id={2} and verified_at is not null
            """, visit.OrganizationId!, visit.BranchId!, visit.ServiceUserId).SingleOrDefaultAsync(cancellationToken);
        double? distanceMeters = property is null ? null : DistanceMeters(request.Latitude, request.Longitude, property.Latitude, property.Longitude);
        var checkIn = request.EventType == "CheckIn";
        var recorded = checkIn ? visit.CheckedInAt : visit.CheckedOutAt;
        if (recorded is not null)
            return Ok(new { visit, replayed = true, distanceMeters });
        if (checkIn ? visit.Status != VisitStatus.Scheduled : visit.Status != VisitStatus.InProgress || visit.CheckedInAt is null)
            return Conflict(new { message = "The visit state does not allow this attendance action. Refresh the visit before continuing." });
        var now = DateTimeOffset.UtcNow;
        var updated = checkIn
            ? visit with { Status = VisitStatus.InProgress, CheckedInAt = now, CheckInLatitude = request.Latitude, CheckInLongitude = request.Longitude }
            : visit with { Status = VisitStatus.Completed, CheckedOutAt = now, CheckOutLatitude = request.Latitude, CheckOutLongitude = request.Longitude };
        _context.Entry(visit).CurrentValues.SetValues(updated);
        _context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), checkIn ? "visit.checked_in" : "visit.checked_out", _currentUser.UserName, nameof(Visit), visitId, now, visit.OrganizationId, visit.BranchId));
        var evidence = await RecordLocation(visitId, request, cancellationToken);
        return evidence is CreatedResult ? Ok(new { visit = updated, replayed = false, distanceMeters }) : evidence;
    }

    public static bool ValidLocation(RecordVisitLocationRequest request, DateTimeOffset now) =>
        request.Latitude is >= -90 and <= 90 && request.Longitude is >= -180 and <= 180 &&
        request.AccuracyMeters is >= 0 and <= 10000 &&
        request.CapturedAt >= now.AddMinutes(-5) && request.CapturedAt <= now.AddMinutes(1);

    public static double DistanceMeters(decimal latitude, decimal longitude, decimal propertyLatitude, decimal propertyLongitude)
    {
        const double radians = Math.PI / 180;
        var lat = (double)latitude * radians;
        var targetLat = (double)propertyLatitude * radians;
        var deltaLat = targetLat - lat;
        var deltaLng = (double)(propertyLongitude - longitude) * radians;
        var a = Math.Pow(Math.Sin(deltaLat / 2), 2) + Math.Cos(lat) * Math.Cos(targetLat) * Math.Pow(Math.Sin(deltaLng / 2), 2);
        return 6371000 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1)));
    }

    private sealed record PropertyLocation(decimal Latitude, decimal Longitude);

    [HttpGet("location")]
    public async Task<IActionResult> GetLocations(Guid visitId, CancellationToken cancellationToken)
    {
        var visit = await FindVisit(visitId, cancellationToken);
        if (visit is null) return NotFound();
        if (!await _authorization.CanOperateVisitAsync(visit.Id, cancellationToken)) return Forbid();
        return Ok(await QueryLocations(visitId, cancellationToken));
    }

    [HttpGet("observations")]
    public async Task<IActionResult> GetObservations(Guid visitId, CancellationToken cancellationToken)
    {
        var visit = await FindVisit(visitId, cancellationToken);
        if (visit is null) return NotFound();
        if (!await _authorization.CanOperateVisitAsync(visit.Id, cancellationToken)) return Forbid();
        var observations = await _context.HealthObservations
            .Where(item => item.VisitId == visitId && item.OrganizationId == visit.OrganizationId)
            .OrderByDescending(item => item.RecordedAt)
            .ToListAsync(cancellationToken);
        return Ok(observations);
    }

    [HttpPost("observations")]
    [TransactionalAction]
    public async Task<IActionResult> RecordObservation(Guid visitId, CreateStructuredObservationRequest request, CancellationToken cancellationToken)
    {
        var visit = await FindVisit(visitId, cancellationToken);
        if (visit is null) return NotFound();
        if (!await _authorization.CanOperateVisitAsync(visit.Id, cancellationToken)) return Forbid();
        if (string.IsNullOrWhiteSpace(request.ObservationType) || string.IsNullOrWhiteSpace(request.Value))
            return BadRequest(new { message = "Observation type and value are required." });

        var allowedTypes = new[] { "Blood pressure", "Pulse", "Temperature", "Oxygen saturation", "Weight", "Blood glucose", "Fluid intake", "Food intake", "Bowel", "Urine", "Pain", "Mood", "Skin", "Sleep", "Other" };
        if (!allowedTypes.Contains(request.ObservationType, StringComparer.OrdinalIgnoreCase))
            return BadRequest(new { message = "Unsupported observation type." });

        var observation = new HealthObservation(
            Guid.NewGuid(), visit.Id, visit.ServiceUserId, request.ObservationType.Trim(), request.Value.Trim(), request.Unit?.Trim() ?? "",
            request.Notes?.Trim() ?? "", request.RecordedAt ?? DateTimeOffset.UtcNow, visit.OrganizationId, visit.BranchId);
        _context.HealthObservations.Add(observation);
        _context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "visit.observation_recorded", _currentUser.UserName, nameof(HealthObservation), observation.Id, DateTimeOffset.UtcNow, visit.OrganizationId, visit.BranchId));
        await _context.SaveChangesAsync(cancellationToken);
        await EvaluateThreshold(observation, visit, cancellationToken);
        return Created($"/api/phase1/visits/{visitId}/delivery/observations/{observation.Id}", observation);
    }

    private async Task EvaluateThreshold(HealthObservation observation, Visit visit, CancellationToken cancellationToken)
    {
        if (!decimal.TryParse(observation.Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value)) return;
        var connection=_context.Database.GetDbConnection();var opened=connection.State!=ConnectionState.Open;if(opened)await connection.OpenAsync(cancellationToken);
        try{await using var command=connection.CreateCommand();command.Transaction=_context.Database.CurrentTransaction?.GetDbTransaction();command.CommandText="select minimum_value,maximum_value,severity,instructions from observation_thresholds where organization_id=@organization and branch_id=@branch and lower(observation_type)=lower(@type) and active=true";Add(command,"organization",visit.OrganizationId);Add(command,"branch",visit.BranchId);Add(command,"type",observation.ObservationType);await using var reader=await command.ExecuteReaderAsync(cancellationToken);if(!await reader.ReadAsync(cancellationToken))return;decimal? min=reader.IsDBNull(0)?null:reader.GetDecimal(0);decimal? max=reader.IsDBNull(1)?null:reader.GetDecimal(1);var severity=reader.GetString(2);var instructions=reader.GetString(3);if((min is null||value>=min)&&(max is null||value<=max))return;await reader.DisposeAsync();await using var insert=connection.CreateCommand();insert.Transaction=_context.Database.CurrentTransaction?.GetDbTransaction();insert.CommandText="insert into deterioration_alerts(id,observation_id,visit_id,service_user_id,organization_id,branch_id,alert_type,severity,detail,immediate_action,status) values(@id,@observation,@visit,@person,@organization,@branch,@type,@severity,@detail,@instructions,'Open')";Add(insert,"id",Guid.NewGuid());Add(insert,"observation",observation.Id);Add(insert,"visit",visit.Id);Add(insert,"person",visit.ServiceUserId);Add(insert,"organization",visit.OrganizationId);Add(insert,"branch",visit.BranchId);Add(insert,"type",observation.ObservationType);Add(insert,"severity",severity);Add(insert,"detail",$"{observation.ObservationType} value {observation.Value} {observation.Unit} is outside the configured range.");Add(insert,"instructions",instructions);await insert.ExecuteNonQueryAsync(cancellationToken);}
        finally{if(opened)await connection.CloseAsync();}
    }

    private async Task<Visit?> FindVisit(Guid id, CancellationToken cancellationToken)
    {
        var visit = await _context.Visits.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return visit is not null && _tenant.CanAccess(visit.OrganizationId, visit.BranchId) ? visit : null;
    }

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
        try { await using var command=connection.CreateCommand(); command.Transaction=_context.Database.CurrentTransaction?.GetDbTransaction(); command.CommandText=sql; bind(command); await command.ExecuteNonQueryAsync(cancellationToken); }
        finally { if(opened) await connection.CloseAsync(); }
    }

    private static void Add(DbCommand command,string name,object? value) { var parameter=command.CreateParameter(); parameter.ParameterName=name; parameter.Value=value??DBNull.Value; command.Parameters.Add(parameter); }
}

public sealed record RecordVisitLocationRequest(string EventType,decimal Latitude,decimal Longitude,decimal? AccuracyMeters,DateTimeOffset CapturedAt,string? Source,string? Notes);
public sealed record VisitLocationResponse(Guid Id,string EventType,decimal Latitude,decimal Longitude,decimal? AccuracyMeters,DateTimeOffset CapturedAt,string Source,string Notes);
public sealed record CreateStructuredObservationRequest(string ObservationType,string Value,string? Unit,string? Notes,DateTimeOffset? RecordedAt);
