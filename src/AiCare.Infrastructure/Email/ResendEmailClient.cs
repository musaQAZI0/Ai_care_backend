using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiCare.Application.Email;
using Microsoft.Extensions.Configuration;

namespace AiCare.Infrastructure.Email;

public interface IResendEmailClient
{
    Task<string> SendAsync(EmailJobV1 job, CancellationToken cancellationToken);
}

public sealed class ResendEmailClient(HttpClient httpClient, IConfiguration configuration) : IResendEmailClient
{
    public async Task<string> SendAsync(EmailJobV1 job, CancellationToken cancellationToken)
    {
        var apiKey = Required("Resend:ApiKey");
        var fromAddress = Required("Resend:FromAddress");
        var fromName = configuration["Resend:FromName"] ?? "AiCare";
        var replyTo = configuration["Resend:ReplyToAddress"];

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", job.IdempotencyKey);
        request.Content = JsonContent.Create(new
        {
            from = $"{fromName} <{fromAddress}>",
            to = new[] { $"{job.RecipientName} <{job.RecipientEmail}>" },
            reply_to = string.IsNullOrWhiteSpace(replyTo) ? null : replyTo,
            subject = "Activate your AiCare Family Portal account",
            text = BuildBody(job)
        });

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

    private static string BuildBody(EmailJobV1 job) =>
        $"Hello {job.RecipientName},\n\n" +
        "You have been invited to the AiCare Family Portal. Use the secure link below to activate your account:\n\n" +
        $"{job.ActivationUrl}\n\n" +
        $"This invitation expires at {job.ExpiresAtUtc:yyyy-MM-dd HH:mm 'UTC'}.\n\n" +
        "For your privacy, care details are not included in this email. Sign in to AiCare to view authorized information.\n\n" +
        "If you were not expecting this invitation, you can ignore this email.";
}
