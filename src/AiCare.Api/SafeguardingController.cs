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
    public async Task<IActionResult> CreateCase(CreateSafeguardingCaseRequest request, CancellationToken cancellationToken)
    {
        var person = await _context.ServiceUsers.SingleOrDefaultAsync(x => x.Id == request.ServiceUserId, cancellationToken);
        if (person is null || !_tenant.CanAccess(person.OrganizationId, person.BranchId)) return NotFound();
        if (string.IsNullOrWhiteSpace(request.Category) || string.IsNullOrWhiteSpace(request.Concern) || string.IsNullOrWhiteSpace(request.RiskLevel)) return BadRequest(new { message = "Category, concern and risk level are required." });
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
            Add(command,"category",request.Category.Trim()); Add(command,"concern",request.Concern.Trim()); Add(command,"actions",request.ImmediateActions?.Trim() ?? ""); Add(command,"risk",request.RiskLevel.Trim()); Add(command,"referral",request.ExternalReferral?.Trim() ?? ""); Add(command,"reference",request.ReferralReference?.Trim() ?? ""); Add(command,"owner",request.Owner?.Trim() ?? _currentUser.UserName); Add(command,"review",request.ReviewDueAt?.UtcDateTime); Add(command,"createdby",_currentUser.UserName);
        }, cancellationToken);
        _context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "safeguarding.case_opened", _currentUser.UserName, "SafeguardingCase", id, DateTimeOffset.UtcNow, person.OrganizationId, person.BranchId));
        await _context.SaveChangesAsync(cancellationToken);
        return Created($"/api/phase1/safeguarding/cases/{id}", (await QueryCases(request.ServiceUserId, null, cancellationToken)).Single(x => x.Id == id));
    }

    [HttpPut("cases/{caseId:guid}")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public async Task<IActionResult> UpdateCase(Guid caseId, UpdateSafeguardingCaseRequest request, CancellationToken cancellationToken)
    {
        var existing = (await QueryCases(null, null, cancellationToken)).SingleOrDefault(x => x.Id == caseId);
        if (existing is null) return NotFound();
        var allowed = new[] { "Open", "Investigating", "Referred", "Monitoring", "Closed" };
        if (!allowed.Contains(request.Status, StringComparer.OrdinalIgnoreCase)) return BadRequest(new { message = "Invalid safeguarding status." });
        if (request.Status.Equals("Closed", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(request.ClosureSummary)) return BadRequest(new { message = "Closure summary is required when closing a safeguarding case." });
        await Execute("""
            update safeguarding_cases set immediate_actions=@actions,risk_level=@risk,status=@status,external_referral=@referral,referral_reference=@reference,owner=@owner,review_due_at=@review,closed_at=@closed,closure_summary=@summary,updated_at=now()
            where id=@id and organization_id=@organization
            """, command =>
        {
            Add(command,"actions",request.ImmediateActions?.Trim() ?? existing.ImmediateActions); Add(command,"risk",request.RiskLevel?.Trim() ?? existing.RiskLevel); Add(command,"status",request.Status.Trim()); Add(command,"referral",request.ExternalReferral?.Trim() ?? existing.ExternalReferral); Add(command,"reference",request.ReferralReference?.Trim() ?? existing.ReferralReference); Add(command,"owner",request.Owner?.Trim() ?? existing.Owner); Add(command,"review",request.ReviewDueAt?.UtcDateTime); Add(command,"closed",request.Status.Equals("Closed",StringComparison.OrdinalIgnoreCase)?DateTime.UtcNow:null); Add(command,"summary",request.ClosureSummary?.Trim() ?? ""); Add(command,"id",caseId); Add(command,"organization",_tenant.OrganizationId);
        }, cancellationToken);
        _context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), request.Status.Equals("Closed",StringComparison.OrdinalIgnoreCase)?"safeguarding.case_closed":"safeguarding.case_updated", _currentUser.UserName, "SafeguardingCase", caseId, DateTimeOffset.UtcNow, _tenant.OrganizationId, _tenant.BranchId));
        await _context.SaveChangesAsync(cancellationToken);
        return Ok((await QueryCases(null, null, cancellationToken)).Single(x => x.Id == caseId));
    }

    [HttpGet("cases/{caseId:guid}/actions")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public async Task<IActionResult> GetActions(Guid caseId, CancellationToken cancellationToken)
    {
        if (!(await QueryCases(null,null,cancellationToken)).Any(x=>x.Id==caseId)) return NotFound();
        return Ok(await QueryActions(caseId,cancellationToken));
    }

    [HttpPost("cases/{caseId:guid}/actions")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public async Task<IActionResult> AddAction(Guid caseId, SafeguardingActionRequest request, CancellationToken cancellationToken)
    {
        if (!(await QueryCases(null,null,cancellationToken)).Any(x=>x.Id==caseId)) return NotFound();
        if (string.IsNullOrWhiteSpace(request.ActionType) || string.IsNullOrWhiteSpace(request.Detail)) return BadRequest(new { message = "Action type and detail are required." });
        var id=Guid.NewGuid();
        await Execute("insert into safeguarding_case_actions(id,case_id,action_type,detail,owner,due_at,status,created_at) values(@id,@case,@type,@detail,@owner,@due,'Open',now())", command=>{ Add(command,"id",id);Add(command,"case",caseId);Add(command,"type",request.ActionType.Trim());Add(command,"detail",request.Detail.Trim());Add(command,"owner",request.Owner?.Trim()??_currentUser.UserName);Add(command,"due",request.DueAt?.UtcDateTime);}, cancellationToken);
        _context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "safeguarding.action_added", _currentUser.UserName, "SafeguardingCase", caseId, DateTimeOffset.UtcNow, _tenant.OrganizationId, _tenant.BranchId)); await _context.SaveChangesAsync(cancellationToken);
        return Created($"/api/phase1/safeguarding/cases/{caseId}/actions/{id}", new { id });
    }

    [HttpPost("cases/{caseId:guid}/actions/{actionId:guid}/complete")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public async Task<IActionResult> CompleteAction(Guid caseId, Guid actionId, CancellationToken cancellationToken)
    {
        if (!(await QueryCases(null,null,cancellationToken)).Any(x=>x.Id==caseId)) return NotFound();
        await Execute("update safeguarding_case_actions set status='Completed',completed_at=now() where id=@id and case_id=@case", command=>{Add(command,"id",actionId);Add(command,"case",caseId);}, cancellationToken);
        _context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "safeguarding.action_completed", _currentUser.UserName, "SafeguardingCase", caseId, DateTimeOffset.UtcNow, _tenant.OrganizationId, _tenant.BranchId)); await _context.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private async Task<List<SafeguardingCaseResponse>> QueryCases(Guid? serviceUserId,string? status,CancellationToken cancellationToken)
    {
        var result=new List<SafeguardingCaseResponse>(); var connection=_context.Database.GetDbConnection(); var opened=connection.State!=ConnectionState.Open; if(opened) await connection.OpenAsync(cancellationToken);
        try { await using var command=connection.CreateCommand(); command.CommandText="select id,service_user_id,incident_id,category,concern,immediate_actions,risk_level,status,external_referral,referral_reference,owner,opened_at,review_due_at,closed_at,closure_summary,created_by,updated_at from safeguarding_cases where organization_id=@organization and (cast(@person as uuid) is null or service_user_id=cast(@person as uuid)) and (cast(@filter_status as text) is null or lower(status)=lower(cast(@filter_status as text))) order by opened_at desc"; Add(command,"organization",_tenant.OrganizationId);Add(command,"person",serviceUserId);Add(command,"filter_status",string.IsNullOrWhiteSpace(status)?null:status); await using var reader=await command.ExecuteReaderAsync(cancellationToken); while(await reader.ReadAsync(cancellationToken)) result.Add(new SafeguardingCaseResponse(reader.GetGuid(0),reader.GetGuid(1),reader.IsDBNull(2)?null:reader.GetGuid(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetString(6),reader.GetString(7),reader.GetString(8),reader.GetString(9),reader.GetString(10),reader.GetDateTime(11),ReadDate(reader,12),ReadDate(reader,13),reader.GetString(14),reader.GetString(15),reader.GetDateTime(16))); return result; }
        finally { if(opened) await connection.CloseAsync(); }
    }

    private async Task<List<SafeguardingActionResponse>> QueryActions(Guid caseId,CancellationToken cancellationToken)
    {
        var result=new List<SafeguardingActionResponse>();var connection=_context.Database.GetDbConnection();var opened=connection.State!=ConnectionState.Open;if(opened)await connection.OpenAsync(cancellationToken);
        try{await using var command=connection.CreateCommand();command.CommandText="select id,action_type,detail,owner,due_at,completed_at,status,created_at from safeguarding_case_actions where case_id=@case order by created_at desc";Add(command,"case",caseId);await using var reader=await command.ExecuteReaderAsync(cancellationToken);while(await reader.ReadAsync(cancellationToken))result.Add(new SafeguardingActionResponse(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),ReadDate(reader,4),ReadDate(reader,5),reader.GetString(6),reader.GetDateTime(7)));return result;}finally{if(opened)await connection.CloseAsync();}
    }

    private async Task Execute(string sql,Action<DbCommand> bind,CancellationToken cancellationToken){var connection=_context.Database.GetDbConnection();var opened=connection.State!=ConnectionState.Open;if(opened)await connection.OpenAsync(cancellationToken);try{await using var command=connection.CreateCommand();command.CommandText=sql;bind(command);await command.ExecuteNonQueryAsync(cancellationToken);}finally{if(opened)await connection.CloseAsync();}}
    private static DateTimeOffset? ReadDate(DbDataReader reader,int ordinal)=>reader.IsDBNull(ordinal)?null:new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(ordinal),DateTimeKind.Utc));
    private static void Add(DbCommand command,string name,object? value){var p=command.CreateParameter();p.ParameterName=name;p.Value=value??DBNull.Value;command.Parameters.Add(p);}
}

