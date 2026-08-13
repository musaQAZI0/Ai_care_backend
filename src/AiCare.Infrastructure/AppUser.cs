using AiCare.Domain;

namespace AiCare.Infrastructure;

public sealed record AppUser(
    Guid Id,
    string UserName,
    string Email,
    string PasswordHash,
    UserRole Role,
    bool IsActive,
    Guid? OrganizationId = null,
    Guid? BranchId = null,
    Guid? CareWorkerId = null
);
