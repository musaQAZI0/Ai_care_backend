using System.Data;
using System.Data.Common;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiCare.Api;

internal sealed class MedicationChangeActivationWorker(IServiceScopeFactory scopes, ILogger<MedicationChangeActivationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                for (var processed = 0; processed < 25 && !stoppingToken.IsCancellationRequested; processed++)
                    if (!await ActivateOneDueChange(stoppingToken)) break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Medication change activation cycle failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task<bool> ActivateOneDueChange(CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        var activation = scope.ServiceProvider.GetRequiredService<MedicationChangeActivationService>();
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var due = await ReadDue(db, token);
            if (due is null) return false;
            var error = await activation.Apply(due.Value.Change, due.Value.Organization, due.Value.Branch,
                null, "system:medication-change-activation", token);
            if (error is not null)
            {
                logger.LogWarning("Approved medication change {ChangeId} remains blocked: {Reason}", due.Value.Change.Id, error);
                return false;
            }
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            logger.LogInformation("Activated approved medication change {ChangeId}", due.Value.Change.Id);
            return true;
        });
    }

    private static async Task<(MedicationChangeRow Change, Guid Organization, Guid Branch)?> ReadDue(CareDbContext db, CancellationToken token)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = """
            select c.id,c.medication_id,c.change_type,c.status,c.baseline_profile_version,c.proposal::text,c.reason,
                   c.source_type,c.source_reference,c.requested_by_user_id,c.effective_at,c.reviewed_by_user_id,c.reviewed_by,
                   c.organization_id,c.branch_id
            from medication_change_requests c
            join medication_safety_profiles p on p.medication_id=c.medication_id
              and p.organization_id=c.organization_id and p.branch_id=c.branch_id
            where c.status='Approved' and c.effective_at<=now()
              and p.profile_version=c.baseline_profile_version and p.reconciliation_status='Verified'
            order by c.effective_at,c.id
            limit 1 for update of c skip locked
            """;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var change = new MedicationChangeRow(reader.GetGuid(0),reader.GetGuid(1),reader.GetString(2),
            reader.GetString(3),reader.GetInt32(4),reader.GetString(5),reader.GetString(6),
            reader.GetString(7),reader.GetString(8),reader.GetGuid(9),
            reader.GetFieldValue<DateTimeOffset>(10),reader.IsDBNull(11)?null:reader.GetGuid(11),reader.GetString(12));
        return (change,reader.GetGuid(13),reader.GetGuid(14));
    }
}
