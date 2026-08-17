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
[Route("api/phase1/care-workers/{workerId:guid}/compliance")]
public sealed class WorkforceComplianceController : ControllerBase
{
    private readonly CareDbContext _context;
    private readonly ITenantContext _tenant;
    private readonly ICurrentUserContext _currentUser;

    public WorkforceComplianceController(CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser)
    {
        _context = context;
        _tenant = tenant;
        _currentUser = currentUser;
    }

    [HttpGet("summary")]
    public async Task<IActionResult> Summary(Guid workerId, CancellationToken cancellationToken)
    {
        var worker = await FindWorker(workerId, cancellationToken);
        if (worker is null) return NotFound();

        try
        {
            var compliance = await QueryCompliance(workerId, cancellationToken);
            var training = await QueryTraining(workerId, cancellationToken);
            var competencies = await QueryCompetencies(workerId, cancellationToken);
            var availability = await QueryAvailability(workerId, cancellationToken);
            var now = DateTimeOffset.UtcNow;

            var activeCompliance = compliance.Where(item => item.Status == "Valid" && (item.ExpiresAt is null || item.ExpiresAt > now)).ToArray();
            var hasDbs = activeCompliance.Any(item => item.ComplianceType.Equals("DBS", StringComparison.OrdinalIgnoreCase));
            var hasRightToWork = activeCompliance.Any(item => item.ComplianceType.Equals("Right to Work", StringComparison.OrdinalIgnoreCase));
            var mandatoryTraining = training.Where(item => item.Category == "Mandatory" && item.Status == "Valid" && (item.ExpiresAt is null || item.ExpiresAt > now)).ToArray();
            var hasMandatoryTraining = mandatoryTraining.Length > 0 || worker.TrainingCompliance.Contains("compliant", StringComparison.OrdinalIgnoreCase);
            var hasAvailability = availability.Any(item => item.IsAvailable);
            var existingDbsFallback = worker.DbsStatus.Contains("valid", StringComparison.OrdinalIgnoreCase) || worker.DbsStatus.Contains("clear", StringComparison.OrdinalIgnoreCase);

            var checks = new[]
            {
                new WorkerReadinessCheck("dbs", "Valid DBS", hasDbs || existingDbsFallback, "A valid DBS record is required before unsupervised care assignment."),
                new WorkerReadinessCheck("right-to-work", "Right to work", hasRightToWork, "Current right-to-work evidence must be verified."),
                new WorkerReadinessCheck("mandatory-training", "Mandatory training", hasMandatoryTraining, "At least one current mandatory training record is required."),
                new WorkerReadinessCheck("availability", "Availability", hasAvailability || !string.IsNullOrWhiteSpace(worker.Availability), "The worker must have an availability pattern before rota assignment."),
            };

            return Ok(new WorkerComplianceSummary(
                worker.Id,
                checks.All(item => item.Complete),
                checks,
                compliance,
                training,
                competencies,
                availability,
                compliance.Count(item => item.ExpiresAt is not null && item.ExpiresAt <= now.AddDays(30) && item.ExpiresAt > now),
                training.Count(item => item.ExpiresAt is not null && item.ExpiresAt <= now.AddDays(30) && item.ExpiresAt > now),
                now));
        }
        catch (DbException)
        {
            var legacyReady = (worker.DbsStatus.Contains("valid", StringComparison.OrdinalIgnoreCase) || worker.DbsStatus.Contains("clear", StringComparison.OrdinalIgnoreCase))
                && !string.IsNullOrWhiteSpace(worker.TrainingCompliance)
                && !string.IsNullOrWhiteSpace(worker.Availability);
            return Ok(new WorkerComplianceSummary(
                worker.Id,
                legacyReady,
                new[] { new WorkerReadinessCheck("legacy", "Legacy worker readiness", legacyReady, "Apply the workforce compliance migration to use structured readiness checks.") },
                [], [], [], [], 0, 0, DateTimeOffset.UtcNow));
        }
    }

