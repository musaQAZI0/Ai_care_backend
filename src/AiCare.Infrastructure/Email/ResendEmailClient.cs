using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiCare.Application.Email;
using Microsoft.Extensions.Configuration;

namespace AiCare.Infrastructure.Email;

public interface IResendEmailClient
{
    Task<string> SendAsync(EmailJobV1 job, CancellationToken cancellationToken);
    Task<string> SendMessageAsync(ResendMessage message, CancellationToken cancellationToken);
}

public sealed record ResendAttachment(string FileName, byte[] Content);
public sealed record ResendMessage(string RecipientEmail, string Subject, string Text, string IdempotencyKey,
    string? RecipientName = null, ResendAttachment? Attachment = null);

public sealed class ResendEmailClient(HttpClient httpClient, IConfiguration configuration) : IResendEmailClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<string> SendAsync(EmailJobV1 job, CancellationToken cancellationToken)
    {
        return await SendMessageAsync(new ResendMessage(job.RecipientEmail,
            job.EmailType == "account-invitation" ? $"Activate your AiCare {job.Role} account" : "Activate your AiCare Family Portal account",
            BuildBody(job), job.IdempotencyKey, job.RecipientName), cancellationToken);
    }

    public async Task<string> SendMessageAsync(ResendMessage message, CancellationToken cancellationToken)
    {
        var apiKey = Required("Resend:ApiKey");
        var fromAddress = Required("Resend:FromAddress");
        var fromName = configuration["Resend:FromName"] ?? "AiCare";
        var replyTo = configuration["Resend:ReplyToAddress"];

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", message.IdempotencyKey);
        request.Content = JsonContent.Create(new
        {
            from = $"{fromName} <{fromAddress}>",
            to = new[] { string.IsNullOrWhiteSpace(message.RecipientName) ? message.RecipientEmail : $"{message.RecipientName} <{message.RecipientEmail}>" },
            reply_to = string.IsNullOrWhiteSpace(replyTo) ? null : replyTo,
            subject = message.Subject,
            text = message.Text,
            attachments = message.Attachment is null ? null : new[]
            {
                new { filename = message.Attachment.FileName, content = Convert.ToBase64String(message.Attachment.Content) }
            }
        }, options: JsonOptions);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode >= 500)
                throw new HttpRequestException($"Resend temporarily rejected the request with HTTP {(int)response.StatusCode}.");
            throw new InvalidOperationException($"Resend rejected the email request with HTTP {(int)response.StatusCode}.");
        }

        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("id", out var id) && !string.IsNullOrWhiteSpace(id.GetString())
            ? id.GetString()!
            : throw new InvalidOperationException("Resend accepted the request without returning a message identifier.");
    }

    private string Required(string key) =>
        !string.IsNullOrWhiteSpace(configuration[key])
            ? configuration[key]!
            : throw new InvalidOperationException($"{key} must be configured.");

    private static string BuildBody(EmailJobV1 job) => job.EmailType == "account-invitation"
        ? $"Hello {job.RecipientName},\n\nYour care provider has invited you to AiCare as {job.Role}. Create your password using this one-time secure link:\n\n{job.ActivationUrl}\n\nThis invitation expires at {job.ExpiresAtUtc:yyyy-MM-dd HH:mm 'UTC'}.\n\nNo password or care information is included in this email. If you were not expecting this invitation, ignore it and contact the provider."
        :
        $"Hello {job.RecipientName},\n\n" +
        "You have been invited to the AiCare Family Portal. Use the secure link below to activate your account:\n\n" +
        $"{job.ActivationUrl}\n\n" +
        $"This invitation expires at {job.ExpiresAtUtc:yyyy-MM-dd HH:mm 'UTC'}.\n\n" +
        "For your privacy, care details are not included in this email. Sign in to AiCare to view authorized information.\n\n" +
        "If you were not expecting this invitation, you can ignore this email.";
}
