using AiCare.Domain;

namespace AiCare.Application.CarePlans;

public sealed record CarePlanActor(
    Guid? UserId,
    string UserName,
    UserRole? Role,
    Guid? CareWorkerId,
    Guid? FamilyMemberId,
    Guid OrganizationId,
    Guid? BranchId);

public sealed record CarePlanLifecycleSnapshot(
    CarePlan CarePlan,
    CarePlanVersionRecord Version,
    IReadOnlyList<CarePlanSignatureRecord> Signatures,
    IReadOnlyList<CarePlanAcknowledgementRecord> Acknowledgements,
    IReadOnlyList<CarePlanLifecycleEventRecord> Events,
    bool RequiredSignaturesSatisfied);

public sealed record SignCarePlanCommand(
    CarePlanSignerType SignerType,
    string SignerName,
    string Relationship,
    string Declaration,
    CarePlanSignatureMethod SignatureMethod,
    long ExpectedRevision);

public sealed record CreateCarePlanRevisionCommand(string ChangeReason, long ExpectedRevision);

public interface ICarePlanLifecycleStore
{
    Task<CarePlanLifecycleSnapshot?> GetAsync(Guid carePlanId, Guid organizationId, Guid? branchId, CancellationToken cancellationToken);
    Task<IReadOnlyList<CarePlanVersionRecord>> GetVersionsAsync(Guid serviceUserId, Guid organizationId, Guid? branchId, CancellationToken cancellationToken);
    Task<CarePlanLifecycleSnapshot> TransitionAsync(Guid carePlanId, long expectedRevision, CarePlanLifecycleStatus targetStatus, string reason, string comment, CarePlanActor actor, CancellationToken cancellationToken);
    Task<CarePlanLifecycleSnapshot> AddSignatureAsync(Guid carePlanId, SignCarePlanCommand command, CarePlanActor actor, CancellationToken cancellationToken);
    Task<CarePlanLifecycleSnapshot> ActivateAsync(Guid carePlanId, long expectedRevision, CarePlanActor actor, CancellationToken cancellationToken);
    Task<CarePlanLifecycleSnapshot> CreateRevisionAsync(Guid carePlanId, CreateCarePlanRevisionCommand command, CarePlanActor actor, CancellationToken cancellationToken);
    Task<CarePlanLifecycleSnapshot> AcknowledgeAsync(Guid carePlanId, long expectedRevision, CarePlanActor actor, CancellationToken cancellationToken);
}

public interface ICarePlanLifecycleService
{
    Task<CarePlanLifecycleSnapshot?> GetAsync(Guid carePlanId, CarePlanActor actor, CancellationToken cancellationToken);
    Task<IReadOnlyList<CarePlanVersionRecord>> GetVersionsAsync(Guid serviceUserId, CarePlanActor actor, CancellationToken cancellationToken);
    Task<CarePlanLifecycleSnapshot> SubmitForReviewAsync(Guid carePlanId, long expectedRevision, string comment, CarePlanActor actor, CancellationToken cancellationToken);
    Task<CarePlanLifecycleSnapshot> ReturnToDraftAsync(Guid carePlanId, long expectedRevision, string reason, string comment, CarePlanActor actor, CancellationToken cancellationToken);
    Task<CarePlanLifecycleSnapshot> ApproveAsync(Guid carePlanId, long expectedRevision, string comment, CarePlanActor actor, CancellationToken cancellationToken);
    Task<CarePlanLifecycleSnapshot> SignAsync(Guid carePlanId, SignCarePlanCommand command, CarePlanActor actor, CancellationToken cancellationToken);
    Task<CarePlanLifecycleSnapshot> ActivateAsync(Guid carePlanId, long expectedRevision, CarePlanActor actor, CancellationToken cancellationToken);
    Task<CarePlanLifecycleSnapshot> CreateRevisionAsync(Guid carePlanId, CreateCarePlanRevisionCommand command, CarePlanActor actor, CancellationToken cancellationToken);
    Task<CarePlanLifecycleSnapshot> AcknowledgeAsync(Guid carePlanId, long expectedRevision, CarePlanActor actor, CancellationToken cancellationToken);
}

public sealed class CarePlanLifecycleService(ICarePlanLifecycleStore store) : ICarePlanLifecycleService
{
    public Task<CarePlanLifecycleSnapshot?> GetAsync(Guid carePlanId, CarePlanActor actor, CancellationToken cancellationToken) =>
        store.GetAsync(carePlanId, actor.OrganizationId, actor.BranchId, cancellationToken);

    public Task<IReadOnlyList<CarePlanVersionRecord>> GetVersionsAsync(Guid serviceUserId, CarePlanActor actor, CancellationToken cancellationToken) =>
        store.GetVersionsAsync(serviceUserId, actor.OrganizationId, actor.BranchId, cancellationToken);

