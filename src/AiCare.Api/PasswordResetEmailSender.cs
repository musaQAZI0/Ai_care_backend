using AiCare.Infrastructure.Email;

namespace AiCare.Api;

public sealed class PasswordResetEmailSender(
    IConfiguration configuration,
    IWebHostEnvironment environment,
    IResendEmailClient resend)
{
    private string? FrontendBaseUrl => !string.IsNullOrWhiteSpace(configuration["Frontend:BaseUrl"])
        ? configuration["Frontend:BaseUrl"]
        : configuration["FamilyPortal:FrontendBaseUrl"];

    public bool IsConfigured => configuration.GetValue<bool>("Email:Enabled") &&
        !string.IsNullOrWhiteSpace(configuration["Resend:ApiKey"]) &&
        !string.IsNullOrWhiteSpace(configuration["Resend:FromAddress"]) &&
        !string.IsNullOrWhiteSpace(FrontendBaseUrl);

    public async Task SendAsync(string recipientEmail, string token, CancellationToken cancellationToken)
    {
        if (!IsConfigured) throw new InvalidOperationException("Password reset email is not configured.");
        var baseUrl = FrontendBaseUrl!.TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var frontend) ||
            frontend.Scheme is not ("https" or "http") ||
            (!environment.IsDevelopment() && !environment.IsEnvironment("Testing") && frontend.Scheme != "https"))
            throw new InvalidOperationException("The frontend base URL must be an absolute HTTPS URL in production.");

        var url = $"{baseUrl}/reset-password?token={Uri.EscapeDataString(token)}";
        var body = $"A password reset was requested for your AiCare account.\n\nOpen this link to choose a new password:\n{url}\n\nThe link expires in 30 minutes. If you did not request this, you can ignore this email.";
        await resend.SendMessageAsync(new ResendMessage(recipientEmail,
            "Reset your AiCare password", body, $"password-reset:{Guid.NewGuid():N}"), cancellationToken);
    }
}
