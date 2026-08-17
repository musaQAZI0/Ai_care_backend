namespace AiCare.Domain;

public sealed record PersonContact(
    Guid Id,
    Guid ServiceUserId,
    string ContactType,
    string FullName,
    string Relationship,
    string PhoneNumber,
    string Email,
    string Address,
    bool IsPrimary,
    bool IsEmergency,
    string Notes,
    DateTimeOffset CreatedAt,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record ConsentRecord(
    Guid Id,
    Guid ServiceUserId,
    string Scope,
    string Status,
    string CapacityStatus,
    string LegalBasis,
    string DecisionMaker,
    string RecordedBy,
    DateTimeOffset RecordedAt,
    DateTimeOffset? ReviewDueAt,
    DateTimeOffset? WithdrawnAt,
    string Notes,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record FundingArrangement(
    Guid Id,
    Guid ServiceUserId,
    string FundingSource,
    string Commissioner,
    string CarePackage,
    decimal AuthorizedHoursPerWeek,
    decimal RatePerHour,
    DateOnly StartDate,
    DateOnly? EndDate,
    string Status,
    string Notes,
    DateTimeOffset CreatedAt,
    Guid? OrganizationId = null,
    Guid? BranchId = null);
