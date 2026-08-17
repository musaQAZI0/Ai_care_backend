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
[Route("api/phase1/medication-safety")]
public sealed class MedicationSafetyController : ControllerBase
{
    private readonly CareDbContext _context;
    private readonly ITenantContext _tenant;
    private readonly ICurrentUserContext _currentUser;

    public MedicationSafetyController(CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser)
    {
        _context = context;
        _tenant = tenant;
        _currentUser = currentUser;
    }

    [HttpGet("medications/{medicationId:guid}/profile")]
    public async Task<IActionResult> GetProfile(Guid medicationId, CancellationToken cancellationToken)
    {
        var medication = await _context.Medications.SingleOrDefaultAsync(x => x.Id == medicationId, cancellationToken);
        if (medication is null || !_tenant.CanAccess(medication.OrganizationId, medication.BranchId)) return NotFound();
        return Ok(await QueryProfile(medicationId, cancellationToken));
    }

    [HttpPut("medications/{medicationId:guid}/profile")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public async Task<IActionResult> UpsertProfile(Guid medicationId, MedicationSafetyProfileRequest request, CancellationToken cancellationToken)
    {
        var medication = await _context.Medications.SingleOrDefaultAsync(x => x.Id == medicationId, cancellationToken);
        if (medication is null || !_tenant.CanAccess(medication.OrganizationId, medication.BranchId)) return NotFound();
        if (request.DoseWindowMinutes is < 0 or > 1440) return BadRequest(new { message = "Dose window must be between 0 and 1440 minutes." });
        if (medication.IsPrn && string.IsNullOrWhiteSpace(request.PrnIndication)) return BadRequest(new { message = "PRN indication is required for PRN medication." });
        if (request.MaxPrnDoses24h is < 1 || request.MinPrnIntervalMinutes is < 0) return BadRequest(new { message = "PRN limits are invalid." });

        await Execute("""
            insert into medication_safety_profiles
              (medication_id,organization_id,branch_id,indication,prescriber,form,strength,start_date,end_date,dose_window_minutes,max_prn_doses_24h,min_prn_interval_minutes,prn_indication,prn_effect_review_minutes,stock_on_hand,reorder_level,requires_witness,last_reconciled_at,reconciled_by,updated_at)
            values (@medication,@organization,@branch,@indication,@prescriber,@form,@strength,@start,@end,@window,@maxprn,@interval,@prnindication,@review,@stock,@reorder,@witness,@reconciled,@reconciledby,now())
            on conflict (medication_id) do update set
              indication=excluded.indication,prescriber=excluded.prescriber,form=excluded.form,strength=excluded.strength,start_date=excluded.start_date,end_date=excluded.end_date,
              dose_window_minutes=excluded.dose_window_minutes,max_prn_doses_24h=excluded.max_prn_doses_24h,min_prn_interval_minutes=excluded.min_prn_interval_minutes,
              prn_indication=excluded.prn_indication,prn_effect_review_minutes=excluded.prn_effect_review_minutes,stock_on_hand=excluded.stock_on_hand,reorder_level=excluded.reorder_level,
              requires_witness=excluded.requires_witness,last_reconciled_at=excluded.last_reconciled_at,reconciled_by=excluded.reconciled_by,updated_at=now()
            """, command =>
        {
            Add(command,"medication",medicationId); Add(command,"organization",medication.OrganizationId ?? _tenant.OrganizationId); Add(command,"branch",medication.BranchId ?? _tenant.BranchId ?? TenantDefaults.BranchId);
            Add(command,"indication",request.Indication.Trim()); Add(command,"prescriber",request.Prescriber.Trim()); Add(command,"form",request.Form.Trim()); Add(command,"strength",request.Strength.Trim());
            Add(command,"start",request.StartDate?.UtcDateTime); Add(command,"end",request.EndDate?.UtcDateTime); Add(command,"window",request.DoseWindowMinutes); Add(command,"maxprn",request.MaxPrnDoses24h);
            Add(command,"interval",request.MinPrnIntervalMinutes); Add(command,"prnindication",request.PrnIndication.Trim()); Add(command,"review",request.PrnEffectReviewMinutes); Add(command,"stock",request.StockOnHand);
            Add(command,"reorder",request.ReorderLevel); Add(command,"witness",request.RequiresWitness); Add(command,"reconciled",request.LastReconciledAt?.UtcDateTime); Add(command,"reconciledby",request.ReconciledBy.Trim());
        }, cancellationToken);

        _context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "medication.safety_profile_updated", _currentUser.UserName, nameof(Medication), medicationId, DateTimeOffset.UtcNow, medication.OrganizationId, medication.BranchId));
        await _context.SaveChangesAsync(cancellationToken);
        return Ok(await QueryProfile(medicationId, cancellationToken));
    }

    [HttpGet("mar/{marRecordId:guid}/events")]
    public async Task<IActionResult> GetMarSafetyEvents(Guid marRecordId, CancellationToken cancellationToken)
    {
        var mar = await _context.MedicationAdministrationRecords.SingleOrDefaultAsync(x => x.Id == marRecordId, cancellationToken);
        if (mar is null || !_tenant.CanAccess(mar.OrganizationId, mar.BranchId)) return NotFound();
        if (!CanAccessMar(mar)) return Forbid();
        return Ok(await QueryEvents(marRecordId, cancellationToken));
    }

    [HttpPost("mar/{marRecordId:guid}/events")]
    public async Task<IActionResult> RecordMarSafetyEvent(Guid marRecordId, MarSafetyEventRequest request, CancellationToken cancellationToken)
    {
        var mar = await _context.MedicationAdministrationRecords.SingleOrDefaultAsync(x => x.Id == marRecordId, cancellationToken);
        if (mar is null || !_tenant.CanAccess(mar.OrganizationId, mar.BranchId)) return NotFound();
        if (!CanAccessMar(mar)) return Forbid();
        var allowed = new[] { "OmissionReason", "PRNEffect", "Correction", "StockAdjustment", "Waste", "Disposal", "Witness" };
        if (!allowed.Contains(request.EventType, StringComparer.OrdinalIgnoreCase)) return BadRequest(new { message = "Unsupported medication safety event." });
        if (request.EventType.Equals("OmissionReason", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(request.Reason)) return BadRequest(new { message = "Omission reason is required." });

        var id = Guid.NewGuid();
        await Execute("""
            insert into mar_safety_events(id,mar_record_id,organization_id,branch_id,event_type,reason,effect,witnessed_by,stock_delta,created_by,created_at)
            values(@id,@mar,@organization,@branch,@type,@reason,@effect,@witness,@stock,@createdby,now())
            """, command =>
        {
            Add(command,"id",id); Add(command,"mar",marRecordId); Add(command,"organization",mar.OrganizationId ?? _tenant.OrganizationId); Add(command,"branch",mar.BranchId ?? _tenant.BranchId ?? TenantDefaults.BranchId);
            Add(command,"type",request.EventType.Trim()); Add(command,"reason",request.Reason?.Trim() ?? ""); Add(command,"effect",request.Effect?.Trim() ?? ""); Add(command,"witness",request.WitnessedBy?.Trim() ?? "");
            Add(command,"stock",request.StockDelta); Add(command,"createdby",_currentUser.UserName);
        }, cancellationToken);
        _context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "emar.safety_event_recorded", _currentUser.UserName, nameof(MedicationAdministrationRecord), marRecordId, DateTimeOffset.UtcNow, mar.OrganizationId, mar.BranchId));
        await _context.SaveChangesAsync(cancellationToken);
        return Created($"/api/phase1/medication-safety/mar/{marRecordId}/events/{id}", new { id });
    }

    private bool CanAccessMar(MedicationAdministrationRecord mar) => _currentUser.IsAdministrator || _currentUser.IsCareManager || _currentUser.IsCareCoordinator || (_currentUser.IsCareWorker && _currentUser.CareWorkerId == mar.CareWorkerId);

    private async Task<MedicationSafetyProfileResponse?> QueryProfile(Guid medicationId, CancellationToken cancellationToken)
    {
        var connection = _context.Database.GetDbConnection(); var opened = connection.State != ConnectionState.Open; if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "select medication_id,indication,prescriber,form,strength,start_date,end_date,dose_window_minutes,max_prn_doses_24h,min_prn_interval_minutes,prn_indication,prn_effect_review_minutes,stock_on_hand,reorder_level,requires_witness,last_reconciled_at,reconciled_by,updated_at from medication_safety_profiles where medication_id=@id and organization_id=@organization";
            Add(command,"id",medicationId); Add(command,"organization",_tenant.OrganizationId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken); if (!await reader.ReadAsync(cancellationToken)) return null;
            return new MedicationSafetyProfileResponse(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),ReadDate(reader,5),ReadDate(reader,6),reader.GetInt32(7),ReadInt(reader,8),ReadInt(reader,9),reader.GetString(10),ReadInt(reader,11),ReadDecimal(reader,12),ReadDecimal(reader,13),reader.GetBoolean(14),ReadDate(reader,15),reader.GetString(16),reader.GetDateTime(17));
        }
        finally { if (opened) await connection.CloseAsync(); }
    }

    private async Task<List<MarSafetyEventResponse>> QueryEvents(Guid marId, CancellationToken cancellationToken)
    {
        var result = new List<MarSafetyEventResponse>(); var connection = _context.Database.GetDbConnection(); var opened = connection.State != ConnectionState.Open; if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand(); command.CommandText = "select id,event_type,reason,effect,witnessed_by,stock_delta,created_by,created_at from mar_safety_events where mar_record_id=@id and organization_id=@organization order by created_at desc"; Add(command,"id",marId); Add(command,"organization",_tenant.OrganizationId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken); while (await reader.ReadAsync(cancellationToken)) result.Add(new MarSafetyEventResponse(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),ReadDecimal(reader,5),reader.GetString(6),reader.GetDateTime(7)));
            return result;
        }
        finally { if (opened) await connection.CloseAsync(); }
    }

    private async Task Execute(string sql, Action<DbCommand> bind, CancellationToken cancellationToken)
    {
        var connection = _context.Database.GetDbConnection(); var opened = connection.State != ConnectionState.Open; if (opened) await connection.OpenAsync(cancellationToken);
        try { await using var command = connection.CreateCommand(); command.CommandText = sql; bind(command); await command.ExecuteNonQueryAsync(cancellationToken); }
        finally { if (opened) await connection.CloseAsync(); }
    }

    private static DateTimeOffset? ReadDate(DbDataReader reader,int ordinal) => reader.IsDBNull(ordinal) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
    private static int? ReadInt(DbDataReader reader,int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    private static decimal? ReadDecimal(DbDataReader reader,int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
    private static void Add(DbCommand command,string name,object? value) { var p=command.CreateParameter(); p.ParameterName=name; p.Value=value??DBNull.Value; command.Parameters.Add(p); }
}

public sealed record MedicationSafetyProfileRequest(string Indication,string Prescriber,string Form,string Strength,DateTimeOffset? StartDate,DateTimeOffset? EndDate,int DoseWindowMinutes,int? MaxPrnDoses24h,int? MinPrnIntervalMinutes,string PrnIndication,int? PrnEffectReviewMinutes,decimal? StockOnHand,decimal? ReorderLevel,bool RequiresWitness,DateTimeOffset? LastReconciledAt,string ReconciledBy);
public sealed record MedicationSafetyProfileResponse(Guid MedicationId,string Indication,string Prescriber,string Form,string Strength,DateTimeOffset? StartDate,DateTimeOffset? EndDate,int DoseWindowMinutes,int? MaxPrnDoses24h,int? MinPrnIntervalMinutes,string PrnIndication,int? PrnEffectReviewMinutes,decimal? StockOnHand,decimal? ReorderLevel,bool RequiresWitness,DateTimeOffset? LastReconciledAt,string ReconciledBy,DateTimeOffset UpdatedAt);
public sealed record MarSafetyEventRequest(string EventType,string? Reason,string? Effect,string? WitnessedBy,decimal? StockDelta);
public sealed record MarSafetyEventResponse(Guid Id,string EventType,string Reason,string Effect,string WitnessedBy,decimal? StockDelta,string CreatedBy,DateTimeOffset CreatedAt);
