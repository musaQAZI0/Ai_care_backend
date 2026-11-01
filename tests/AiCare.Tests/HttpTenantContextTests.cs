using System.Security.Claims;
using AiCare.Api;
using AiCare.Domain;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AiCare.Tests;

public sealed class HttpTenantContextTests
{
    [Fact]
    public void MissingOrganizationClaimCannotAccessDefaultOrganization()
    {
        var context = TenantContext(UserRole.CareManager, null, TenantDefaults.BranchId);

        Assert.Equal(Guid.Empty, context.OrganizationId);
        Assert.False(context.CanAccess(TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        Assert.False(TenantContext(UserRole.BackOffice, null, null)
            .CanAccess(TenantDefaults.OrganizationId, TenantDefaults.BranchId));
    }

    [Fact]
    public void BranchScopedUserNeedsAValidBranchClaim()
    {
        var noBranch = TenantContext(UserRole.CareWorker, TenantDefaults.OrganizationId, null);
        var assignedBranch = TenantContext(UserRole.CareWorker, TenantDefaults.OrganizationId, TenantDefaults.BranchId);
        var emptyBranch = TenantContext(UserRole.CareWorker, TenantDefaults.OrganizationId, Guid.Empty);

        Assert.False(noBranch.CanAccess(TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        Assert.True(assignedBranch.CanAccess(TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        Assert.False(assignedBranch.CanAccess(Guid.NewGuid(), TenantDefaults.BranchId));
        Assert.False(emptyBranch.CanAccess(TenantDefaults.OrganizationId, TenantDefaults.BranchId));
    }

    [Fact]
    public void BranchAssignedBackOfficeCannotBypassTenantOrBranchScope()
    {
        var organization = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var scoped = TenantContext(UserRole.BackOffice, organization, branch);
        Assert.False(scoped.IsPlatformOwner);
        Assert.False(scoped.IsOrganizationWide);
        Assert.True(scoped.CanAccess(organization, branch));
        Assert.False(scoped.CanAccess(organization, Guid.NewGuid()));
        Assert.False(scoped.CanAccess(Guid.NewGuid(), branch));
        Assert.True(TenantContext(UserRole.BackOffice, organization, null).IsPlatformOwner);
    }

    private static HttpTenantContext TenantContext(UserRole role, Guid? organization, Guid? branch)
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, role.ToString()) };
        if (organization is Guid organizationId) claims.Add(new Claim("organization_id", organizationId.ToString()));
        if (branch is Guid branchId) claims.Add(new Claim("branch_id", branchId.ToString()));
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        return new HttpTenantContext(new FixedHttpContextAccessor { HttpContext = httpContext });
    }

    private sealed class FixedHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
