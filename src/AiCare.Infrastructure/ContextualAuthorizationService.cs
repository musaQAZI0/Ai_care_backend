using AiCare.Application;
using AiCare.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Infrastructure;

public sealed class ContextualAuthorizationService(
    CareDbContext db,
    ITenantContext tenant,
    ICurrentUserContext user) : IContextualAuthorization
{
    public async Task<bool> CanReadServiceUserAsync(Guid serviceUserId, CancellationToken cancellationToken = default)
    {
        var person = await db.ServiceUsers.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == serviceUserId, cancellationToken);
        if (person is null || !tenant.CanAccess(person.OrganizationId, person.BranchId)) return false;
        if (user.HasAnyRole(UserRole.Administrator, UserRole.BackOffice, UserRole.CareManager, UserRole.CareCoordinator)) return true;

        if (user.IsCareWorker && user.CareWorkerId is Guid workerId)
            return await db.Visits.AsNoTracking().AnyAsync(visit =>
                visit.ServiceUserId == serviceUserId && visit.CareWorkerId == workerId &&
                visit.OrganizationId == tenant.OrganizationId, cancellationToken);

        if (user.IsFamilyMember && user.FamilyMemberId is Guid familyMemberId)
            return await db.FamilyMembers.AsNoTracking().AnyAsync(member =>
                member.Id == familyMemberId && member.ServiceUserId == serviceUserId &&
                member.OrganizationId == tenant.OrganizationId && member.Status == "Active", cancellationToken);

        return false;
    }

    public async Task<bool> CanWriteServiceUserAsync(Guid serviceUserId, CancellationToken cancellationToken = default)
    {
        if (!user.HasAnyRole(UserRole.Administrator, UserRole.CareManager, UserRole.CareCoordinator)) return false;
        var person = await db.ServiceUsers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == serviceUserId, cancellationToken);
        return person is not null && tenant.CanAccess(person.OrganizationId, person.BranchId);
    }

    public async Task<bool> CanReadVisitAsync(Guid visitId, CancellationToken cancellationToken = default)
    {
        var visit = await db.Visits.AsNoTracking().SingleOrDefaultAsync(item => item.Id == visitId, cancellationToken);
        if (visit is null || !tenant.CanAccess(visit.OrganizationId, visit.BranchId)) return false;
        if (user.HasAnyRole(UserRole.Administrator, UserRole.BackOffice, UserRole.CareManager, UserRole.CareCoordinator)) return true;
        if (user.IsCareWorker) return user.CareWorkerId == visit.CareWorkerId;
        return await CanReadServiceUserAsync(visit.ServiceUserId, cancellationToken);
    }
}
