using System.Security.Cryptography;
using System.Text;

namespace AiCare.Application.FamilyPortal;

public static class FamilyPermissions
{
    public const string ViewCareSummary = "ViewCareSummary";
    public const string ViewTimeline = "ViewTimeline";
    public const string ViewVisits = "ViewVisits";
    public const string ViewAppointments = "ViewAppointments";
    public const string ViewCarePlan = "ViewCarePlan";
    public const string SignCarePlan = "SignCarePlan";
    public const string ViewDocuments = "ViewDocuments";
    public const string ViewMedicationSummary = "ViewMedicationSummary";
    public const string ViewIncidentSummary = "ViewIncidentSummary";
    public const string MessageCareTeam = "MessageCareTeam";
    public const string SubmitFeedback = "SubmitFeedback";
    public const string ViewFinance = "ViewFinance";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        ViewCareSummary, ViewTimeline, ViewVisits, ViewAppointments, ViewCarePlan, SignCarePlan,
        ViewDocuments, ViewMedicationSummary, ViewIncidentSummary, MessageCareTeam, SubmitFeedback, ViewFinance
    };
}

public sealed record FamilyAccessSnapshot(
    Guid FamilyMemberId,
    Guid ServiceUserId,
    string FullName,
    string Email,
    string Relationship,
    string AuthorityType,
    string VerificationStatus,
    string VerificationReference,
    string AccessStatus,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidUntil,
    long Revision,
    IReadOnlyCollection<string> Permissions,
    string InvitationStatus,
    DateTimeOffset? InvitationExpiresAt);

public sealed record FamilyInvitationResult(Guid InvitationId, string Status, DateTimeOffset ExpiresAt, string? DevelopmentActivationUrl);
public sealed record FamilyInvitationValidation(bool Valid, string Status, string Message, string? FullName, string? ProviderName, DateTimeOffset? ExpiresAt);
public sealed record FamilyPortalPerson(Guid ServiceUserId, string FullName, string Relationship, IReadOnlyCollection<string> Permissions);
public sealed record FamilyFeedbackInput(Guid ServiceUserId, string Type, string Subject, string Description, string Priority);
public sealed record FamilyFeedbackResult(Guid Id, string Status, DateTimeOffset SubmittedAt);

public sealed record ConfigureFamilyAccessCommand(
    Guid FamilyMemberId,
    string AuthorityType,
    string VerificationStatus,
    string VerificationReference,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidUntil,
    IReadOnlyCollection<string> Permissions,
    long? ExpectedRevision);

