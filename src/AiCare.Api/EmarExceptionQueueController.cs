using System.Data;
using System.Data.Common;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace AiCare.Api;

[ApiController]
[Authorize(Policy = "Phase1User")]
[Route("api/phase1/emar-safety/exceptions")]
public sealed class EmarExceptionQueueController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid? personId, [FromQuery] Guid? branchId,
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] string? status, [FromQuery] string? owner, CancellationToken token)
    {
        if (!user.HasAnyRole(UserRole.Administrator, UserRole.CareManager, UserRole.CareCoordinator)) return Forbid();
        if (from is not null && to is not null && from >= to) return BadRequest(new { message = "The end must follow the start." });
        if (from is not null && to is not null && to - from > TimeSpan.FromDays(93))
            return BadRequest(new { message = "Choose a period of at most 93 days." });
        if (status is not (null or "Outstanding" or "Open" or "Acknowledged" or "InProgress" or "Resolved" or "All"))
            return BadRequest(new { message = "Unsupported exception status." });
        if (branchId is not null && !tenant.CanAccess(tenant.OrganizationId, branchId)) return NotFound();

        var selectedBranch = branchId ?? tenant.BranchId;
        await using var command = db.Database.GetDbConnection().CreateCommand();
        if (command.Connection!.State != ConnectionState.Open) await command.Connection.OpenAsync(token);
        command.CommandText = """
            select e.id,e.mar_record_id,m."ServiceUserId",e.branch_id,m."Name",
                   r."ScheduledAt",e.escalation_type,e.severity,e.status,e.message,
                   e.owner,e.due_at,e.created_at
            from emar_escalations e
            join "MedicationAdministrationRecords" r on r."Id"=e.mar_record_id
              and r."OrganizationId"=e.organization_id and r."BranchId"=e.branch_id
            join "Medications" m on m."Id"=r."MedicationId"
              and m."OrganizationId"=e.organization_id and m."BranchId"=e.branch_id
            where e.organization_id=@organization
              and (@branch is null or e.branch_id=@branch)
              and (@person is null or m."ServiceUserId"=@person)
              and (@from is null or r."ScheduledAt">=@from)
              and (@to is null or r."ScheduledAt"<@to)
              and (@owner='' or e.owner ilike '%' || @owner || '%')
              and (@status='All' or @status='Outstanding' and e.status<>'Resolved' or e.status=@status)
            order by case e.status when 'Open' then 0 when 'Acknowledged' then 1
                     when 'InProgress' then 2 else 3 end,
                     coalesce(e.due_at,r."ScheduledAt"),e.created_at,e.id
            limit 500
            """;
        command.Parameters.Add(new NpgsqlParameter("organization", NpgsqlDbType.Uuid) { Value = tenant.OrganizationId });
        command.Parameters.Add(new NpgsqlParameter("branch", NpgsqlDbType.Uuid) { Value = selectedBranch is null ? DBNull.Value : selectedBranch.Value });
        command.Parameters.Add(new NpgsqlParameter("person", NpgsqlDbType.Uuid) { Value = personId is null ? DBNull.Value : personId.Value });
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = from is null ? DBNull.Value : from.Value });
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = to is null ? DBNull.Value : to.Value });
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = status ?? "Outstanding" });
        command.Parameters.Add(new NpgsqlParameter("owner", NpgsqlDbType.Text) { Value = owner?.Trim() ?? "" });
        var rows = new List<object>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) rows.Add(new
        {
            id = reader.GetGuid(0), marId = reader.GetGuid(1), personId = reader.GetGuid(2),
            branchId = reader.GetGuid(3), medicationName = reader.GetString(4),
            scheduledAt = reader.GetFieldValue<DateTimeOffset>(5), type = reader.GetString(6),
            severity = reader.GetString(7), status = reader.GetString(8), message = reader.GetString(9),
            owner = reader.GetString(10), dueAt = reader.IsDBNull(11) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(11),
            createdAt = reader.GetFieldValue<DateTimeOffset>(12)
        });
        return Ok(rows);
    }
}
