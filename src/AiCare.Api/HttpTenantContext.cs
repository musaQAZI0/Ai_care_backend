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

    public Guid OrganizationId => TryReadGuid("organization_id") ?? TenantDefaults.OrganizationId;

    public Guid? BranchId => TryReadGuid("branch_id");

    public bool IsPlatformOwner => IsInRole(nameof(UserRole.BackOffice));

    public bool IsOrganizationWide => IsPlatformOwner || IsInRole(nameof(UserRole.Administrator));

    public bool CanAccess(Guid? organizationId, Guid? branchId)
    {
        if (IsPlatformOwner)
        {
            return true;
        }

        if (organizationId is not null && organizationId != OrganizationId)
        {
            return false;
        }

        if (IsOrganizationWide || BranchId is null)
        {
            return true;
        }

        return branchId is null || branchId == BranchId;
    }

    private bool IsInRole(string role) => _httpContextAccessor.HttpContext?.User.IsInRole(role) == true;

    private Guid? TryReadGuid(string claimType)
    {
        var value = _httpContextAccessor.HttpContext?.User.FindFirstValue(claimType);
        return Guid.TryParse(value, out var parsed) ? parsed : null;
    }
}