    public Task<CarePlanLifecycleSnapshot> SubmitForReviewAsync(Guid carePlanId, long expectedRevision, string comment, CarePlanActor actor, CancellationToken cancellationToken)
    {
        RequireRole(actor, UserRole.CareCoordinator, UserRole.CareManager, UserRole.Administrator);
        return store.TransitionAsync(carePlanId, expectedRevision, CarePlanLifecycleStatus.InReview, "Submitted for review", comment?.Trim() ?? string.Empty, actor, cancellationToken);
    }

    public Task<CarePlanLifecycleSnapshot> ReturnToDraftAsync(Guid carePlanId, long expectedRevision, string reason, string comment, CarePlanActor actor, CancellationToken cancellationToken)
    {
        RequireRole(actor, UserRole.CareManager, UserRole.Administrator);
        if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("A reason is required when returning a care plan to draft.");
        return store.TransitionAsync(carePlanId, expectedRevision, CarePlanLifecycleStatus.Draft, reason.Trim(), comment?.Trim() ?? string.Empty, actor, cancellationToken);
    }

    public Task<CarePlanLifecycleSnapshot> ApproveAsync(Guid carePlanId, long expectedRevision, string comment, CarePlanActor actor, CancellationToken cancellationToken)
    {
        RequireRole(actor, UserRole.CareManager, UserRole.Administrator);
        return store.TransitionAsync(carePlanId, expectedRevision, CarePlanLifecycleStatus.Approved, "Approved", comment?.Trim() ?? string.Empty, actor, cancellationToken);
    }

    public Task<CarePlanLifecycleSnapshot> SignAsync(Guid carePlanId, SignCarePlanCommand command, CarePlanActor actor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.SignerName) || string.IsNullOrWhiteSpace(command.Declaration))
            throw new InvalidOperationException("Signer name and declaration are required.");

        switch (command.SignerType)
        {
            case CarePlanSignerType.CareManager:
                RequireRole(actor, UserRole.CareManager, UserRole.Administrator);
                break;
            case CarePlanSignerType.CareCoordinator:
                RequireRole(actor, UserRole.CareCoordinator, UserRole.CareManager, UserRole.Administrator);
                break;
            case CarePlanSignerType.Representative:
                RequireRole(actor, UserRole.FamilyMember, UserRole.CareManager, UserRole.Administrator);
                break;
            case CarePlanSignerType.ServiceUser:
                RequireRole(actor, UserRole.ServiceUser, UserRole.CareManager, UserRole.Administrator);
                break;
            default:
                throw new InvalidOperationException("Unsupported care plan signer type.");
        }

        return store.AddSignatureAsync(carePlanId, command with
        {
            SignerName = command.SignerName.Trim(),
            Relationship = command.Relationship?.Trim() ?? string.Empty,
            Declaration = command.Declaration.Trim()
        }, actor, cancellationToken);
    }

    public Task<CarePlanLifecycleSnapshot> ActivateAsync(Guid carePlanId, long expectedRevision, CarePlanActor actor, CancellationToken cancellationToken)
    {
        RequireRole(actor, UserRole.CareManager, UserRole.Administrator);
        return store.ActivateAsync(carePlanId, expectedRevision, actor, cancellationToken);
    }

    public Task<CarePlanLifecycleSnapshot> CreateRevisionAsync(Guid carePlanId, CreateCarePlanRevisionCommand command, CarePlanActor actor, CancellationToken cancellationToken)
    {
        RequireRole(actor, UserRole.CareCoordinator, UserRole.CareManager, UserRole.Administrator);
        if (string.IsNullOrWhiteSpace(command.ChangeReason)) throw new InvalidOperationException("A change reason is required when creating a new care plan version.");
        return store.CreateRevisionAsync(carePlanId, command with { ChangeReason = command.ChangeReason.Trim() }, actor, cancellationToken);
    }

    public Task<CarePlanLifecycleSnapshot> AcknowledgeAsync(Guid carePlanId, long expectedRevision, CarePlanActor actor, CancellationToken cancellationToken)
    {
        RequireRole(actor, UserRole.CareWorker);
        if (actor.CareWorkerId is null) throw new InvalidOperationException("The signed-in care worker is not linked to a worker record.");
        return store.AcknowledgeAsync(carePlanId, expectedRevision, actor, cancellationToken);
    }

    private static void RequireRole(CarePlanActor actor, params UserRole[] allowed)
    {
        if (actor.Role is null || !allowed.Contains(actor.Role.Value)) throw new UnauthorizedAccessException("You are not permitted to perform this care plan action.");
    }
}
