using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AiCare.Application;
using AiCare.Domain;

namespace AiCare.Api;

public sealed class HttpCurrentUserContext : ICurrentUserContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpCurrentUserContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? UserId
    {
        get
        {
            var subject = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(subject, out var parsed) ? parsed : null;
        }
    }

    public string UserName => User.FindFirstValue(JwtRegisteredClaimNames.UniqueName) ?? User.Identity?.Name ?? string.Empty;

    public UserRole? Role
    {
        get
        {
            var role = User.FindFirstValue(ClaimTypes.Role);
            return Enum.TryParse<UserRole>(role, out var parsed) ? parsed : null;
        }
    }

    public bool IsAdministrator => HasAnyRole(UserRole.Administrator);

    public bool IsCareManager => HasAnyRole(UserRole.CareManager);

    public bool IsCareCoordinator => HasAnyRole(UserRole.CareCoordinator);

    public bool IsCareWorker => HasAnyRole(UserRole.CareWorker);

    public bool IsFamilyMember => HasAnyRole(UserRole.FamilyMember);

    public bool IsBackOffice => HasAnyRole(UserRole.BackOffice);

    public bool HasAnyRole(params UserRole[] roles)
    {
        var role = Role;
        return role is not null && roles.Contains(role.Value);
    }

    private ClaimsPrincipal User => _httpContextAccessor.HttpContext?.User ?? new ClaimsPrincipal();
}
