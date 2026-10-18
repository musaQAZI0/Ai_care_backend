namespace AiCare.Application.Accounting;
public interface IAccountingProvider
{
 string Name{get;}
 Task<AccountingSyncResult> SyncContactAsync(AccountingSyncCommand command,CancellationToken token);
 Task<AccountingSyncResult> SyncInvoiceAsync(AccountingSyncCommand command,CancellationToken token);
 Task<AccountingSyncResult> SyncCreditNoteAsync(AccountingSyncCommand command,CancellationToken token);
 Task<AccountingSyncResult> SyncPaymentAsync(AccountingSyncCommand command,CancellationToken token);
}
public sealed record AccountingSyncCommand(Guid OrganizationId,Guid EntityId,string IdempotencyKey,string CredentialReference,string ExternalTenantId);
public sealed record AccountingSyncResult(string ExternalId,string Status);
