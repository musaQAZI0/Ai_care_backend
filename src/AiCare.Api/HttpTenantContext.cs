using System.Security.Claims;
using AiCare.Application;
using AiCare.Domain;

namespace AiCare.Api;

public sealed class HttpTenantContext : ITenantContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpTenantContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid OrganizationId => TryReadGuid("organization_id") ?? Guid.Empty;

    public Guid? BranchId => TryReadGuid("branch_id");

    // A branch-assigned finance operator must retain that branch's access scope.
    public bool IsPlatformOwner => IsInRole(nameof(UserRole.BackOffice)) && BranchId is null;

    public bool IsOrganizationWide => IsPlatformOwner || IsInRole(nameof(UserRole.Administrator));

    public bool CanAccess(Guid? organizationId, Guid? branchId)
    {
        if (OrganizationId == Guid.Empty)
        {
            return false;
        }

        if (IsPlatformOwner)
        {
            return true;
        }

        if (organizationId is not null && organizationId != OrganizationId)
        {
            return false;
        }

        if (IsOrganizationWide)
        {
            return true;
        }

        return BranchId is not null && (branchId is null || branchId == BranchId);
    }

    private bool IsInRole(string role) => _httpContextAccessor.HttpContext?.User.IsInRole(role) == true;

    private Guid? TryReadGuid(string claimType)
    {
        var value = _httpContextAccessor.HttpContext?.User.FindFirstValue(claimType);
        return Guid.TryParse(value, out var parsed) && parsed != Guid.Empty ? parsed : null;
    }
}