public interface IFamilyPortalStore
{
    Task<FamilyAccessSnapshot?> GetAccessAsync(Guid organizationId, Guid familyMemberId, CancellationToken cancellationToken);
    Task<FamilyAccessSnapshot> ConfigureAccessAsync(Guid organizationId, Guid? branchId, Guid actorUserId, string actorName, ConfigureFamilyAccessCommand command, CancellationToken cancellationToken);
    Task<FamilyInvitationResult> CreateInvitationAsync(Guid organizationId, Guid? branchId, Guid actorUserId, string actorName, Guid familyMemberId, string tokenHash, DateTimeOffset expiresAt, CancellationToken cancellationToken);
    Task<FamilyInvitationValidation> ValidateInvitationAsync(string tokenHash, DateTimeOffset now, CancellationToken cancellationToken);
    Task AcceptInvitationAsync(string tokenHash, string password, DateTimeOffset now, CancellationToken cancellationToken);
    Task SetAccessStatusAsync(Guid organizationId, Guid actorUserId, string actorName, Guid familyMemberId, string status, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<FamilyPortalPerson>> GetAuthorizedPeopleAsync(Guid organizationId, Guid familyMemberId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> HasPermissionAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, string permission, DateTimeOffset now, CancellationToken cancellationToken);
    Task<FamilyFeedbackResult> SubmitFeedbackAsync(Guid organizationId, Guid? branchId, Guid familyMemberId, FamilyFeedbackInput input, CancellationToken cancellationToken);
}

public interface IFamilyInvitationEmailSender
{
    Task SendInvitationAsync(string recipientName, string recipientEmail, string activationUrl, DateTimeOffset expiresAt, CancellationToken cancellationToken);
}

public sealed class DevelopmentFamilyInvitationEmailSender : IFamilyInvitationEmailSender
{
    public Task SendInvitationAsync(string recipientName, string recipientEmail, string activationUrl, DateTimeOffset expiresAt, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public interface IFamilyPortalService
{
    Task<FamilyAccessSnapshot?> GetAccessAsync(Guid organizationId, Guid familyMemberId, CancellationToken cancellationToken);
    Task<FamilyAccessSnapshot> ConfigureAccessAsync(Guid organizationId, Guid? branchId, Guid actorUserId, string actorName, ConfigureFamilyAccessCommand command, CancellationToken cancellationToken);
    Task<FamilyInvitationResult> InviteAsync(Guid organizationId, Guid? branchId, Guid actorUserId, string actorName, Guid familyMemberId, string frontendBaseUrl, CancellationToken cancellationToken);
    Task<FamilyInvitationValidation> ValidateInvitationAsync(string token, CancellationToken cancellationToken);
    Task AcceptInvitationAsync(string token, string password, bool acceptTerms, CancellationToken cancellationToken);
    Task SetAccessStatusAsync(Guid organizationId, Guid actorUserId, string actorName, Guid familyMemberId, string status, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<FamilyPortalPerson>> GetAuthorizedPeopleAsync(Guid organizationId, Guid familyMemberId, CancellationToken cancellationToken);
    Task EnsurePermissionAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, string permission, CancellationToken cancellationToken);
    Task<FamilyFeedbackResult> SubmitFeedbackAsync(Guid organizationId, Guid? branchId, Guid familyMemberId, FamilyFeedbackInput input, CancellationToken cancellationToken);
}

public sealed class FamilyPortalService : IFamilyPortalService
{
    private readonly IFamilyPortalStore _store;
    private readonly IFamilyInvitationEmailSender _emailSender;

    public FamilyPortalService(IFamilyPortalStore store, IFamilyInvitationEmailSender emailSender)
    {
        _store = store;
        _emailSender = emailSender;
    }

    public Task<FamilyAccessSnapshot?> GetAccessAsync(Guid organizationId, Guid familyMemberId, CancellationToken cancellationToken)
        => _store.GetAccessAsync(organizationId, familyMemberId, cancellationToken);

    public Task<FamilyAccessSnapshot> ConfigureAccessAsync(Guid organizationId, Guid? branchId, Guid actorUserId, string actorName, ConfigureFamilyAccessCommand command, CancellationToken cancellationToken)
    {
        if (command.FamilyMemberId == Guid.Empty) throw new InvalidOperationException("Family member is required.");
        if (string.IsNullOrWhiteSpace(command.AuthorityType)) throw new InvalidOperationException("Authority type is required.");
        if (command.VerificationStatus is not ("Pending" or "Verified" or "Rejected" or "Expired" or "Revoked"))
            throw new InvalidOperationException("Unsupported authority verification status.");
        if (command.VerificationStatus == "Verified" && string.IsNullOrWhiteSpace(command.VerificationReference))
            throw new InvalidOperationException("A verification reference or evidence note is required before family authority can be verified.");
        if (command.ValidUntil is not null && command.ValidFrom is not null && command.ValidUntil <= command.ValidFrom)
            throw new InvalidOperationException("Access expiry must be later than the access start date.");

        var invalid = command.Permissions.Where(permission => !FamilyPermissions.All.Contains(permission)).Distinct().ToArray();
        if (invalid.Length > 0) throw new InvalidOperationException($"Unsupported family permission: {string.Join(", ", invalid)}.");
        return _store.ConfigureAccessAsync(organizationId, branchId, actorUserId, actorName, command with
        {
            AuthorityType = command.AuthorityType.Trim(),
            VerificationReference = command.VerificationReference.Trim()
        }, cancellationToken);
    }

    public async Task<FamilyInvitationResult> InviteAsync(Guid organizationId, Guid? branchId, Guid actorUserId, string actorName, Guid familyMemberId, string frontendBaseUrl, CancellationToken cancellationToken)
    {
        var access = await _store.GetAccessAsync(organizationId, familyMemberId, cancellationToken)
            ?? throw new InvalidOperationException("Family access must be configured before an invitation can be sent.");
        if (!string.Equals(access.VerificationStatus, "Verified", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Family authority must be verified before an invitation can be sent.");
        if (access.AccessStatus is "Revoked" or "Expired")
            throw new InvalidOperationException("Revoked or expired family access cannot be invited.");
        if (string.IsNullOrWhiteSpace(access.Email)) throw new InvalidOperationException("Family member email is required.");

        var rawToken = Base64Url(RandomNumberGenerator.GetBytes(32));
        var tokenHash = HashToken(rawToken);
        var expiresAt = DateTimeOffset.UtcNow.AddHours(72);
        var invitation = await _store.CreateInvitationAsync(organizationId, branchId, actorUserId, actorName, familyMemberId, tokenHash, expiresAt, cancellationToken);
        var activationUrl = $"{frontendBaseUrl.TrimEnd('/')}/family/activate?token={Uri.EscapeDataString(rawToken)}";
        await _emailSender.SendInvitationAsync(access.FullName, access.Email, activationUrl, expiresAt, cancellationToken);
        return invitation with { DevelopmentActivationUrl = activationUrl };
    }

    public Task<FamilyInvitationValidation> ValidateInvitationAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Task.FromResult(new FamilyInvitationValidation(false, "Invalid", "Invitation token is required.", null, null, null));
        return _store.ValidateInvitationAsync(HashToken(token), DateTimeOffset.UtcNow, cancellationToken);
    }

    public Task AcceptInvitationAsync(string token, string password, bool acceptTerms, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Invitation token is required.");
        if (!acceptTerms) throw new InvalidOperationException("Family Portal terms must be accepted before the account can be activated.");
        ValidatePassword(password);
        return _store.AcceptInvitationAsync(HashToken(token), password, DateTimeOffset.UtcNow, cancellationToken);
    }

    public Task SetAccessStatusAsync(Guid organizationId, Guid actorUserId, string actorName, Guid familyMemberId, string status, CancellationToken cancellationToken)
    {
        if (status is not ("Active" or "Suspended" or "Revoked")) throw new InvalidOperationException("Unsupported family access status.");
        return _store.SetAccessStatusAsync(organizationId, actorUserId, actorName, familyMemberId, status, cancellationToken);
    }

    public Task<IReadOnlyCollection<FamilyPortalPerson>> GetAuthorizedPeopleAsync(Guid organizationId, Guid familyMemberId, CancellationToken cancellationToken)
        => _store.GetAuthorizedPeopleAsync(organizationId, familyMemberId, DateTimeOffset.UtcNow, cancellationToken);

    public async Task EnsurePermissionAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, string permission, CancellationToken cancellationToken)
    {
        if (!FamilyPermissions.All.Contains(permission)) throw new InvalidOperationException("Unsupported family permission.");
        if (!await _store.HasPermissionAsync(organizationId, familyMemberId, serviceUserId, permission, DateTimeOffset.UtcNow, cancellationToken))
            throw new UnauthorizedAccessException("Family access is not authorized for this person or operation.");
    }

    public Task<FamilyFeedbackResult> SubmitFeedbackAsync(Guid organizationId, Guid? branchId, Guid familyMemberId, FamilyFeedbackInput input, CancellationToken cancellationToken)
    {
        if (input.ServiceUserId == Guid.Empty || string.IsNullOrWhiteSpace(input.Subject) || string.IsNullOrWhiteSpace(input.Description))
            throw new InvalidOperationException("Person, subject, and description are required.");
        if (input.Type is not ("Feedback" or "Compliment" or "Concern" or "Complaint" or "Suggestion"))
            throw new InvalidOperationException("Unsupported feedback type.");
        if (input.Priority is not ("Routine" or "Medium" or "High"))
            throw new InvalidOperationException("Unsupported feedback priority.");
        return _store.SubmitFeedbackAsync(organizationId, branchId, familyMemberId, input with
        {
            Subject = input.Subject.Trim(),
            Description = input.Description.Trim()
        }, cancellationToken);
    }

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 12 || !password.Any(char.IsUpper) || !password.Any(char.IsLower) || !password.Any(char.IsDigit))
            throw new InvalidOperationException("Password must be at least 12 characters and include upper-case, lower-case, and numeric characters.");
    }
}
