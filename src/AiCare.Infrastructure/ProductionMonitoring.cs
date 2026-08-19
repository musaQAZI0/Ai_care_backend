using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiCare.Infrastructure;

public sealed record ProductionAlert(
    string Severity,
    string Code,
    string Message,
    string RequestId,
    string Path,
    int? StatusCode,
    long? ElapsedMilliseconds,
    DateTimeOffset OccurredAt);

public interface IProductionAlertSink
{
    Task SendAsync(ProductionAlert alert, CancellationToken cancellationToken = default);
}

public sealed class WebhookProductionAlertSink(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    ILogger<WebhookProductionAlertSink> logger) : IProductionAlertSink
{
    public async Task SendAsync(ProductionAlert alert, CancellationToken cancellationToken = default)
    {
        var webhookUrl = configuration["Monitoring:AlertWebhookUrl"];
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            logger.LogWarning(
                "Production alert {Severity} {Code}: {Message} requestId={RequestId} path={Path} status={StatusCode} elapsedMs={ElapsedMs}",
                alert.Severity,
                alert.Code,
                alert.Message,
                alert.RequestId,
                alert.Path,
                alert.StatusCode,
                alert.ElapsedMilliseconds);
            return;
        }

        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            logger.LogError("Monitoring alert webhook is invalid; alert {Code} was logged locally only.", alert.Code);
            return;
        }

        try
        {
            var client = httpClientFactory.CreateClient();
            using var response = await client.PostAsJsonAsync(uri, alert, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "Monitoring alert webhook returned {StatusCode} for alert {Code}.",
                    (int)response.StatusCode,
                    alert.Code);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Monitoring alert webhook delivery failed for alert {Code}.", alert.Code);
        }
    }
}

public static class MonitoringPolicy
{
    public static string? Classify(int statusCode, long elapsedMilliseconds, long slowRequestThresholdMilliseconds)
    {
        if (statusCode >= 500) return "http_5xx";
        if (statusCode == StatusCodes.Status429TooManyRequests) return "rate_limit_triggered";
        if (elapsedMilliseconds >= slowRequestThresholdMilliseconds) return "slow_request";
        return null;
    }

    public static long SlowRequestThreshold(IConfiguration configuration)
    {
        var configured = configuration.GetValue<long?>("Monitoring:SlowRequestThresholdMs");
        return configured is >= 250 and <= 60_000 ? configured.Value : 2_000;
    }
}

public sealed class ProductionMonitoringStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<ProductionMonitoringMiddleware>();
        next(app);
    };
}

public sealed class ProductionMonitoringMiddleware(
    RequestDelegate next,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<ProductionMonitoringMiddleware> logger,
    IProductionAlertSink alertSink,
    IHttpClientFactory httpClientFactory)
{
    public async Task InvokeAsync(HttpContext context, CareDbContext dbContext)
    {
        if (HttpMethods.IsGet(context.Request.Method) &&
            string.Equals(context.Request.Path.Value, "/health/live", StringComparison.OrdinalIgnoreCase))
        {
            await context.Response.WriteAsJsonAsync(new
            {
                status = "healthy",
                service = "AiCare API",
                check = "liveness",
                checkedAt = DateTimeOffset.UtcNow
            }, context.RequestAborted);
            return;
        }

        if (HttpMethods.IsGet(context.Request.Method) &&
            string.Equals(context.Request.Path.Value, "/health/ready", StringComparison.OrdinalIgnoreCase))
        {
            await WriteReadinessAsync(context, dbContext);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        Exception? failure = null;
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            var userId = context.User.FindFirstValue("sub") ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var organizationId = context.User.FindFirstValue("organization_id");
            var branchId = context.User.FindFirstValue("branch_id");
            logger.LogInformation(
                "request.completed method={Method} path={Path} status={StatusCode} elapsedMs={ElapsedMs} requestId={RequestId} userId={UserId} organizationId={OrganizationId} branchId={BranchId}",
                context.Request.Method,
                context.Request.Path,
                context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                context.TraceIdentifier,
                userId ?? "anonymous",
                organizationId ?? "none",
                branchId ?? "none");

            if (environment.IsProduction())
            {
                var code = failure is not null
                    ? "unhandled_exception"
                    : MonitoringPolicy.Classify(
                        context.Response.StatusCode,
                        stopwatch.ElapsedMilliseconds,
                        MonitoringPolicy.SlowRequestThreshold(configuration));
                if (code is not null)
                {
                    var severity = failure is not null || context.Response.StatusCode >= 500 ? "critical" : "warning";
                    await alertSink.SendAsync(new ProductionAlert(
                        severity,
                        code,
                        failure is null ? "Production request threshold triggered." : "Unhandled production request failure.",
                        context.TraceIdentifier,
                        context.Request.Path.Value ?? string.Empty,
                        failure is null ? context.Response.StatusCode : StatusCodes.Status500InternalServerError,
                        stopwatch.ElapsedMilliseconds,
                        DateTimeOffset.UtcNow),
                        CancellationToken.None);
                }
            }
        }
    }

    private async Task WriteReadinessAsync(HttpContext context, CareDbContext dbContext)
    {
        var databaseHealthy = false;
        try
        {
            databaseHealthy = await dbContext.Database.CanConnectAsync(context.RequestAborted);
        }
        catch
        {
            databaseHealthy = false;
        }

        var storageHealthy = await CheckStorageAsync(context.RequestAborted);
        var ready = databaseHealthy && storageHealthy;
        context.Response.StatusCode = ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new
        {
            status = ready ? "healthy" : "unhealthy",
            check = "readiness",
            database = databaseHealthy ? "healthy" : "unhealthy",
            storage = storageHealthy ? "healthy" : "unhealthy",
            checkedAt = DateTimeOffset.UtcNow
        }, context.RequestAborted);

        if (!ready && environment.IsProduction())
        {
            await alertSink.SendAsync(new ProductionAlert(
                "critical",
                "readiness_failed",
                "Production readiness check failed.",
                context.TraceIdentifier,
                context.Request.Path.Value ?? "/health/ready",
                context.Response.StatusCode,
                null,
                DateTimeOffset.UtcNow),
                CancellationToken.None);
        }
    }

    private async Task<bool> CheckStorageAsync(CancellationToken cancellationToken)
    {
        var provider = configuration["Storage:Provider"] ?? "Local";
        if (!string.Equals(provider, "Supabase", StringComparison.OrdinalIgnoreCase)) return true;

        var supabaseUrl = configuration["Supabase:Url"];
        var serviceRoleKey = configuration["Supabase:ServiceRoleKey"];
        var bucket = configuration["Supabase:Bucket"];
        if (string.IsNullOrWhiteSpace(supabaseUrl) ||
            string.IsNullOrWhiteSpace(serviceRoleKey) ||
            string.IsNullOrWhiteSpace(bucket)) return false;

        try
        {
            var client = httpClientFactory.CreateClient();
            var url = $"{supabaseUrl.TrimEnd('/')}/storage/v1/bucket/{Uri.EscapeDataString(bucket)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("apikey", serviceRoleKey);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {serviceRoleKey}");
            using var response = await client.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
