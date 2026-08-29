using System.Data.Common;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[ApiController]
[Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
[Route("api/phase1/scheduling")]
public sealed class AdvancedSchedulingController(CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) : ControllerBase
{
    [HttpGet("care-workers/{workerId:guid}/absences")]
    public async Task<IActionResult> Absences(Guid workerId, CancellationToken token)
    {
        if (!await WorkerExists(workerId, token)) return NotFound();
        var rows = new List<WorkerAbsenceResponse>();
        await using var command = await Command("select id,absence_type,starts_at,ends_at,status,notes from worker_absences where care_worker_id=@worker and organization_id=@organization order by starts_at desc", token);
        Add(command,"worker",workerId);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) rows.Add(new(reader.GetGuid(0),reader.GetString(1),reader.GetFieldValue<DateTimeOffset>(2),reader.GetFieldValue<DateTimeOffset>(3),reader.GetString(4),reader.GetString(5)));
        return Ok(rows);
    }

    [HttpPost("care-workers/{workerId:guid}/absences")]
    public async Task<IActionResult> AddAbsence(Guid workerId, CreateWorkerAbsenceRequest request, CancellationToken token)
    {
        if (!await WorkerExists(workerId, token)) return NotFound();
        if (request.EndsAt <= request.StartsAt || request.AbsenceType is not ("Leave" or "Sickness")) return BadRequest(new { message = "Leave or Sickness and a valid time range are required." });
        var id = Guid.NewGuid();
        await using var command = await Command("insert into worker_absences(id,care_worker_id,organization_id,branch_id,absence_type,starts_at,ends_at,status,notes) values(@id,@worker,@organization,@branch,@type,@start,@end,@status,@notes)", token);
        Add(command,"id",id); Add(command,"worker",workerId); Add(command,"type",request.AbsenceType); Add(command,"start",request.StartsAt); Add(command,"end",request.EndsAt); Add(command,"status",string.IsNullOrWhiteSpace(request.Status)?"Approved":request.Status); Add(command,"notes",request.Notes??"");
        await command.ExecuteNonQueryAsync(token);
        context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),"worker_absence.created",currentUser.UserName,"WorkerAbsence",id,DateTimeOffset.UtcNow,tenant.OrganizationId,tenant.BranchId??TenantDefaults.BranchId));
        await context.SaveChangesAsync(token);
        return Created($"/api/phase1/scheduling/care-workers/{workerId}/absences/{id}",new { id });
    }

    [HttpGet("policy")]
    public async Task<IActionResult> Policy(CancellationToken token) => Ok(await ReadPolicy(token));

    [HttpPut("policy")]
    public async Task<IActionResult> UpdatePolicy(SchedulingPolicyResponse request, CancellationToken token)
    {
        if (request.MinimumRestMinutes < 0 || request.MaximumDailyMinutes <= 0 || request.MaximumWeeklyMinutes <= 0 || request.TravelBufferMinutes < 0) return BadRequest(new { message = "Scheduling policy values are invalid." });
        await using var command = await Command("insert into scheduling_policies(organization_id,branch_id,minimum_rest_minutes,maximum_daily_minutes,maximum_weekly_minutes,travel_buffer_minutes,updated_at) values(@organization,@branch,@rest,@daily,@weekly,@travel,now()) on conflict(organization_id,branch_id) do update set minimum_rest_minutes=@rest,maximum_daily_minutes=@daily,maximum_weekly_minutes=@weekly,travel_buffer_minutes=@travel,updated_at=now()", token);
        Add(command,"rest",request.MinimumRestMinutes); Add(command,"daily",request.MaximumDailyMinutes); Add(command,"weekly",request.MaximumWeeklyMinutes); Add(command,"travel",request.TravelBufferMinutes);
        await command.ExecuteNonQueryAsync(token);
        return Ok(request);
    }

    private async Task<bool> WorkerExists(Guid id,CancellationToken token) => await context.CareWorkers.AnyAsync(x=>x.Id==id&&x.OrganizationId==tenant.OrganizationId,token);
    private async Task<SchedulingPolicyResponse> ReadPolicy(CancellationToken token)
    {
        await using var command=await Command("select minimum_rest_minutes,maximum_daily_minutes,maximum_weekly_minutes,travel_buffer_minutes from scheduling_policies where organization_id=@organization and branch_id=@branch",token);
        await using var reader=await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)?new(reader.GetInt32(0),reader.GetInt32(1),reader.GetInt32(2),reader.GetInt32(3)):new(660,720,2880,15);
    }
    private async Task<DbCommand> Command(string sql,CancellationToken token){var connection=context.Database.GetDbConnection();if(connection.State!=System.Data.ConnectionState.Open)await connection.OpenAsync(token);var command=connection.CreateCommand();command.CommandText=sql;Add(command,"organization",tenant.OrganizationId);Add(command,"branch",tenant.BranchId??TenantDefaults.BranchId);return command;}
    private static void Add(DbCommand command,string name,object value){if(command.Parameters.Contains(name))return;var p=command.CreateParameter();p.ParameterName=name;p.Value=value;command.Parameters.Add(p);}
}

public sealed record CreateWorkerAbsenceRequest(string AbsenceType,DateTimeOffset StartsAt,DateTimeOffset EndsAt,string? Status,string? Notes);
public sealed record WorkerAbsenceResponse(Guid Id,string AbsenceType,DateTimeOffset StartsAt,DateTimeOffset EndsAt,string Status,string Notes);
public sealed record SchedulingPolicyResponse(int MinimumRestMinutes,int MaximumDailyMinutes,int MaximumWeeklyMinutes,int TravelBufferMinutes);
