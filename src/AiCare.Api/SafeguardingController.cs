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
[Route("api/phase1/safeguarding")]
public sealed class SafeguardingController : ControllerBase
{
    private readonly CareDbContext _context;
    private readonly ITenantContext _tenant;
    private readonly ICurrentUserContext _currentUser;

    public SafeguardingController(CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser)
    {
        _context = context; _tenant = tenant; _currentUser = currentUser;
    }

    [HttpGet("cases")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public async Task<IActionResult> GetCases([FromQuery] Guid? serviceUserId, [FromQuery] string? status, CancellationToken cancellationToken)
        => Ok(await QueryCases(serviceUserId, status, cancellationToken));

    [HttpPost("cases")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public Task<IActionResult> CreateCase(CreateSafeguardingCaseRequest request, CancellationToken cancellationToken)
        => InTransaction(() => CreateCaseCore(request, cancellationToken), cancellationToken);

    private async Task<IActionResult> CreateCaseCore(CreateSafeguardingCaseRequest request, CancellationToken cancellationToken)
    {
        var person = await _context.ServiceUsers.SingleOrDefaultAsync(x => x.Id == request.ServiceUserId, cancellationToken);
        if (person is null || !_tenant.CanAccess(person.OrganizationId, person.BranchId)) return NotFound();
        if (string.IsNullOrWhiteSpace(request.Category) || string.IsNullOrWhiteSpace(request.Concern) || string.IsNullOrWhiteSpace(request.RiskLevel)) return BadRequest(new { message = "Category, concern and risk level are required." });
        if (NormalizeRisk(request.RiskLevel) is null) return BadRequest(new { message = "Invalid safeguarding risk level." });
        if (request.IncidentId is not null)
        {
            var incident = await _context.Incidents.SingleOrDefaultAsync(x => x.Id == request.IncidentId && x.ServiceUserId == request.ServiceUserId, cancellationToken);
            if (incident is null) return BadRequest(new { message = "Linked incident was not found for this person." });
        }
        var id = Guid.NewGuid();
        await Execute("""
            insert into safeguarding_cases(id,service_user_id,incident_id,organization_id,branch_id,category,concern,immediate_actions,risk_level,status,external_referral,referral_reference,owner,opened_at,review_due_at,created_by,updated_at)
            values(@id,@person,@incident,@organization,@branch,@category,@concern,@actions,@risk,'Open',@referral,@reference,@owner,now(),@review,@createdby,now())
            """, command =>
        {
            Add(command,"id",id); Add(command,"person",request.ServiceUserId); Add(command,"incident",request.IncidentId); Add(command,"organization",person.OrganizationId ?? _tenant.OrganizationId); Add(command,"branch",person.BranchId ?? _tenant.BranchId ?? TenantDefaults.BranchId);
            Add(command,"category",request.Category.Trim()); Add(command,"concern",request.Concern.Trim()); Add(command,"actions",request.ImmediateActions?.Trim() ?? ""); Add(command,"risk",NormalizeRisk(request.RiskLevel)); Add(command,"referral",request.ExternalReferral?.Trim() ?? ""); Add(command,"reference",request.ReferralReference?.Trim() ?? ""); Add(command,"owner",request.Owner?.Trim() ?? _currentUser.UserName); Add(command,"review",request.ReviewDueAt?.UtcDateTime); Add(command,"createdby",_currentUser.UserName);
        }, cancellationToken);
        _context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "safeguarding.case_opened", _currentUser.UserName, "SafeguardingCase", id, DateTimeOffset.UtcNow, person.OrganizationId, person.BranchId));
        await _context.SaveChangesAsync(cancellationToken);
        return Created($"/api/phase1/safeguarding/cases/{id}", (await QueryCases(request.ServiceUserId, null, cancellationToken)).Single(x => x.Id == id));
    }

