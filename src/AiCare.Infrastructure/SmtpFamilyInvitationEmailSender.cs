using System.Net;
using System.Net.Mail;
using System.Net.Security;
using AiCare.Application.FamilyPortal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiCare.Infrastructure;

public sealed class SmtpFamilyInvitationEmailSender(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<SmtpFamilyInvitationEmailSender> logger) : IFamilyInvitationEmailSender
{
    public async Task SendInvitationAsync(
        string recipientName,
        string recipientEmail,
        string activationUrl,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        if (!environment.IsProduction() && !configuration.GetValue<bool>("Email:Enabled"))
        {
            logger.LogInformation("Family invitation email suppressed outside Production because Email:Enabled is false.");
            return;
        }

        var host = Required("Email:SmtpHost");
        var port = configuration.GetValue<int?>("Email:SmtpPort") ?? 587;
        var username = Required("Email:Username");
        var password = Required("Email:Password");
        var fromAddress = configuration["Email:FromAddress"] ?? username;
        var fromName = configuration["Email:FromName"] ?? "AiCare";
        var enableSsl = configuration.GetValue<bool?>("Email:EnableSsl") ?? true;

        using var message = new MailMessage
        {
            From = new MailAddress(fromAddress, fromName),
            Subject = "Activate your AiCare Family Portal account",
            Body = BuildBody(recipientName, activationUrl, expiresAt),
            IsBodyHtml = false
        };
        message.To.Add(new MailAddress(recipientEmail, recipientName));

#pragma warning disable SYSLIB0014
        using var client = new SmtpClient(host, port)
#pragma warning restore SYSLIB0014
        {
            EnableSsl = enableSsl,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(username, password),
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 30_000
        };

        cancellationToken.ThrowIfCancellationRequested();
        await client.SendMailAsync(message, cancellationToken);
        logger.LogInformation("Family invitation email sent to {RecipientDomain} via configured SMTP relay.", DomainOnly(recipientEmail));
    }

    private string Required(string key)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{key} must be configured before family invitation email can be sent.");
        return value;
    }

    private static string BuildBody(string recipientName, string activationUrl, DateTimeOffset expiresAt) =>
        $"Hello {recipientName},\n\n" +
        "You have been invited to the AiCare Family Portal. Use the secure link below to activate your account:\n\n" +
        $"{activationUrl}\n\n" +
        $"This invitation expires at {expiresAt:yyyy-MM-dd HH:mm 'UTC'}.\n\n" +
        "For your privacy, care details are not included in this email. Sign in to AiCare to view authorized information.\n\n" +
        "If you were not expecting this invitation, you can ignore this email.";

    private static string DomainOnly(string email)
    {
        var index = email.LastIndexOf('@');
        return index >= 0 ? email[index..] : "unknown-domain";
    }
}
