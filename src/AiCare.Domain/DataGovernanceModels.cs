namespace AiCare.Domain;

public sealed record RetentionPolicy(
    Guid Id,
    string DataCategory,
    int RetentionDays,
    string LegalBasis,
    string DispositionAction,
    bool IsActive,
    DateTimeOffset ReviewDueAt,
    DateTimeOffset UpdatedAt,
    Guid OrganizationId,
    Guid? BranchId = null);

public sealed record DataGovernanceRequest(
    Guid Id,
    Guid ServiceUserId,
    string RequestType,
    string Status,
    string RequestedBy,
    string Reason,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    Guid OrganizationId,
    Guid? BranchId = null);
