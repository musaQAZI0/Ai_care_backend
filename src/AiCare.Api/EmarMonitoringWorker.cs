using Microsoft.Extensions.Hosting;

namespace AiCare.Api;

internal sealed class EmarMonitoringWorker(IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<EmarMonitoringWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (configuration.GetValue<bool>("MedicationSafety:EmarProductionEnabled"))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var result = await scope.ServiceProvider.GetRequiredService<EmarMonitoringService>().ScanAsync(stoppingToken);
                    if (result.OverdueDoses + result.OverduePrnReviews + result.MedicationAlerts > 0)
                        logger.LogInformation("eMAR monitoring created {Doses} dose alerts and {Medications} medication alerts; escalated {Prn} PRN reviews",
                            result.OverdueDoses,result.MedicationAlerts,result.OverduePrnReviews);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error) { logger.LogError(error,"eMAR monitoring cycle failed"); }
            }
            try { await Task.Delay(TimeSpan.FromMinutes(1),stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
