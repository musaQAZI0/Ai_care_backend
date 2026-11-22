using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[ApiController]
[Authorize(Policy = "Phase1User", Roles = "Administrator")]
[Route("api/phase1/admin/accounts")]
public sealed class AdminAccountBranchController(CareDbContext db, ITenantContext tenant, ICurrentUserContext actor) : ControllerBase
{
    [HttpPatch("{id:guid}/branch")]
    public async Task<IActionResult> AssignMissingBranch(Guid id, AssignAccountBranch request, CancellationToken token)
    {
        if (request.BranchId == Guid.Empty || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length is < 10 or > 500)
            return BadRequest(new { message = "Select a branch and provide a reason of 10 to 500 characters." });
        if (actor.UserId == id) return Conflict(new { message = "Another administrator must review your branch assignment." });
        if (!tenant.IsOrganizationWide && tenant.BranchId != request.BranchId) return Forbid();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync<IActionResult>(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var account = await db.AppUsers.AsNoTracking().SingleOrDefaultAsync(user => user.Id == id && user.OrganizationId == tenant.OrganizationId && user.Role == UserRole.CareWorker, token);
            if (account is null) return NotFound();
            if (account.BranchId is not null) return Conflict(new { message = "This account already has a branch. Refresh access details; this action only repairs missing assignments." });
            if (!await db.Branches.AnyAsync(branch => branch.Id == request.BranchId && branch.OrganizationId == tenant.OrganizationId && branch.Status == "Active", token))
                return BadRequest(new { message = "Select an active branch belonging to this organisation." });
            var worker = await db.CareWorkers.AsNoTracking().SingleOrDefaultAsync(worker => worker.Id == account.CareWorkerId && worker.OrganizationId == tenant.OrganizationId, token);
            if (worker is null || worker.BranchId != request.BranchId)
                return Conflict(new { message = "The linked worker profile must belong to the selected branch. Review the worker profile before assigning access." });
            var changed = await db.AppUsers.Where(user => user.Id == id && user.OrganizationId == tenant.OrganizationId && user.Role == UserRole.CareWorker && user.BranchId == null && user.CareWorkerId == account.CareWorkerId && db.CareWorkers.Any(worker => worker.Id == user.CareWorkerId && worker.OrganizationId == tenant.OrganizationId && worker.BranchId == request.BranchId))
                .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.BranchId, (Guid?)request.BranchId), token);
            if (changed != 1) return Conflict(new { message = "The account changed during review. Refresh before trying again." });
            var reason = request.Reason.Trim();
            await db.Database.ExecuteSqlInterpolatedAsync($"insert into admin_branch_change_events(id,organization_id,user_id,new_branch_id,care_worker_id,reason,changed_by) values({Guid.NewGuid()},{tenant.OrganizationId},{id},{request.BranchId},{account.CareWorkerId},{reason},{actor.UserName})", token);
            await db.Database.ExecuteSqlInterpolatedAsync($"update auth_sessions set revoked_at=now(),revoked_by={actor.UserName},revocation_reason={reason} where user_id={id} and organization_id={tenant.OrganizationId} and revoked_at is null", token);
            await db.Database.ExecuteSqlInterpolatedAsync($"update auth_refresh_tokens set revoked_at=now() where user_id={id} and revoked_at is null", token);
            db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "admin.account_branch_assigned", actor.UserName, nameof(AppUser), id, DateTimeOffset.UtcNow, tenant.OrganizationId, request.BranchId));
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Ok(new { id, branchId = request.BranchId, message = "Branch assigned. Existing sessions ended; the worker must sign in again." });
        });
    }
}

public sealed record AssignAccountBranch(Guid BranchId, string Reason);
