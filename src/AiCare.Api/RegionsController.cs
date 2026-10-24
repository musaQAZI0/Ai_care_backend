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
[Route("api/phase1/admin/regions")]
public sealed class RegionsController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken token)
    {
        await using var command = await Command("""
            select regions.name, count(branches."Id")::int as branch_count
            from (
                select name from admin_regions where organization_id=@organization
                    and (@wide or exists(select 1 from "Branches" scope where scope."OrganizationId"=@organization
                        and scope."Id"=@branch and lower(scope."Region")=lower(name)))
                union
                select "Region" as name from "Branches" where "OrganizationId"=@organization
                    and (@wide or "Id"=@branch)
            ) regions
            left join "Branches" branches on branches."OrganizationId"=@organization
                and (@wide or branches."Id"=@branch) and lower(branches."Region")=lower(regions.name)
            group by regions.name order by regions.name
            """, token);
        var rows = new List<object>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) rows.Add(new { name = reader.GetString(0), branchCount = reader.GetInt32(1) });
        return Ok(rows);
    }

    [HttpPost]
    public async Task<IActionResult> Create(RegionRequest request, CancellationToken token)
    {
        if (!tenant.IsOrganizationWide) return Forbid();
        var name = ValidName(request.Name);
        if (name is null) return BadRequest(new { message = "Enter a region name of at most 120 characters." });
        return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await using var check = await Command("""
                select exists(select 1 from admin_regions where organization_id=@organization and lower(name)=lower(@name))
                    or exists(select 1 from "Branches" where "OrganizationId"=@organization and lower("Region")=lower(@name))
                """, token);
            Add(check, "name", name);
            if ((bool)(await check.ExecuteScalarAsync(token) ?? false)) return Conflict(new { message = "This region already exists." });
            var id = Guid.NewGuid();
            await using var insert = await Command("insert into admin_regions(id,organization_id,name) values(@id,@organization,@name)", token);
            Add(insert, "id", id);
            Add(insert, "name", name);
            await insert.ExecuteNonQueryAsync(token);
            Audit("admin.region_created", id);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Created($"/api/phase1/admin/regions/{Uri.EscapeDataString(name)}", new { name, branchCount = 0 });
        });
    }

    [HttpPut("{oldName}")]
    public async Task<IActionResult> Rename(string oldName, RegionRequest request, CancellationToken token)
    {
        if (!tenant.IsOrganizationWide) return Forbid();
        var name = ValidName(request.Name);
        if (name is null) return BadRequest(new { message = "Enter a region name of at most 120 characters." });
        return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await using var check = await Command("""
                select exists(select 1 from admin_regions where organization_id=@organization and lower(name)=lower(@old))
                    or exists(select 1 from "Branches" where "OrganizationId"=@organization and lower("Region")=lower(@old))
                """, token);
            Add(check, "old", oldName);
            if (!(bool)(await check.ExecuteScalarAsync(token) ?? false)) return NotFound();
            if (!string.Equals(oldName, name, StringComparison.OrdinalIgnoreCase))
            {
                await using var duplicate = await Command("""
                    select exists(select 1 from admin_regions where organization_id=@organization and lower(name)=lower(@name))
                        or exists(select 1 from "Branches" where "OrganizationId"=@organization and lower("Region")=lower(@name))
                    """, token);
                Add(duplicate, "name", name);
                if ((bool)(await duplicate.ExecuteScalarAsync(token) ?? false)) return Conflict(new { message = "This region already exists." });
            }
            await using var update = await Command("""
                update admin_regions set name=@name where organization_id=@organization and lower(name)=lower(@old);
                update "Branches" set "Region"=@name where "OrganizationId"=@organization and lower("Region")=lower(@old)
                """, token);
            Add(update, "old", oldName);
            Add(update, "name", name);
            await update.ExecuteNonQueryAsync(token);
            Audit("admin.region_renamed", null);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Ok(new { name });
        });
    }

    [HttpDelete("{name}")]
    public async Task<IActionResult> Delete(string name, CancellationToken token)
    {
        if (!tenant.IsOrganizationWide) return Forbid();
        return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await using var inUse = await Command("select exists(select 1 from \"Branches\" where \"OrganizationId\"=@organization and lower(\"Region\")=lower(@name))", token);
            Add(inUse, "name", name);
            if ((bool)(await inUse.ExecuteScalarAsync(token) ?? false)) return Conflict(new { message = "Move branches to another region before deleting this one." });
            await using var remove = await Command("delete from admin_regions where organization_id=@organization and lower(name)=lower(@name)", token);
            Add(remove, "name", name);
            if (await remove.ExecuteNonQueryAsync(token) == 0) return NotFound();
            Audit("admin.region_deleted", null);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return NoContent();
        });
    }

    [HttpPut("branches/{id:guid}")]
    public async Task<IActionResult> AssignBranch(Guid id, RegionRequest request, CancellationToken token)
    {
        if (!tenant.IsOrganizationWide) return Forbid();
        var name = ValidName(request.Name);
        if (name is null) return BadRequest(new { message = "Choose a region." });
        var exists = await db.Database.SqlQuery<bool>($"select exists(select 1 from admin_regions where organization_id={tenant.OrganizationId} and lower(name)=lower({name}) union select 1 from \"Branches\" where \"OrganizationId\"={tenant.OrganizationId} and lower(\"Region\")=lower({name})) as \"Value\"").SingleAsync(token);
        if (!exists) return BadRequest(new { message = "Create this region before assigning it to a branch." });
        var branch = await db.Branches.SingleOrDefaultAsync(item => item.Id == id && item.OrganizationId == tenant.OrganizationId, token);
        if (branch is null) return NotFound();
        db.Entry(branch).CurrentValues.SetValues(branch with { Region = name });
        Audit("admin.branch_region_changed", id);
        await db.SaveChangesAsync(token);
        return NoContent();
    }

    private async Task<DbCommand> Command(string sql, CancellationToken token)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(token);
        var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = sql;
        Add(command, "organization", tenant.OrganizationId);
        Add(command, "wide", tenant.IsOrganizationWide);
        Add(command, "branch", tenant.BranchId);
        return command;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string? ValidName(string? name) => string.IsNullOrWhiteSpace(name) || name.Trim().Length > 120 ? null : name.Trim();
    private void Audit(string action, Guid? id) => db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), action, user.UserName, "Region", id, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
}

public sealed record RegionRequest(string Name);
