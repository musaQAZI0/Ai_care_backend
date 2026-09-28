using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiCare.Application.Email;
using Microsoft.Extensions.Configuration;

namespace AiCare.Infrastructure.Email;

public sealed class ResendWebhookProcessor(
    IConfiguration configuration,
    IEmailDeliveryStore deliveries) : IResendWebhookProcessor
{
    public async Task ProcessAsync(string rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        var eventId = RequiredHeader(headers, "svix-id");
        var timestampText = RequiredHeader(headers, "svix-timestamp");
        var signatureHeader = RequiredHeader(headers, "svix-signature");
        if (!long.TryParse(timestampText, out var timestamp) ||
            Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp) > 300)
            throw new UnauthorizedAccessException("Resend webhook timestamp is invalid.");

        var secret = configuration["Resend:WebhookSigningSecret"];
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("Resend:WebhookSigningSecret must be configured.");
        var encodedSecret = secret.StartsWith("whsec_", StringComparison.Ordinal) ? secret[6..] : secret;
        byte[] secretBytes;
        try { secretBytes = Convert.FromBase64String(encodedSecret); }
        catch (FormatException) { throw new InvalidOperationException("Resend webhook signing secret is invalid."); }

        var signedContent = Encoding.UTF8.GetBytes($"{eventId}.{timestampText}.{rawBody}");
        var expected = HMACSHA256.HashData(secretBytes, signedContent);
        var valid = signatureHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Split(',', 2))
            .Where(parts => parts.Length == 2 && parts[0] == "v1")
            .Any(parts => TryMatch(parts[1], expected));
        if (!valid) throw new UnauthorizedAccessException("Resend webhook signature is invalid.");

        using var json = JsonDocument.Parse(rawBody);
        var root = json.RootElement;
        var eventType = root.GetProperty("type").GetString() ?? throw new InvalidOperationException("Webhook event type is missing.");
        var data = root.GetProperty("data");
        var providerMessageId =
            (data.TryGetProperty("email_id", out var emailId) ? emailId.GetString() : null) ??
            (data.TryGetProperty("id", out var id) ? id.GetString() : null) ??
            throw new InvalidOperationException("Webhook email identifier is missing.");
        var occurredAt = root.TryGetProperty("created_at", out var created) &&
                         DateTimeOffset.TryParse(created.GetString(), out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
        await deliveries.ApplyProviderEventAsync(eventId, providerMessageId, eventType, occurredAt, cancellationToken);
    }

    private static string RequiredHeader(IReadOnlyDictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new UnauthorizedAccessException($"Required webhook header {name} is missing.");

    private static bool TryMatch(string signature, byte[] expected)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(signature), expected); }
        catch (FormatException) { return false; }
    }
}
