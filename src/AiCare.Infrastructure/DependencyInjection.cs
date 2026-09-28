using AiCare.Application;
using AiCare.Application.CarePlans;
using AiCare.Application.FamilyPortal;
using AiCare.Application.Email;
using AiCare.Infrastructure.Email;
using Amazon;
using Amazon.SQS;
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
                .UseNpgsql(connectionString, postgres => postgres.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(2),
                    errorCodesToAdd: null))
                .AddInterceptors(serviceProvider.GetRequiredService<DocumentStorageCleanupInterceptor>()));
        services.AddHostedService<ProductionConfigurationValidationService>();
        services.AddHostedService<RenderTestPatientSeeder>();
        services.AddHostedService<IntegrationJobWorker>();
        services.AddHostedService<InvoiceOverdueWorker>();
        services.AddHostedService<EmailQueueWorker>();
        services.AddSingleton<IAmazonSQS>(serviceProvider =>
        {
            var configuration = serviceProvider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
            var region = configuration["EmailQueue:Region"] ?? configuration["AWS_REGION"] ?? "eu-west-2";
            return new AmazonSQSClient(RegionEndpoint.GetBySystemName(region));
        });
        services.AddHttpClient<IResendEmailClient, ResendEmailClient>(client =>
            client.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<IEmailDeliveryStore, EmailDeliveryStore>();
        services.AddScoped<IResendWebhookProcessor, ResendWebhookProcessor>();
        services.AddSingleton<IDocumentMalwareScanner, BasicDocumentMalwareScanner>();
        services.AddSingleton<IProductionAlertSink, WebhookProductionAlertSink>();
        services.AddSingleton<IStartupFilter, ApiSecurityHardeningStartupFilter>();
        services.AddSingleton<IStartupFilter, ProductionMonitoringStartupFilter>();
        services.AddSingleton<IStartupFilter, DocumentUploadSecurityStartupFilter>();

        services.AddScoped<ICareRepository, EfCoreCareRepository>();
        services.AddScoped<IContextualAuthorization, ContextualAuthorizationService>();
        services.AddScoped<ICarePlanLifecycleStore, CarePlanLifecycleStore>();
        services.AddScoped<ICarePlanLifecycleService, CarePlanLifecycleService>();
        services.AddScoped<IFamilyPortalStore, FamilyPortalStore>();
        services.AddScoped<IFamilyPortalService, FamilyPortalService>();
        services.AddScoped<IFamilyPortalQueryStore, FamilyPortalQueryStore>();
        services.AddScoped<IFamilyPortalQueryService, FamilyPortalQueryService>();
        services.AddSingleton<IFamilyInvitationEmailSender, SqsFamilyInvitationEmailSender>();
        return services;
    }
}
