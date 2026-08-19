using AiCare.Application;
using AiCare.Application.CarePlans;
using AiCare.Application.FamilyPortal;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiCare.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddScoped<DocumentStorageCleanupInterceptor>();
        services.AddDbContext<CareDbContext>((serviceProvider, options) =>
            options
                .UseNpgsql(connectionString)
                .AddInterceptors(serviceProvider.GetRequiredService<DocumentStorageCleanupInterceptor>()));
        services.AddHostedService<ProductionConfigurationValidationService>();
        services.AddHostedService<RenderTestPatientSeeder>();
        services.AddSingleton<IDocumentMalwareScanner, BasicDocumentMalwareScanner>();
        services.AddSingleton<IProductionAlertSink, WebhookProductionAlertSink>();
        services.AddSingleton<IStartupFilter, ApiSecurityHardeningStartupFilter>();
        services.AddSingleton<IStartupFilter, ProductionMonitoringStartupFilter>();
        services.AddSingleton<IStartupFilter, DocumentUploadSecurityStartupFilter>();

        services.AddScoped<ICareRepository, EfCoreCareRepository>();
        services.AddScoped<ICarePlanLifecycleStore, CarePlanLifecycleStore>();
        services.AddScoped<ICarePlanLifecycleService, CarePlanLifecycleService>();
        services.AddScoped<IFamilyPortalStore, FamilyPortalStore>();
        services.AddScoped<IFamilyPortalService, FamilyPortalService>();
        services.AddScoped<IFamilyPortalQueryStore, FamilyPortalQueryStore>();
        services.AddScoped<IFamilyPortalQueryService, FamilyPortalQueryService>();
        services.AddSingleton<IFamilyInvitationEmailSender, SmtpFamilyInvitationEmailSender>();
        return services;
    }
}
