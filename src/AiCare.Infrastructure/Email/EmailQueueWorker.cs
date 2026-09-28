using System.Text.Json;
using AiCare.Application.Email;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiCare.Infrastructure.Email;

public sealed class EmailQueueWorker(
    IAmazonSQS sqs,
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<EmailQueueWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue<bool>("EmailQueue:Enabled"))
        {
            logger.LogInformation("Email queue worker is disabled.");
            return;
        }

        var queueUrl = configuration["EmailQueue:QueueUrl"];
        if (string.IsNullOrWhiteSpace(queueUrl))
            throw new InvalidOperationException("EmailQueue:QueueUrl must be configured when the email queue is enabled.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 1,
                    WaitTimeSeconds = Math.Clamp(configuration.GetValue<int?>("EmailQueue:WaitTimeSeconds") ?? 20, 1, 20),
                    VisibilityTimeout = Math.Clamp(configuration.GetValue<int?>("EmailQueue:VisibilityTimeoutSeconds") ?? 120, 30, 43200),
                    MessageSystemAttributeNames = new List<string> { "ApproximateReceiveCount" }
                }, stoppingToken);
                foreach (var message in response.Messages)
                    await ProcessAsync(queueUrl, message, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Email queue poll failed.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ProcessAsync(string queueUrl, Message message, CancellationToken cancellationToken)
    {
        EmailJobV1? job = null;
        try
        {
            job = JsonSerializer.Deserialize<EmailJobV1>(message.Body, JsonOptions);
            Validate(job);

            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IEmailDeliveryStore>();
            if (!await store.TryStartAsync(job!, cancellationToken))
            {
                await DeleteAsync(queueUrl, message, cancellationToken);
                return;
            }

            var resend = scope.ServiceProvider.GetRequiredService<IResendEmailClient>();
            var providerId = await resend.SendAsync(job!, cancellationToken);
            await store.MarkAcceptedAsync(job!.JobId, providerId, cancellationToken);
            await DeleteAsync(queueUrl, message, cancellationToken);
            logger.LogInformation("Resend accepted email job {JobId} as {ProviderMessageId}.", job.JobId, providerId);
        }
        catch (Exception exception)
        {
            if (job is not null)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<IEmailDeliveryStore>()
                        .MarkFailedAsync(job.JobId, exception.Message, cancellationToken);
                }
                catch (Exception trackingException)
                {
                    logger.LogError(trackingException, "Could not record failure for email job {JobId}.", job.JobId);
                }
            }
            logger.LogWarning(exception, "Email message {SqsMessageId} failed and will remain in SQS for retry.", message.MessageId);
        }
    }

    private Task DeleteAsync(string queueUrl, Message message, CancellationToken cancellationToken) =>
        sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, cancellationToken);

    private static void Validate(EmailJobV1? job)
    {
        if (job is null || job.Version != 1 || job.JobId == Guid.Empty || job.TenantId == Guid.Empty ||
            job.EmailType != "family-invitation" || string.IsNullOrWhiteSpace(job.IdempotencyKey) ||
            string.IsNullOrWhiteSpace(job.RecipientEmail) || string.IsNullOrWhiteSpace(job.ActivationUrl))
            throw new InvalidOperationException("SQS email job is invalid or uses an unsupported schema version.");
    }
}