    [HttpPut("cases/{caseId:guid}")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public Task<IActionResult> UpdateCase(Guid caseId, UpdateSafeguardingCaseRequest request, CancellationToken cancellationToken)
        => InTransaction(() => UpdateCaseCore(caseId, request, cancellationToken), cancellationToken);

    private async Task<IActionResult> UpdateCaseCore(Guid caseId, UpdateSafeguardingCaseRequest request, CancellationToken cancellationToken)
    {
        if (!await LockCase(caseId, cancellationToken)) return NotFound();
        var existing = (await QueryCases(null, null, cancellationToken)).SingleOrDefault(x => x.Id == caseId);
        if (existing is null) return NotFound();
        if (existing.Status == "Closed") return Conflict(new { message = "Closed cases are immutable. Use the explicit reopen endpoint." });
        var allowed = new[] { "Open", "Investigating", "Referred", "Monitoring", "Closed" };
        var effectiveRisk = NormalizeRisk(request.RiskLevel ?? existing.RiskLevel);
        if (effectiveRisk is null) return BadRequest(new { message = "Invalid safeguarding risk level." });
        if (!allowed.Contains(request.Status, StringComparer.OrdinalIgnoreCase)) return BadRequest(new { message = "Invalid safeguarding status." });
        if (request.Status.Equals("Closed", StringComparison.OrdinalIgnoreCase))
        {
            if (!_currentUser.IsCareManager && !_currentUser.IsAdministrator) return Forbid();
            if (string.IsNullOrWhiteSpace(request.ClosureSummary)) return BadRequest(new { message = "Closure summary is required when closing a safeguarding case." });
            if (await Scalar("select count(*) from safeguarding_case_actions where case_id=@case and status<>'Completed'",caseId,cancellationToken)>0) return Conflict(new { message = "All safeguarding actions must be completed before closure." });
            if ((effectiveRisk == "High" || effectiveRisk == "Critical")&&(string.IsNullOrWhiteSpace(request.ExternalReferral??existing.ExternalReferral)||string.IsNullOrWhiteSpace(request.ReferralReference??existing.ReferralReference))) return Conflict(new { message = "High and critical risk cases require external referral evidence before closure." });
        }
        await Execute("""
            update safeguarding_cases set immediate_actions=@actions,risk_level=@risk,status=@status,external_referral=@referral,referral_reference=@reference,owner=@owner,review_due_at=@review,closed_at=@closed,closure_summary=@summary,updated_at=now()
            where id=@id and organization_id=@organization
            """, command =>
        {
            Add(command,"actions",request.ImmediateActions?.Trim() ?? existing.ImmediateActions); Add(command,"risk",effectiveRisk); Add(command,"status",allowed.Single(value => value.Equals(request.Status, StringComparison.OrdinalIgnoreCase))); Add(command,"referral",request.ExternalReferral?.Trim() ?? existing.ExternalReferral); Add(command,"reference",request.ReferralReference?.Trim() ?? existing.ReferralReference); Add(command,"owner",request.Owner?.Trim() ?? existing.Owner); Add(command,"review",request.ReviewDueAt?.UtcDateTime); Add(command,"closed",request.Status.Equals("Closed",StringComparison.OrdinalIgnoreCase)?DateTime.UtcNow:null); Add(command,"summary",request.ClosureSummary?.Trim() ?? ""); Add(command,"id",caseId); Add(command,"organization",_tenant.OrganizationId);
        }, cancellationToken);
        await Event(caseId,request.Status.Equals("Closed",StringComparison.OrdinalIgnoreCase)?"Closed":"StatusChanged",$"Status changed from {existing.Status} to {request.Status}. {request.ClosureSummary}",cancellationToken);
        _context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), request.Status.Equals("Closed",StringComparison.OrdinalIgnoreCase)?"safeguarding.case_closed":"safeguarding.case_updated", _currentUser.UserName, "SafeguardingCase", caseId, DateTimeOffset.UtcNow, _tenant.OrganizationId, _tenant.BranchId));
        await _context.SaveChangesAsync(cancellationToken);
        return Ok((await QueryCases(null, null, cancellationToken)).Single(x => x.Id == caseId));
    }

    [HttpPost("cases/{caseId:guid}/reopen")]
    [Authorize(Roles = "CareManager,Administrator")]
    public Task<IActionResult> ReopenCase(Guid caseId, ReopenSafeguardingCaseRequest request, CancellationToken token)
        => InTransaction(async () =>
        {
            if (!await LockCase(caseId, token)) return (IActionResult)NotFound();
            var existing = (await QueryCases(null, null, token)).Single(x => x.Id == caseId);
            if (existing.Status != "Closed") return Conflict(new { message = "Only closed safeguarding cases can be reopened." });
            if (string.IsNullOrWhiteSpace(request.Reason)) return BadRequest(new { message = "A reopening reason is required." });

            // Snapshot the previous closure before clearing current-state fields. Events are immutable.
            var detail = System.Text.Json.JsonSerializer.Serialize(new {
                reason = request.Reason.Trim(), previousStatus = existing.Status,
                previousClosedAt = existing.ClosedAt, previousClosureSummary = existing.ClosureSummary,
                previousRiskLevel = existing.RiskLevel
            });
            await Event(caseId, "Reopened", detail, token);
            await Execute("""
                update safeguarding_cases set status='Open',closed_at=null,closure_summary='',updated_at=now()
                where id=@id and organization_id=@organization and status='Closed'
                """, c => { Add(c,"id",caseId); Add(c,"organization",_tenant.OrganizationId); }, token);
            await Execute("""
                insert into "AuditEvents" ("Id","Action","Actor","EntityType","EntityId","CreatedAt","OrganizationId","BranchId")
                select @audit,'safeguarding.case_reopened',@actor,'SafeguardingCase',id,now(),organization_id,branch_id
                from safeguarding_cases where id=@id and organization_id=@organization
                """, c => { Add(c,"audit",Guid.NewGuid()); Add(c,"actor",_currentUser.UserName); Add(c,"id",caseId); Add(c,"organization",_tenant.OrganizationId); }, token);
            return NoContent();
        }, token);

    [HttpGet("cases/{caseId:guid}/actions")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public async Task<IActionResult> GetActions(Guid caseId, CancellationToken cancellationToken)
    {
        if (!(await QueryCases(null,null,cancellationToken)).Any(x=>x.Id==caseId)) return NotFound();
        return Ok(await QueryActions(caseId,cancellationToken));
    }

    [HttpPost("cases/{caseId:guid}/actions")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public Task<IActionResult> AddAction(Guid caseId, SafeguardingActionRequest request, CancellationToken cancellationToken)
        => InTransaction(() => AddActionCore(caseId, request, cancellationToken), cancellationToken);

    private async Task<IActionResult> AddActionCore(Guid caseId, SafeguardingActionRequest request, CancellationToken cancellationToken)
    {
        if (!await LockCase(caseId, cancellationToken)) return NotFound();
        if (!(await QueryCases(null,null,cancellationToken)).Any(x=>x.Id==caseId)) return NotFound();
        if ((await QueryCases(null,null,cancellationToken)).Single(x=>x.Id==caseId).Status == "Closed") return Conflict(new { message = "Closed cases cannot receive new actions." });
        if (string.IsNullOrWhiteSpace(request.ActionType) || string.IsNullOrWhiteSpace(request.Detail)) return BadRequest(new { message = "Action type and detail are required." });
        var id=Guid.NewGuid();
        await Execute("insert into safeguarding_case_actions(id,case_id,action_type,detail,owner,due_at,status,created_at) values(@id,@case,@type,@detail,@owner,@due,'Open',now())", command=>{ Add(command,"id",id);Add(command,"case",caseId);Add(command,"type",request.ActionType.Trim());Add(command,"detail",request.Detail.Trim());Add(command,"owner",request.Owner?.Trim()??_currentUser.UserName);Add(command,"due",request.DueAt?.UtcDateTime);}, cancellationToken);
        _context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "safeguarding.action_added", _currentUser.UserName, "SafeguardingCase", caseId, DateTimeOffset.UtcNow, _tenant.OrganizationId, _tenant.BranchId)); await _context.SaveChangesAsync(cancellationToken);
        return Created($"/api/phase1/safeguarding/cases/{caseId}/actions/{id}", new { id });
    }

    [HttpPost("cases/{caseId:guid}/actions/{actionId:guid}/complete")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public Task<IActionResult> CompleteAction(Guid caseId, Guid actionId, CompleteSafeguardingActionRequest request, CancellationToken cancellationToken)
        => InTransaction(() => CompleteActionCore(caseId, actionId, request, cancellationToken), cancellationToken);

    private async Task<IActionResult> CompleteActionCore(Guid caseId, Guid actionId, CompleteSafeguardingActionRequest request, CancellationToken cancellationToken)
    {
        if (!await LockCase(caseId, cancellationToken)) return NotFound();
        if (!(await QueryCases(null,null,cancellationToken)).Any(x=>x.Id==caseId)) return NotFound();
        if(string.IsNullOrWhiteSpace(request.CompletionEvidence))return BadRequest(new{message="Completion evidence is required."});
        var affected=await Execute("update safeguarding_case_actions set status='Completed',completed_at=now(),completion_evidence=@evidence,completed_by=@actor where id=@id and case_id=@case and status<>'Completed'", command=>{Add(command,"id",actionId);Add(command,"case",caseId);Add(command,"evidence",request.CompletionEvidence.Trim());Add(command,"actor",_currentUser.UserName);}, cancellationToken);
        if(affected==0)return NotFound();
        await Event(caseId,"ActionCompleted",request.CompletionEvidence.Trim(),cancellationToken);
        _context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "safeguarding.action_completed", _currentUser.UserName, "SafeguardingCase", caseId, DateTimeOffset.UtcNow, _tenant.OrganizationId, _tenant.BranchId)); await _context.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private static string? NormalizeRisk(string? risk) => new[] { "Low", "Medium", "High", "Critical" }
        .SingleOrDefault(value => value.Equals(risk?.Trim(), StringComparison.OrdinalIgnoreCase));

    private Task<IActionResult> InTransaction(Func<Task<IActionResult>> operation, CancellationToken token)
        => _context.Database.CreateExecutionStrategy().ExecuteAsync(async () => {
            await using var tx = await _context.Database.BeginTransactionAsync(token);
            var result = await operation();
            await tx.CommitAsync(token);
            return result;
        });

    private async Task<bool> LockCase(Guid id, CancellationToken token)
    {
        var connection = _context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = _context.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "select id from safeguarding_cases where id=@id and organization_id=@organization and (@wide or branch_id=@branch) for update";
        Add(command,"id",id); Add(command,"organization",_tenant.OrganizationId);
        Add(command,"wide",_tenant.IsOrganizationWide); Add(command,"branch",_tenant.BranchId);
        return await command.ExecuteScalarAsync(token) is Guid;
    }

    private async Task<List<SafeguardingCaseResponse>> QueryCases(Guid? serviceUserId,string? status,CancellationToken cancellationToken)
    {
        var result=new List<SafeguardingCaseResponse>(); var connection=_context.Database.GetDbConnection(); var opened=connection.State!=ConnectionState.Open; if(opened) await connection.OpenAsync(cancellationToken);
        try { await using var command=connection.CreateCommand(); command.Transaction=_context.Database.CurrentTransaction?.GetDbTransaction();command.CommandText="select id,service_user_id,incident_id,category,concern,immediate_actions,risk_level,status,external_referral,referral_reference,owner,opened_at,review_due_at,closed_at,closure_summary,created_by,updated_at from safeguarding_cases where organization_id=@organization and (@wide or branch_id=@branch) and (cast(@person as uuid) is null or service_user_id=cast(@person as uuid)) and (cast(@filter_status as text) is null or lower(status)=lower(cast(@filter_status as text))) order by opened_at desc"; Add(command,"organization",_tenant.OrganizationId);Add(command,"wide",_tenant.IsOrganizationWide);Add(command,"branch",_tenant.BranchId);Add(command,"person",serviceUserId);Add(command,"filter_status",string.IsNullOrWhiteSpace(status)?null:status); await using var reader=await command.ExecuteReaderAsync(cancellationToken); while(await reader.ReadAsync(cancellationToken)) result.Add(new SafeguardingCaseResponse(reader.GetGuid(0),reader.GetGuid(1),reader.IsDBNull(2)?null:reader.GetGuid(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetString(6),reader.GetString(7),reader.GetString(8),reader.GetString(9),reader.GetString(10),reader.GetDateTime(11),ReadDate(reader,12),ReadDate(reader,13),reader.GetString(14),reader.GetString(15),reader.GetDateTime(16))); return result; }
        finally { if(opened) await connection.CloseAsync(); }
    }

    private async Task<List<SafeguardingActionResponse>> QueryActions(Guid caseId,CancellationToken cancellationToken)
    {
        var result=new List<SafeguardingActionResponse>();var connection=_context.Database.GetDbConnection();var opened=connection.State!=ConnectionState.Open;if(opened)await connection.OpenAsync(cancellationToken);
        try{await using var command=connection.CreateCommand();command.Transaction=_context.Database.CurrentTransaction?.GetDbTransaction();command.CommandText="select id,action_type,detail,owner,due_at,completed_at,status,created_at,completion_evidence,completed_by from safeguarding_case_actions where case_id=@case order by created_at desc";Add(command,"case",caseId);await using var reader=await command.ExecuteReaderAsync(cancellationToken);while(await reader.ReadAsync(cancellationToken))result.Add(new SafeguardingActionResponse(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),ReadDate(reader,4),ReadDate(reader,5),reader.GetString(6),reader.GetDateTime(7),reader.GetString(8),reader.GetString(9)));return result;}finally{if(opened)await connection.CloseAsync();}
    }

    private Task Event(Guid caseId,string type,string detail,CancellationToken token)=>Execute("insert into safeguarding_case_events(id,case_id,organization_id,branch_id,event_type,detail,actor) select @id,id,organization_id,branch_id,@type,@detail,@actor from safeguarding_cases where id=@case and organization_id=@organization",c=>{Add(c,"id",Guid.NewGuid());Add(c,"case",caseId);Add(c,"organization",_tenant.OrganizationId);Add(c,"branch",_tenant.BranchId??TenantDefaults.BranchId);Add(c,"type",type);Add(c,"detail",detail);Add(c,"actor",_currentUser.UserName);},token);
    private async Task<long> Scalar(string sql,Guid caseId,CancellationToken token){var connection=_context.Database.GetDbConnection();var opened=connection.State!=ConnectionState.Open;if(opened)await connection.OpenAsync(token);try{await using var command=connection.CreateCommand();command.Transaction=_context.Database.CurrentTransaction?.GetDbTransaction();command.CommandText=sql;Add(command,"case",caseId);return Convert.ToInt64(await command.ExecuteScalarAsync(token));}finally{if(opened)await connection.CloseAsync();}}
    private async Task<int> Execute(string sql,Action<DbCommand> bind,CancellationToken cancellationToken){var connection=_context.Database.GetDbConnection();var opened=connection.State!=ConnectionState.Open;if(opened)await connection.OpenAsync(cancellationToken);try{await using var command=connection.CreateCommand();command.Transaction=_context.Database.CurrentTransaction?.GetDbTransaction();command.CommandText=sql;bind(command);return await command.ExecuteNonQueryAsync(cancellationToken);}finally{if(opened)await connection.CloseAsync();}}
    private static DateTimeOffset? ReadDate(DbDataReader reader,int ordinal)=>reader.IsDBNull(ordinal)?null:new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(ordinal),DateTimeKind.Utc));
    private static void Add(DbCommand command,string name,object? value){var p=command.CreateParameter();p.ParameterName=name;p.Value=value??DBNull.Value;command.Parameters.Add(p);}
}