    [HttpPost("records")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public async Task<IActionResult> AddCompliance(Guid workerId, CreateWorkerComplianceRequest request, CancellationToken cancellationToken)
    {
        var worker = await FindWorker(workerId, cancellationToken);
        if (worker is null) return NotFound();
        if (string.IsNullOrWhiteSpace(request.ComplianceType)) return BadRequest(new { message = "Compliance type is required." });

        var id = Guid.NewGuid();
        await Execute("""
            insert into worker_compliance_records
              (id,care_worker_id,organization_id,branch_id,compliance_type,reference,status,issued_at,expires_at,verified_by,notes,created_at,updated_at)
            values (@id,@worker,@organization,@branch,@type,@reference,@status,@issued,@expires,@verifiedBy,@notes,now(),now())
            """, command =>
        {
            Add(command,"id",id); AddCommon(command, workerId);
            Add(command,"type",request.ComplianceType.Trim()); Add(command,"reference",request.Reference?.Trim() ?? "");
            Add(command,"status",string.IsNullOrWhiteSpace(request.Status) ? "Valid" : request.Status.Trim());
            Add(command,"issued",request.IssuedAt?.UtcDateTime); Add(command,"expires",request.ExpiresAt?.UtcDateTime);
            Add(command,"verifiedBy",request.VerifiedBy?.Trim() ?? _currentUser.UserName); Add(command,"notes",request.Notes?.Trim() ?? "");
        }, cancellationToken);
        Audit("worker_compliance.created", "WorkerComplianceRecord", id, worker);
        await _context.SaveChangesAsync(cancellationToken);
        return Created($"/api/phase1/care-workers/{workerId}/compliance/records/{id}", new { id });
    }

    [HttpPost("training")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public async Task<IActionResult> AddTraining(Guid workerId, CreateWorkerTrainingRequest request, CancellationToken cancellationToken)
    {
        var worker = await FindWorker(workerId, cancellationToken);
        if (worker is null) return NotFound();
        if (string.IsNullOrWhiteSpace(request.CourseName)) return BadRequest(new { message = "Course name is required." });
        var id = Guid.NewGuid();
        await Execute("""
            insert into worker_training_records
              (id,care_worker_id,organization_id,branch_id,course_name,category,provider,certificate_reference,completed_at,expires_at,status,created_at)
            values (@id,@worker,@organization,@branch,@course,@category,@provider,@certificate,@completed,@expires,@status,now())
            """, command =>
        {
            Add(command,"id",id); AddCommon(command,workerId); Add(command,"course",request.CourseName.Trim());
            Add(command,"category",string.IsNullOrWhiteSpace(request.Category) ? "Mandatory" : request.Category.Trim());
            Add(command,"provider",request.Provider?.Trim() ?? ""); Add(command,"certificate",request.CertificateReference?.Trim() ?? "");
            Add(command,"completed",request.CompletedAt.UtcDateTime); Add(command,"expires",request.ExpiresAt?.UtcDateTime);
            Add(command,"status",string.IsNullOrWhiteSpace(request.Status) ? "Valid" : request.Status.Trim());
        }, cancellationToken);
        Audit("worker_training.created", "WorkerTrainingRecord", id, worker);
        await _context.SaveChangesAsync(cancellationToken);
        return Created($"/api/phase1/care-workers/{workerId}/compliance/training/{id}", new { id });
    }

    [HttpPost("competencies")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public async Task<IActionResult> AddCompetency(Guid workerId, CreateWorkerCompetencyRequest request, CancellationToken cancellationToken)
    {
        var worker = await FindWorker(workerId, cancellationToken);
        if (worker is null) return NotFound();
        if (string.IsNullOrWhiteSpace(request.Competency)) return BadRequest(new { message = "Competency is required." });
        var id = Guid.NewGuid();
        await Execute("""
            insert into worker_competency_records
              (id,care_worker_id,organization_id,branch_id,competency,level,status,assessed_by,assessed_at,expires_at,notes,created_at)
            values (@id,@worker,@organization,@branch,@competency,@level,@status,@assessedBy,@assessedAt,@expires,@notes,now())
            """, command =>
        {
            Add(command,"id",id); AddCommon(command,workerId); Add(command,"competency",request.Competency.Trim());
            Add(command,"level",string.IsNullOrWhiteSpace(request.Level) ? "Competent" : request.Level.Trim());
            Add(command,"status",string.IsNullOrWhiteSpace(request.Status) ? "Valid" : request.Status.Trim());
            Add(command,"assessedBy",request.AssessedBy?.Trim() ?? _currentUser.UserName); Add(command,"assessedAt",request.AssessedAt.UtcDateTime);
            Add(command,"expires",request.ExpiresAt?.UtcDateTime); Add(command,"notes",request.Notes?.Trim() ?? "");
        }, cancellationToken);
        Audit("worker_competency.created", "WorkerCompetencyRecord", id, worker);
        await _context.SaveChangesAsync(cancellationToken);
        return Created($"/api/phase1/care-workers/{workerId}/compliance/competencies/{id}", new { id });
    }

    [HttpPost("availability")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public async Task<IActionResult> AddAvailability(Guid workerId, CreateAvailabilityRuleRequest request, CancellationToken cancellationToken)
    {
        var worker = await FindWorker(workerId, cancellationToken);
        if (worker is null) return NotFound();
        if (request.DayOfWeek is < 0 or > 6 || request.EndTime <= request.StartTime) return BadRequest(new { message = "Day/time range is invalid." });
        var id = Guid.NewGuid();
        await Execute("""
            insert into worker_availability_rules
              (id,care_worker_id,organization_id,branch_id,day_of_week,start_time,end_time,is_available,effective_from,effective_to,notes,created_at)
            values (@id,@worker,@organization,@branch,@day,@start,@end,@available,@from,@to,@notes,now())
            """, command =>
        {
            Add(command,"id",id); AddCommon(command,workerId); Add(command,"day",request.DayOfWeek); Add(command,"start",request.StartTime);
            Add(command,"end",request.EndTime); Add(command,"available",request.IsAvailable); Add(command,"from",request.EffectiveFrom);
            Add(command,"to",request.EffectiveTo); Add(command,"notes",request.Notes?.Trim() ?? "");
        }, cancellationToken);
        Audit("worker_availability.created", "WorkerAvailabilityRule", id, worker);
        await _context.SaveChangesAsync(cancellationToken);
        return Created($"/api/phase1/care-workers/{workerId}/compliance/availability/{id}", new { id });
    }

    private async Task<CareWorker?> FindWorker(Guid id, CancellationToken cancellationToken)
    {
        var worker = await _context.CareWorkers.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return worker is not null && _tenant.CanAccess(worker.OrganizationId, worker.BranchId) ? worker : null;
    }

    private async Task<List<WorkerComplianceRecordResponse>> QueryCompliance(Guid workerId, CancellationToken cancellationToken) =>
        await Query("select id,compliance_type,reference,status,issued_at,expires_at,verified_by,notes from worker_compliance_records where care_worker_id=@worker and organization_id=@organization order by compliance_type", workerId, reader => new WorkerComplianceRecordResponse(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.IsDBNull(4)?null:reader.GetDateTime(4),reader.IsDBNull(5)?null:reader.GetDateTime(5),reader.GetString(6),reader.GetString(7)), cancellationToken);

    private async Task<List<WorkerTrainingRecordResponse>> QueryTraining(Guid workerId, CancellationToken cancellationToken) =>
        await Query("select id,course_name,category,provider,certificate_reference,completed_at,expires_at,status from worker_training_records where care_worker_id=@worker and organization_id=@organization order by completed_at desc", workerId, reader => new WorkerTrainingRecordResponse(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetDateTime(5),reader.IsDBNull(6)?null:reader.GetDateTime(6),reader.GetString(7)), cancellationToken);

    private async Task<List<WorkerCompetencyRecordResponse>> QueryCompetencies(Guid workerId, CancellationToken cancellationToken) =>
        await Query("select id,competency,level,status,assessed_by,assessed_at,expires_at,notes from worker_competency_records where care_worker_id=@worker and organization_id=@organization order by competency", workerId, reader => new WorkerCompetencyRecordResponse(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetDateTime(5),reader.IsDBNull(6)?null:reader.GetDateTime(6),reader.GetString(7)), cancellationToken);

    private async Task<List<WorkerAvailabilityRuleResponse>> QueryAvailability(Guid workerId, CancellationToken cancellationToken) =>
        await Query("select id,day_of_week,start_time,end_time,is_available,effective_from,effective_to,notes from worker_availability_rules where care_worker_id=@worker and organization_id=@organization order by day_of_week,start_time", workerId, reader => new WorkerAvailabilityRuleResponse(reader.GetGuid(0),reader.GetInt32(1),reader.GetFieldValue<TimeOnly>(2),reader.GetFieldValue<TimeOnly>(3),reader.GetBoolean(4),reader.GetFieldValue<DateOnly>(5),reader.IsDBNull(6)?null:reader.GetFieldValue<DateOnly>(6),reader.GetString(7)), cancellationToken);

    private async Task<List<T>> Query<T>(string sql, Guid workerId, Func<DbDataReader,T> map, CancellationToken cancellationToken)
    {
        var result = new List<T>();
        var connection = _context.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand(); command.CommandText = sql; AddCommon(command,workerId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) result.Add(map(reader));
            return result;
        }
        finally { if (opened) await connection.CloseAsync(); }
    }

    private async Task Execute(string sql, Action<DbCommand> bind, CancellationToken cancellationToken)
    {
        var connection = _context.Database.GetDbConnection(); var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try { await using var command = connection.CreateCommand(); command.CommandText = sql; bind(command); await command.ExecuteNonQueryAsync(cancellationToken); }
        finally { if (opened) await connection.CloseAsync(); }
    }

    private void AddCommon(DbCommand command, Guid workerId)
    {
        Add(command,"worker",workerId); Add(command,"organization",_tenant.OrganizationId); Add(command,"branch",_tenant.BranchId ?? TenantDefaults.BranchId);
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; command.Parameters.Add(parameter);
    }

    private void Audit(string action, string entityType, Guid id, CareWorker worker) =>
        _context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), action, _currentUser.UserName, entityType, id, DateTimeOffset.UtcNow, worker.OrganizationId, worker.BranchId));
}

