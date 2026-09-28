using System.Text.Json;
using AiCare.Application.Email;
using AiCare.Application.FamilyPortal;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AiCare.Infrastructure.Email;

public sealed class SqsFamilyInvitationEmailSender(
    IAmazonSQS sqs,
    IConfiguration configuration,
    ILogger<SqsFamilyInvitationEmailSender> logger) : IFamilyInvitationEmailSender
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task SendInvitationAsync(FamilyInvitationEmailRequest request, CancellationToken cancellationToken)
    {
        if (!configuration.GetValue<bool>("EmailQueue:Enabled"))
        {
            logger.LogWarning("Family invitation email queue is disabled.");
            return;
        }

        var queueUrl = configuration["EmailQueue:QueueUrl"];
        if (string.IsNullOrWhiteSpace(queueUrl))
            throw new InvalidOperationException("EmailQueue:QueueUrl must be configured when the email queue is enabled.");

        var job = new EmailJobV1(
            1,
            request.InvitationId,
            $"family-invitation:{request.TenantId:N}:{request.InvitationId:N}",
            request.TenantId,
            "family-invitation",
            request.RecipientName.Trim(),
            request.RecipientEmail.Trim(),
            request.ActivationUrl,
            request.ExpiresAtUtc,
            DateTimeOffset.UtcNow,
            request.InvitationId.ToString("N"));

        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = JsonSerializer.Serialize(job, JsonOptions),
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["schemaVersion"] = new() { DataType = "Number", StringValue = "1" },
                ["emailType"] = new() { DataType = "String", StringValue = job.EmailType },
                ["tenantId"] = new() { DataType = "String", StringValue = job.TenantId.ToString("N") }
            }
        }, cancellationToken);

        logger.LogInformation("Queued family invitation email job {JobId} for tenant {TenantId}.", job.JobId, job.TenantId);
    }
}
