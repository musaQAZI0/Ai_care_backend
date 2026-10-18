using AiCare.Application.Accounting;
namespace AiCare.Infrastructure.Accounting;
public sealed class SageAccountingProvider: IAccountingProvider
{
 public string Name=>"Sage";
 public Task<AccountingSyncResult> SyncContactAsync(AccountingSyncCommand c,CancellationToken t)=>Unavailable();
 public Task<AccountingSyncResult> SyncInvoiceAsync(AccountingSyncCommand c,CancellationToken t)=>Unavailable();
 public Task<AccountingSyncResult> SyncCreditNoteAsync(AccountingSyncCommand c,CancellationToken t)=>Unavailable();
 public Task<AccountingSyncResult> SyncPaymentAsync(AccountingSyncCommand c,CancellationToken t)=>Unavailable();
 static Task<AccountingSyncResult> Unavailable()=>throw new InvalidOperationException("Live Sage transport is not configured. Configure OAuth credentials in an external secret store and a Sage test tenant before enabling sync.");
}