public sealed record WorkerReadinessCheck(string Key,string Label,bool Complete,string Requirement);
public sealed record WorkerComplianceRecordResponse(Guid Id,string ComplianceType,string Reference,string Status,DateTimeOffset? IssuedAt,DateTimeOffset? ExpiresAt,string VerifiedBy,string Notes);
public sealed record WorkerTrainingRecordResponse(Guid Id,string CourseName,string Category,string Provider,string CertificateReference,DateTimeOffset CompletedAt,DateTimeOffset? ExpiresAt,string Status);
public sealed record WorkerCompetencyRecordResponse(Guid Id,string Competency,string Level,string Status,string AssessedBy,DateTimeOffset AssessedAt,DateTimeOffset? ExpiresAt,string Notes);
public sealed record WorkerAvailabilityRuleResponse(Guid Id,int DayOfWeek,TimeOnly StartTime,TimeOnly EndTime,bool IsAvailable,DateOnly EffectiveFrom,DateOnly? EffectiveTo,string Notes);
public sealed record WorkerComplianceSummary(Guid CareWorkerId,bool ReadyForAssignment,IReadOnlyCollection<WorkerReadinessCheck> Checks,IReadOnlyCollection<WorkerComplianceRecordResponse> Compliance,IReadOnlyCollection<WorkerTrainingRecordResponse> Training,IReadOnlyCollection<WorkerCompetencyRecordResponse> Competencies,IReadOnlyCollection<WorkerAvailabilityRuleResponse> Availability,int ComplianceExpiringWithin30Days,int TrainingExpiringWithin30Days,DateTimeOffset EvaluatedAt);
public sealed record CreateWorkerComplianceRequest(string ComplianceType,string? Reference,string? Status,DateTimeOffset? IssuedAt,DateTimeOffset? ExpiresAt,string? VerifiedBy,string? Notes);
public sealed record CreateWorkerTrainingRequest(string CourseName,string? Category,string? Provider,string? CertificateReference,DateTimeOffset CompletedAt,DateTimeOffset? ExpiresAt,string? Status);
public sealed record CreateWorkerCompetencyRequest(string Competency,string? Level,string? Status,string? AssessedBy,DateTimeOffset AssessedAt,DateTimeOffset? ExpiresAt,string? Notes);
public sealed record CreateAvailabilityRuleRequest(int DayOfWeek,TimeOnly StartTime,TimeOnly EndTime,bool IsAvailable,DateOnly EffectiveFrom,DateOnly? EffectiveTo,string? Notes);
