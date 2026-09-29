namespace AiCare.Application.Email;

public sealed record EmailJobV1(
    int Version,
    Guid JobId,
    string IdempotencyKey,
    Guid TenantId,
    string EmailType,
    string RecipientName,
    string RecipientEmail,
    string ActivationUrl,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset CreatedAtUtc,
    string CorrelationId,
    string? Role = null);

public sealed record FamilyInvitationEmailRequest(
    Guid InvitationId,
    Guid TenantId,
    string RecipientName,
    string RecipientEmail,
    string ActivationUrl,
    DateTimeOffset ExpiresAtUtc);

public sealed record AccountInvitationEmailRequest(Guid InvitationId, Guid TenantId, string RecipientName, string RecipientEmail, string ActivationUrl, DateTimeOffset ExpiresAtUtc, string Role);

public interface IEmailDeliveryStore
{
    Task<bool> TryStartAsync(EmailJobV1 job, CancellationToken cancellationToken);
    Task MarkAcceptedAsync(Guid jobId, string providerMessageId, CancellationToken cancellationToken);
    Task MarkFailedAsync(Guid jobId, string sanitizedError, CancellationToken cancellationToken);
    Task ApplyProviderEventAsync(string eventId, string providerMessageId, string eventType, DateTimeOffset occurredAt, CancellationToken cancellationToken);
}

public interface IResendWebhookProcessor
{
    Task ProcessAsync(string rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken);
}
