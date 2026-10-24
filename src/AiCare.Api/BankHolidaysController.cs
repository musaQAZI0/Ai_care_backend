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

[ApiController, Authorize(Policy = "Phase1User", Roles = "Administrator")]
[Route("api/phase1/admin/bank-holidays")]
public sealed class BankHolidaysController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int year, CancellationToken token)
    {
        if (year is < 2000 or > 2099) return BadRequest(new { message = "Choose a year between 2000 and 2099." });
        await using var command = await Command("""
            select id, branch_id, holiday_date, name, updated_by, updated_at
            from admin_bank_holidays
            where organization_id=@organization and holiday_date>=@start and holiday_date<@end
              and (@wide or branch_id is null or branch_id=@branch)
            order by holiday_date, branch_id nulls first, name
            """, token);
        Add(command, "start", new DateOnly(year, 1, 1));
        Add(command, "end", new DateOnly(year + 1, 1, 1));
        var rows = new List<object>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) rows.Add(new
        {
            id = reader.GetGuid(0),
            branchId = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1),
            date = reader.GetFieldValue<DateOnly>(2),
            name = reader.GetString(3),
            updatedBy = reader.GetString(4),
            updatedAt = reader.GetFieldValue<DateTimeOffset>(5)
        });
        return Ok(rows);
    }

    [HttpPut]
    public async Task<IActionResult> Save(BankHolidayRequest request, CancellationToken token)
    {
        if (request.Date.Year is < 2000 or > 2099 || string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120)
            return BadRequest(new { message = "Choose a date and a name of at most 120 characters." });
        if (!tenant.IsOrganizationWide && request.BranchId != tenant.BranchId) return Forbid();
        if (request.BranchId is Guid branchId && !await db.Branches.AnyAsync(x => x.Id == branchId && x.OrganizationId == tenant.OrganizationId, token))
            return BadRequest(new { message = "Choose a branch in this organisation." });

        return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await using var command = await Command("""
                insert into admin_bank_holidays(id, organization_id, branch_id, holiday_date, name, created_by, updated_by)
                values(@id,@organization,@recordBranch,@date,@name,@actor,@actor)
                on conflict(organization_id,branch_id,holiday_date)
                do update set name=excluded.name,updated_by=excluded.updated_by,updated_at=now()
                returning id
                """, token);
            Add(command, "id", Guid.NewGuid());
            Add(command, "recordBranch", request.BranchId);
            Add(command, "date", request.Date);
            Add(command, "name", request.Name.Trim());
            var id = (Guid)(await command.ExecuteScalarAsync(token) ?? throw new InvalidOperationException("Bank holiday was not saved."));
            Audit("admin.bank_holiday_saved", id, request.BranchId);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Ok(new { id });
        });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id, CancellationToken token)
    {
        return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await using var command = await Command("""
                delete from admin_bank_holidays
                where id=@id and organization_id=@organization and (@wide or branch_id=@branch)
                returning branch_id
                """, token);
            Add(command, "id", id);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return NotFound();
            var branchId = reader.IsDBNull(0) ? (Guid?)null : reader.GetGuid(0);
            await reader.DisposeAsync();
            Audit("admin.bank_holiday_removed", id, branchId);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return NoContent();
        });
    }

    private async Task<DbCommand> Command(string sql, CancellationToken token)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(token);
        var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = sql;
        Add(command, "organization", tenant.OrganizationId);
        Add(command, "branch", tenant.BranchId);
        Add(command, "wide", tenant.IsOrganizationWide);
        Add(command, "actor", user.UserName);
        return command;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        if (command.Parameters.Contains(name)) return;
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private void Audit(string action, Guid id, Guid? branchId) => db.AuditEvents.Add(new AuditEvent(
        Guid.NewGuid(), action, user.UserName, "BankHoliday", id, DateTimeOffset.UtcNow,
        tenant.OrganizationId, branchId ?? tenant.BranchId ?? TenantDefaults.BranchId));
}

public sealed record BankHolidayRequest(DateOnly Date, string Name, Guid? BranchId);
