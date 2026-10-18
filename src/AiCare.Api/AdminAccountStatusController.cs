using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[ApiController]
[Authorize(Policy = "Phase1User", Roles = "Administrator")]
[Route("api/phase1/admin/accounts")]
public sealed class AdminAccountStatusController(CareDbContext db, ITenantContext tenant, ICurrentUserContext currentUser, AdminPermissionService permissions) : ControllerBase
{
    [HttpGet("{id:guid}/permissions")]
    public async Task<IActionResult> Permissions(Guid id, CancellationToken token)
    {
        var account = await VisibleAccounts().AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, token);
        if (account is null) return NotFound();
        var disabled = (await permissions.Denied(tenant.OrganizationId, id, token)).ToHashSet(StringComparer.Ordinal);
        return Ok(new
        {
            account.Id,
            account.Role,
            items = AdminPermissionCatalog.All.Select(definition => new
            {
                definition.Key, definition.Label, definition.Description,
                grantedByRole = definition.Roles.Contains(account.Role),
                enabled = definition.Roles.Contains(account.Role) && !disabled.Contains(definition.Key)
            })
        });
    }

    [HttpGet("{id:guid}/permission-history")]
    public async Task<IActionResult> PermissionHistory(Guid id, CancellationToken token)
    {
        if (!await VisibleAccounts().AnyAsync(item => item.Id == id, token)) return NotFound();
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(token);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "select previous_disabled_keys,disabled_keys,reason,changed_by,changed_at from admin_permission_change_events where organization_id=@organization and user_id=@user order by changed_at desc limit 20";
            var organization = command.CreateParameter(); organization.ParameterName = "organization"; organization.Value = tenant.OrganizationId; command.Parameters.Add(organization);
            var target = command.CreateParameter(); target.ParameterName = "user"; target.Value = id; command.Parameters.Add(target);
            var history = new List<object>();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                history.Add(new { previousDisabledKeys = reader.GetFieldValue<string[]>(0), disabledKeys = reader.GetFieldValue<string[]>(1), reason = reader.GetString(2), changedBy = reader.GetString(3), changedAt = reader.GetFieldValue<DateTime>(4) });
            return Ok(history);
        }
        finally { if (opened) await connection.CloseAsync(); }
    }

    [HttpPut("{id:guid}/permissions")]
    public async Task<IActionResult> SetPermissions(Guid id, ChangeEffectivePermissions request, CancellationToken token)
    {
        var account = await VisibleAccounts().SingleOrDefaultAsync(item => item.Id == id, token);
        if (account is null) return NotFound();
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length is < 10 or > 500)
            return BadRequest(new { message = "A reason of 10 to 500 characters is required." });
        var disabled = request.DisabledKeys?.Distinct(StringComparer.Ordinal).ToArray() ?? [];
        if (disabled.Length > AdminPermissionCatalog.All.Length || disabled.Any(key => !AdminPermissionCatalog.GrantedByRole(account.Role, key)))
            return BadRequest(new { message = "Permissions may only be removed from this role's allowed set." });
        if (account.Id == currentUser.UserId && disabled.Any(key => key is "users.read" or "users.manage"))
            return Conflict(new { message = "You cannot remove your own user administration access." });
        var previous = await permissions.Denied(tenant.OrganizationId, id, token);
        if (previous.Order(StringComparer.Ordinal).SequenceEqual(disabled.Order(StringComparer.Ordinal), StringComparer.Ordinal)) return NoContent();

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await db.Database.ExecuteSqlInterpolatedAsync($"delete from admin_permission_denials where organization_id={tenant.OrganizationId} and user_id={id}", token);
            foreach (var key in disabled)
                await db.Database.ExecuteSqlInterpolatedAsync($"insert into admin_permission_denials(organization_id,user_id,permission_key,changed_by,reason) values({tenant.OrganizationId},{id},{key},{currentUser.UserName},{request.Reason.Trim()})", token);
            await db.Database.ExecuteSqlInterpolatedAsync($"insert into admin_permission_change_events(id,organization_id,user_id,previous_disabled_keys,disabled_keys,reason,changed_by) values({Guid.NewGuid()},{tenant.OrganizationId},{id},{previous},{disabled},{request.Reason.Trim()},{currentUser.UserName})", token);
            await RevokeSessions(id, request.Reason.Trim(), token);
            db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "admin.permissions_changed", currentUser.UserName, nameof(AppUser), id, DateTimeOffset.UtcNow, tenant.OrganizationId, account.BranchId ?? TenantDefaults.BranchId));
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        });
        return NoContent();
    }

    [HttpGet("{id:guid}/role-history")]
    public async Task<IActionResult> RoleHistory(Guid id, CancellationToken token)
    {
        if (!await VisibleAccounts().AnyAsync(user => user.Id == id, token)) return NotFound();
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(token);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "select old_role,new_role,reason,changed_by,changed_at from admin_role_change_events where organization_id=@organization and user_id=@user order by changed_at desc limit 20";
            var organization = command.CreateParameter(); organization.ParameterName = "organization"; organization.Value = tenant.OrganizationId; command.Parameters.Add(organization);
            var user = command.CreateParameter(); user.ParameterName = "user"; user.Value = id; command.Parameters.Add(user);
            var history = new List<object>();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                history.Add(new { oldRole = reader.GetString(0), newRole = reader.GetString(1), reason = reader.GetString(2), changedBy = reader.GetString(3), changedAt = reader.GetFieldValue<DateTime>(4) });
            return Ok(history);
        }
        finally { if (opened) await connection.CloseAsync(); }
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] bool administratorsOnly = false, [FromQuery] bool includeBlocked = false, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken token = default)
    {
        if (page < 1 || pageSize is < 1 or > 100) return BadRequest(new { message = "Page and page size are out of range." });
        var query = VisibleAccounts();
        if (administratorsOnly) query = query.Where(user => user.Role == UserRole.Administrator);
        if (!includeBlocked) query = query.Where(user => user.IsActive);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(user => EF.Functions.ILike(user.UserName, $"%{term}%") || EF.Functions.ILike(user.Email, $"%{term}%"));
        }

        var total = await query.CountAsync(token);
        var users = await query.AsNoTracking().OrderBy(user => user.UserName).ThenBy(user => user.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(token);
        var userIds = users.Select(user => user.Id).ToArray();
        var invitations = userIds.Length == 0 ? Array.Empty<Guid>() : await db.Database.SqlQuery<Guid>($"""select user_id as "Value" from account_invitations where user_id=any({userIds}) and status in ('Pending','Sent','Failed')""").ToArrayAsync(token);
        var invited = invitations.ToHashSet();
        return Ok(new { items = users.Select(user => new { user.Id, fullName = user.UserName, user.Email, user.Role, status = user.IsActive ? "Active" : invited.Contains(user.Id) ? "Invited" : "Suspended", user.OrganizationId, user.BranchId, user.CareWorkerId, user.FamilyMemberId }), total, page, pageSize });
    }

    [HttpPatch("{id:guid}/status")]
    public async Task<IActionResult> SetStatus(Guid id, ChangeAccountStatus request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < 10)
            return BadRequest(new { message = "A reason of at least 10 characters is required." });

        var account = await VisibleAccounts().SingleOrDefaultAsync(user => user.Id == id, token);
        if (account is null) return NotFound();
        if (account.Id == currentUser.UserId && !request.Active)
            return Conflict(new { message = "You cannot suspend your own account." });
        if (account.IsActive == request.Active)
            return Conflict(new { message = request.Active ? "Account is already active." : "Account is already suspended." });
        if (request.Active && await HasUnacceptedInvitation(id, token))
            return Conflict(new { message = "An invitation must be accepted before this account can be activated." });
        if (!request.Active && account.Role == UserRole.Administrator && !await AnotherActiveAdministrator(id, token))
            return Conflict(new { message = "The organization must retain an active administrator." });

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            db.Entry(account).CurrentValues.SetValues(account with { IsActive = request.Active });
            if (!request.Active) await RevokeSessions(id, request.Reason.Trim(), token);
            db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), request.Active ? "admin.account_unblocked" : "admin.account_suspended", currentUser.UserName, nameof(AppUser), id, DateTimeOffset.UtcNow, tenant.OrganizationId, account.BranchId ?? TenantDefaults.BranchId));
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        });
        return Ok(new { account.Id, status = request.Active ? "Active" : "Suspended" });
    }

    [HttpPost("bulk-unblock")]
    public async Task<IActionResult> BulkUnblock(BulkUnblockAccounts request, CancellationToken token)
    {
        var ids = request.UserIds?.Distinct().ToArray() ?? [];
        if (ids.Length is < 1 or > 100 || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length < 10)
            return BadRequest(new { message = "Select 1-100 accounts and provide a reason of at least 10 characters." });

        var accounts = await VisibleAccounts().Where(user => ids.Contains(user.Id)).ToListAsync(token);
        if (accounts.Count != ids.Length) return NotFound();
        if (accounts.Any(user => user.IsActive))
            return Conflict(new { message = "Select only suspended accounts." });
        foreach (var account in accounts)
            if (await HasUnacceptedInvitation(account.Id, token))
                return Conflict(new { message = "Invited accounts must accept their invitation before activation." });

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            foreach (var account in accounts)
            {
                db.Entry(account).CurrentValues.SetValues(account with { IsActive = true });
                db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "admin.account_unblocked", currentUser.UserName, nameof(AppUser), account.Id, DateTimeOffset.UtcNow, tenant.OrganizationId, account.BranchId ?? TenantDefaults.BranchId));
            }
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        });
        return Ok(new { updated = accounts.Count });
    }

    private IQueryable<AppUser> VisibleAccounts() => db.AppUsers.Where(user => user.OrganizationId == tenant.OrganizationId && user.Role != UserRole.BackOffice && (tenant.IsOrganizationWide || tenant.BranchId == null || user.BranchId == tenant.BranchId));

    private Task<bool> AnotherActiveAdministrator(Guid id, CancellationToken token) =>
        VisibleAccounts().AnyAsync(user => user.Id != id && user.IsActive && user.Role == UserRole.Administrator, token);

    private async Task<bool> HasUnacceptedInvitation(Guid id, CancellationToken token)
    {
        var count = await db.Database.SqlQuery<int>($"""select count(*)::integer as "Value" from account_invitations where user_id={id} and status in ('Pending','Sent','Failed')""").SingleAsync(token);
        return count > 0;
    }

    private async Task RevokeSessions(Guid id, string reason, CancellationToken token)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"update auth_sessions set revoked_at=now(),revoked_by={currentUser.UserName},revocation_reason={reason} where user_id={id} and organization_id={tenant.OrganizationId} and revoked_at is null", token);
        await db.Database.ExecuteSqlInterpolatedAsync($"update auth_refresh_tokens set revoked_at=now() where user_id={id} and revoked_at is null", token);
    }
}

public sealed record ChangeAccountStatus(bool Active, string Reason);
public sealed record BulkUnblockAccounts(Guid[] UserIds, string Reason);
public sealed record ChangeEffectivePermissions(string[] DisabledKeys, string Reason);
