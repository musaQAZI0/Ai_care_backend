using System.Data.Common;
using AiCare.Application.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AiCare.Infrastructure.Email;

public sealed class EmailDeliveryStore(CareDbContext db) : IEmailDeliveryStore
{
    public async Task<bool> TryStartAsync(EmailJobV1 job, CancellationToken cancellationToken)
    {
        await using var command = await CommandAsync("""
            insert into email_deliveries
                (id, tenant_id, job_id, idempotency_key, email_type, recipient, status, attempt_count, created_at, updated_at)
            values
                (@id, @tenant, @job, @key, @type, @recipient, 'Processing', 1, now(), now())
            on conflict (idempotency_key) do update
                set status = 'Processing',
                    attempt_count = email_deliveries.attempt_count + 1,
                    updated_at = now()
            where email_deliveries.status in ('Retryable', 'Failed')
               or (email_deliveries.status = 'Processing' and email_deliveries.updated_at < now() - interval '90 seconds')
            returning job_id
            """, cancellationToken);
        Add(command, "id", Guid.NewGuid());
        Add(command, "tenant", job.TenantId);
        Add(command, "job", job.JobId);
        Add(command, "key", job.IdempotencyKey);
        Add(command, "type", job.EmailType);
        Add(command, "recipient", job.RecipientEmail);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public Task MarkAcceptedAsync(Guid jobId, string providerMessageId, CancellationToken cancellationToken) =>
        ExecuteAsync("""
            update email_deliveries
            set status='Accepted', provider_message_id=@provider, accepted_at=now(), last_error='', updated_at=now()
            where job_id=@job
            """, cancellationToken, ("provider", providerMessageId), ("job", jobId));

    public Task MarkFailedAsync(Guid jobId, string sanitizedError, CancellationToken cancellationToken) =>
        ExecuteAsync("""
            update email_deliveries
            set status='Retryable', last_error=@error, failed_at=now(), updated_at=now()
            where job_id=@job
            """, cancellationToken, ("error", Sanitize(sanitizedError)), ("job", jobId));

    public async Task ApplyProviderEventAsync(string eventId, string providerMessageId, string eventType, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await using var insert = await CommandAsync("""
            insert into email_webhook_events(id, provider_event_id, provider_message_id, event_type, occurred_at, processed_at)
            values(@id, @event, @provider, @type, @occurred, now())
            on conflict(provider_event_id) do nothing
            returning id
            """, cancellationToken);
        Add(insert, "id", Guid.NewGuid());
        Add(insert, "event", eventId);
        Add(insert, "provider", providerMessageId);
        Add(insert, "type", eventType);
        Add(insert, "occurred", occurredAt);
        if (await insert.ExecuteScalarAsync(cancellationToken) is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        var status = eventType switch
        {
            "email.sent" => "Accepted",
            "email.delivered" => "Delivered",
            "email.delivery_delayed" => "DeliveryDelayed",
            "email.bounced" => "Bounced",
            "email.complained" => "Complained",
            "email.failed" => "Failed",
            "email.suppressed" => "Suppressed",
            _ => null
        };
        if (status is not null)
        {
            await ExecuteAsync("""
                update email_deliveries
                set status=@status,
                    delivered_at=case when @status='Delivered' then @occurred else delivered_at end,
                    bounced_at=case when @status='Bounced' then @occurred else bounced_at end,
                    failed_at=case when @status in ('Failed','Suppressed','Complained') then @occurred else failed_at end,
                    updated_at=now()
                where provider_message_id=@provider
                """, cancellationToken,
                ("status", status), ("occurred", occurredAt), ("provider", providerMessageId));
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken, params (string Name, object Value)[] values)
    {
        await using var command = await CommandAsync(sql, cancellationToken);
        foreach (var value in values) Add(command, value.Name, value.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<DbCommand> CommandAsync(string sql, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        return command;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string Sanitize(string error)
    {
        var singleLine = error.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine[..Math.Min(singleLine.Length, 500)];
    }
}
