using AiCare.Application;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[ApiController]
[Authorize(Roles = "Administrator,CareManager,CareCoordinator")]
[Route("api/phase1/service-users/{serviceUserId:guid}/person-record/history")]
public sealed class PersonRecordHistoryController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user) : ControllerBase
{
    [HttpGet("~/api/phase1/service-users/{serviceUserId:guid}/profile/history")]
    public async Task<IActionResult> GetProfile(Guid serviceUserId, CancellationToken token)
    {
        var person = await db.ServiceUsers.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == serviceUserId && item.OrganizationId == tenant.OrganizationId, token);
        if (person is null || !tenant.CanAccess(person.OrganizationId, person.BranchId)) return NotFound();
        var revisions = await db.ServiceUserProfileRevisions.AsNoTracking()
            .Where(item => item.OrganizationId == tenant.OrganizationId && item.ServiceUserId == serviceUserId)
            .OrderByDescending(item => item.RecordedAt).ThenBy(item => item.Id).ToListAsync(token);
        db.AuditEvents.Add(new(Guid.NewGuid(), "service_user.history_viewed", user.UserName,
            "ServiceUser", serviceUserId, DateTimeOffset.UtcNow, person.OrganizationId, person.BranchId));
        await db.SaveChangesAsync(token);
        return Ok(revisions);
    }

    [HttpGet]
    public async Task<IActionResult> Get(Guid serviceUserId, CancellationToken token)
    {
        var person = await db.ServiceUsers.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == serviceUserId && item.OrganizationId == tenant.OrganizationId, token);
        if (person is null || !tenant.CanAccess(person.OrganizationId, person.BranchId)) return NotFound();

        // Access follows the person's current branch after a transfer. Earlier revisions
        // retain their original branch attribution and are never shared with worker/family roles.
        var revisions = await db.PersonRecordRevisions.AsNoTracking()
            .Where(item => item.OrganizationId == tenant.OrganizationId && item.ServiceUserId == serviceUserId)
            .OrderByDescending(item => item.RecordedAt).ThenBy(item => item.Id).ToListAsync(token);
        db.AuditEvents.Add(new(Guid.NewGuid(), "person_record.history_viewed", user.UserName,
            "ServiceUser", serviceUserId, DateTimeOffset.UtcNow, person.OrganizationId, person.BranchId));
        await db.SaveChangesAsync(token);
        return Ok(revisions);
    }
}