public sealed record CreateSafeguardingCaseRequest(Guid ServiceUserId,Guid? IncidentId,string Category,string Concern,string? ImmediateActions,string RiskLevel,string? ExternalReferral,string? ReferralReference,string? Owner,DateTimeOffset? ReviewDueAt);
public sealed record UpdateSafeguardingCaseRequest(string Status,string? ImmediateActions,string? RiskLevel,string? ExternalReferral,string? ReferralReference,string? Owner,DateTimeOffset? ReviewDueAt,string? ClosureSummary);
public sealed record SafeguardingActionRequest(string ActionType,string Detail,string? Owner,DateTimeOffset? DueAt);
public sealed record SafeguardingCaseResponse(Guid Id,Guid ServiceUserId,Guid? IncidentId,string Category,string Concern,string ImmediateActions,string RiskLevel,string Status,string ExternalReferral,string ReferralReference,string Owner,DateTimeOffset OpenedAt,DateTimeOffset? ReviewDueAt,DateTimeOffset? ClosedAt,string ClosureSummary,string CreatedBy,DateTimeOffset UpdatedAt);
public sealed record SafeguardingActionResponse(Guid Id,string ActionType,string Detail,string Owner,DateTimeOffset? DueAt,DateTimeOffset? CompletedAt,string Status,DateTimeOffset CreatedAt);