public sealed record CreateSafeguardingCaseRequest(Guid ServiceUserId,Guid? IncidentId,string Category,string Concern,string? ImmediateActions,string RiskLevel,string? ExternalReferral,string? ReferralReference,string? Owner,DateTimeOffset? ReviewDueAt);
public sealed record UpdateSafeguardingCaseRequest(string Status,string? ImmediateActions,string? RiskLevel,string? ExternalReferral,string? ReferralReference,string? Owner,DateTimeOffset? ReviewDueAt,string? ClosureSummary);
public sealed record SafeguardingActionRequest(string ActionType,string Detail,string? Owner,DateTimeOffset? DueAt);
public sealed record CompleteSafeguardingActionRequest(string CompletionEvidence);
public sealed record SafeguardingCaseResponse(Guid Id,Guid ServiceUserId,Guid? IncidentId,string Category,string Concern,string ImmediateActions,string RiskLevel,string Status,string ExternalReferral,string ReferralReference,string Owner,DateTimeOffset OpenedAt,DateTimeOffset? ReviewDueAt,DateTimeOffset? ClosedAt,string ClosureSummary,string CreatedBy,DateTimeOffset UpdatedAt);
public sealed record SafeguardingActionResponse(Guid Id,string ActionType,string Detail,string Owner,DateTimeOffset? DueAt,DateTimeOffset? CompletedAt,string Status,DateTimeOffset CreatedAt,string CompletionEvidence,string CompletedBy);

public sealed record ReopenSafeguardingCaseRequest(string? Reason);
