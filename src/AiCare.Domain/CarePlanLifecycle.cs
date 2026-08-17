using System.Text.Json.Serialization;

namespace AiCare.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CarePlanLifecycleStatus
{
    Draft,
    InReview,
    Approved,
    Signed,
    Active,
    Superseded
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CarePlanSignerType
{
    ServiceUser,
    Representative,
    CareCoordinator,
    CareManager
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CarePlanSignatureMethod
{
    AuthenticatedConfirmation,
    RepresentativeConfirmation
}

public sealed record CarePlanVersionRecord(
    Guid Id,
    Guid CarePlanId,
    Guid ServiceUserId,
    int VersionNumber,
    Guid? PreviousCarePlanId,
    string ChangeReason,
    CarePlanLifecycleStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Revision,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record CarePlanSignatureRecord(
    Guid Id,
    Guid CarePlanId,
    CarePlanSignerType SignerType,
    Guid? SignerUserId,
    Guid? FamilyMemberId,
    string SignerName,
    string Relationship,
    string Declaration,
    CarePlanSignatureMethod SignatureMethod,
    DateTimeOffset SignedAt,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record CarePlanAcknowledgementRecord(
    Guid Id,
    Guid CarePlanId,
    Guid CareWorkerId,
    Guid? AcknowledgedByUserId,
    string AcknowledgedBy,
    DateTimeOffset AcknowledgedAt,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record CarePlanLifecycleEventRecord(
    Guid Id,
    Guid CarePlanId,
    CarePlanLifecycleStatus FromStatus,
    CarePlanLifecycleStatus ToStatus,
    string Reason,
    string Comment,
    Guid? PerformedByUserId,
    string PerformedBy,
    DateTimeOffset PerformedAt,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public static class CarePlanLifecyclePolicy
{
    public static bool CanTransition(CarePlanLifecycleStatus from, CarePlanLifecycleStatus to) => (from, to) switch
    {
        (CarePlanLifecycleStatus.Draft, CarePlanLifecycleStatus.InReview) => true,
        (CarePlanLifecycleStatus.InReview, CarePlanLifecycleStatus.Draft) => true,
        (CarePlanLifecycleStatus.InReview, CarePlanLifecycleStatus.Approved) => true,
        (CarePlanLifecycleStatus.Approved, CarePlanLifecycleStatus.Signed) => true,
        (CarePlanLifecycleStatus.Signed, CarePlanLifecycleStatus.Active) => true,
        (CarePlanLifecycleStatus.Active, CarePlanLifecycleStatus.Superseded) => true,
        _ => false
    };

    public static void EnsureTransition(CarePlanLifecycleStatus from, CarePlanLifecycleStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"Care plan cannot transition from {from} to {to}.");
        }
    }

    public static bool ContentIsEditable(CarePlanLifecycleStatus status) => status == CarePlanLifecycleStatus.Draft;
}
