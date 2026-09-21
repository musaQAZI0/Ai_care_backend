using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace AiCare.Infrastructure;
public sealed class InvoiceOverdueWorker(IServiceScopeFactory scopeFactory,ILogger<InvoiceOverdueWorker> logger):BackgroundService
{
 protected override async Task ExecuteAsync(CancellationToken stoppingToken)
 {
  while(!stoppingToken.IsCancellationRequested)
  {
   try{await using var scope=scopeFactory.CreateAsyncScope();var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();var changed=await db.Database.ExecuteSqlRawAsync("update \"Invoices\" i set \"Status\"='Overdue' from finance_invoice_details d where d.invoice_id=i.\"Id\" and i.\"Status\"='Issued' and d.due_date<current_date",stoppingToken);if(changed>0)logger.LogInformation("Marked {InvoiceCount} issued invoices overdue.",changed);}
   catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}catch(Exception ex){logger.LogError(ex,"Invoice overdue processing failed.");}
   await Task.Delay(TimeSpan.FromHours(1),stoppingToken);
  }
 }
}
