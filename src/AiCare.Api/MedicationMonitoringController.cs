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
[Route("api/phase1/emar-safety/medications/{medicationId:guid}/alerts")]
public sealed class MedicationMonitoringController(CareDbContext db, ITenantContext tenant,
    ICurrentUserContext user) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid medicationId, CancellationToken token)
    {
        if (!await Accessible(medicationId,token)) return NotFound();
        await using var command = await Command("""
            select id,alert_type,due_at,severity,status,message,owner,acknowledged_at,
              acknowledged_by,resolved_at,resolution,created_at
            from medication_monitoring_alerts
            where medication_id=@medication and organization_id=@organization and branch_id=@branch
            order by case status when 'Open' then 0 when 'Acknowledged' then 1
              when 'InProgress' then 2 else 3 end,due_at,id
            """,medicationId,token);
        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new List<object>();
        while (await reader.ReadAsync(token)) rows.Add(new {
            id=reader.GetGuid(0),alertType=reader.GetString(1),dueAt=reader.GetFieldValue<DateTimeOffset>(2),
            severity=reader.GetString(3),status=reader.GetString(4),message=reader.GetString(5),
            owner=reader.GetString(6),acknowledgedAt=Date(reader,7),acknowledgedBy=reader.GetString(8),
            resolvedAt=Date(reader,9),resolution=reader.GetString(10),createdAt=reader.GetFieldValue<DateTimeOffset>(11)
        });
        return Ok(rows);
    }

    [HttpPost("{alertId:guid}/progress")]
    [Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
    public async Task<IActionResult> Progress(Guid medicationId,Guid alertId,MedicationAlertDecision request,CancellationToken token)
    {
        if (!await Accessible(medicationId,token)) return NotFound();
        if (request.Action is not ("Acknowledge" or "Start" or "Resolve" or "Reopen") ||
            string.IsNullOrWhiteSpace(request.Comment))
            return BadRequest(new { message="A supported action and documented comment are required." });
        var sql = request.Action switch {
            "Acknowledge" => "update medication_monitoring_alerts set status='Acknowledged',acknowledged_at=now(),acknowledged_by=@actor,resolution=@comment where id=@id and medication_id=@medication and organization_id=@organization and branch_id=@branch and status='Open'",
            "Start" => "update medication_monitoring_alerts set status='InProgress',resolution=@comment where id=@id and medication_id=@medication and organization_id=@organization and branch_id=@branch and status='Acknowledged'",
            "Resolve" => "update medication_monitoring_alerts set status='Resolved',resolved_at=now(),resolution=@comment where id=@id and medication_id=@medication and organization_id=@organization and branch_id=@branch and status in ('Acknowledged','InProgress')",
            _ => "update medication_monitoring_alerts set status='Open',resolved_at=null,resolution=@comment where id=@id and medication_id=@medication and organization_id=@organization and branch_id=@branch and status='Resolved'"
        };
        await using var command = await Command(sql,medicationId,token);
        Add(command,"id",alertId); Add(command,"comment",request.Comment.Trim());
        if (await command.ExecuteNonQueryAsync(token)!=1) return Conflict(new { message="Alert cannot be progressed from its current state." });
        db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),"emar.medication_alert_progressed",user.UserName,
            nameof(Medication),medicationId,DateTimeOffset.UtcNow,tenant.OrganizationId,tenant.BranchId??TenantDefaults.BranchId));
        await db.SaveChangesAsync(token);
        return NoContent();
    }

    private async Task<bool> Accessible(Guid medicationId,CancellationToken token)
    {
        var medication=await db.Medications.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==medicationId,token);
        return medication is not null && tenant.CanAccess(medication.OrganizationId,medication.BranchId);
    }
    private async Task<DbCommand> Command(string sql,Guid medicationId,CancellationToken token)
    {
        var connection=db.Database.GetDbConnection();
        if (connection.State!=ConnectionState.Open) await connection.OpenAsync(token);
        var command=connection.CreateCommand(); command.CommandText=sql;
        command.Transaction=db.Database.CurrentTransaction?.GetDbTransaction();
        Add(command,"medication",medicationId); Add(command,"organization",tenant.OrganizationId);
        Add(command,"branch",tenant.BranchId??TenantDefaults.BranchId); Add(command,"actor",user.UserName);
        return command;
    }
    private static DateTimeOffset? Date(DbDataReader reader,int index) =>
        reader.IsDBNull(index)?null:reader.GetFieldValue<DateTimeOffset>(index);
    private static void Add(DbCommand command,string name,object? value)
    {
        var parameter=command.CreateParameter(); parameter.ParameterName=name;
        parameter.Value=value??DBNull.Value; command.Parameters.Add(parameter);
    }
}
public sealed record MedicationAlertDecision(string Action,string Comment);
