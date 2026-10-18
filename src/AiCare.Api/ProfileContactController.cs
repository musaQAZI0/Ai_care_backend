using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[ApiController]
[Authorize(Policy = "Phase1User")]
[Route("api/phase1/profile-contact")]
public sealed class ProfileContactController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user) : ControllerBase
{
    [HttpGet("{subjectType}/{id:guid}")]
    public async Task<IActionResult> Get(string subjectType, Guid id, CancellationToken token)
    {
        if (subjectType is not ("CareWorker" or "ServiceUser")) return NotFound();
        var manager = User.IsInRole("Administrator") || User.IsInRole("CareManager");
        var allowed = subjectType == "CareWorker"
            ? manager || (User.IsInRole("CareWorker") && user.CareWorkerId == id)
            : manager || User.IsInRole("CareCoordinator");
        if (!allowed) return Forbid();

        var branchId = subjectType == "CareWorker"
            ? await db.CareWorkers.Where(item => item.Id == id && item.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || item.BranchId == tenant.BranchId)).Select(item => item.BranchId).SingleOrDefaultAsync(token)
            : await db.ServiceUsers.Where(item => item.Id == id && item.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || item.BranchId == tenant.BranchId)).Select(item => item.BranchId).SingleOrDefaultAsync(token);
        if (branchId is null) return NotFound();

        var profile = await db.ProfileContactDetails.AsNoTracking().SingleOrDefaultAsync(item => item.OrganizationId == tenant.OrganizationId && item.BranchId == branchId && item.SubjectType == subjectType && item.SubjectId == id, token);
        db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "profile_contact.viewed", user.UserName, subjectType, id, DateTimeOffset.UtcNow, tenant.OrganizationId, branchId));
        await db.SaveChangesAsync(token);
        return Ok(profile is null ? new ProfileContactRequest() : new ProfileContactRequest(
            profile.Email, profile.MobilePhone, profile.Address, profile.Postcode,
            profile.EmergencyContactName, profile.EmergencyContactRelationship,
            profile.EmergencyContactPhone, profile.PreferredContactMethod));
    }
}
