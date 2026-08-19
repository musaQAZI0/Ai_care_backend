using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AiCare.Infrastructure;

public static class ApiSecurityPolicy
{
    public static bool IsOriginAllowed(IConfiguration configuration, string origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return true;
        var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        return allowedOrigins.Any(allowed =>
            !string.IsNullOrWhiteSpace(allowed) &&
            string.Equals(allowed.TrimEnd('/'), origin.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
    }

    public static int? GetRateLimit(string method, string path)
    {
        if (!HttpMethods.IsPost(method)) return null;

        if (string.Equals(path, "/api/auth/login", StringComparison.OrdinalIgnoreCase)) return 10;
        if (string.Equals(path, "/api/auth/forgot-password", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "/api/auth/reset-password", StringComparison.OrdinalIgnoreCase)) return 5;
        if (string.Equals(path, "/api/family/invitations/validate", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "/api/family/invitations/accept", StringComparison.OrdinalIgnoreCase)) return 10;
        if (string.Equals(path, "/api/phase1/documents/upload", StringComparison.OrdinalIgnoreCase)) return 30;
        return null;
    }
}

public sealed class ApiSecurityHardeningStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<ApiSecurityHardeningMiddleware>();
        next(app);
    };
}

public sealed class ApiSecurityHardeningMiddleware(
    RequestDelegate next,
    IConfiguration configuration,
    IHostEnvironment environment)
{
    private static readonly ConcurrentDictionary<string, RateWindow> RateWindows = new(StringComparer.Ordinal);
    private static readonly TimeSpan RateWindowDuration = TimeSpan.FromMinutes(1);

    public async Task InvokeAsync(HttpContext context, CareDbContext dbContext)
    {
        AddSecurityHeaders(context);

        if (environment.IsProduction() && HasDisallowedOrigin(context))
        {
            await Reject(context, StatusCodes.Status403Forbidden, "Request origin is not allowed.");
            return;
        }

        var rateLimit = environment.IsProduction()
            ? ApiSecurityPolicy.GetRateLimit(context.Request.Method, context.Request.Path.Value ?? string.Empty)
            : null;
        if (rateLimit is not null && !ConsumeRateLimit(context, rateLimit.Value))
        {
            context.Response.Headers.RetryAfter = "60";
            await Reject(context, StatusCodes.Status429TooManyRequests, "Too many requests. Please try again later.");
            return;
        }

        if (HttpMethods.IsGet(context.Request.Method) &&
            string.Equals(context.Request.Path.Value, "/health/db", StringComparison.OrdinalIgnoreCase))
        {
            await WriteDatabaseHealth(context, dbContext);
            return;
        }

        await next(context);
    }

    private bool HasDisallowedOrigin(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue("Origin", out var rawOrigin)) return false;
        return !ApiSecurityPolicy.IsOriginAllowed(configuration, rawOrigin.ToString());
    }

    private static bool ConsumeRateLimit(HttpContext context, int limit)
    {
        var now = DateTimeOffset.UtcNow;
        var key = BuildRateLimitKey(context);
        var window = RateWindows.GetOrAdd(key, _ => new RateWindow(now));
        lock (window)
        {
            if (now - window.StartedAt >= RateWindowDuration)
            {
                window.StartedAt = now;
                window.Count = 0;
            }

            if (window.Count >= limit)
            {
                CleanupExpiredWindows(now);
                return false;
            }

            window.Count++;
            CleanupExpiredWindows(now);
            return true;
        }
    }

    private static string BuildRateLimitKey(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? string.Empty;
        var remoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var forwardedAddress = context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwarded)
            ? forwarded.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? string.Empty
            : string.Empty;
        var authFingerprint = context.Request.Headers.TryGetValue("Authorization", out var authorization)
            ? Fingerprint(authorization.ToString())
            : string.Empty;
        return $"{path}|{remoteAddress}|{forwardedAddress}|{authFingerprint}";
    }

    private static string Fingerprint(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
    }

    private static void CleanupExpiredWindows(DateTimeOffset now)
    {
        if (RateWindows.Count < 1_000) return;
        foreach (var item in RateWindows)
        {
            if (now - item.Value.StartedAt > RateWindowDuration + RateWindowDuration)
            {
                RateWindows.TryRemove(item.Key, out _);
            }
        }
    }

    private static void AddSecurityHeaders(HttpContext context)
    {
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["Pragma"] = "no-cache";
        if (context.Request.IsHttps)
        {
            context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        }
    }

    private static async Task WriteDatabaseHealth(HttpContext context, CareDbContext dbContext)
    {
        try
        {
            if (await dbContext.Database.CanConnectAsync(context.RequestAborted))
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                await context.Response.WriteAsJsonAsync(new
                {
                    status = "healthy",
                    provider = "PostgreSQL",
                    checkedAt = DateTimeOffset.UtcNow
                }, context.RequestAborted);
                return;
            }
        }
        catch
        {
            // Public health endpoints deliberately do not expose provider exception details.
        }

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new
        {
            status = "unhealthy",
            service = "database",
            checkedAt = DateTimeOffset.UtcNow
        }, context.RequestAborted);
    }

    private static async Task Reject(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { message }, context.RequestAborted);
    }

    private sealed class RateWindow(DateTimeOffset startedAt)
    {
        public DateTimeOffset StartedAt { get; set; } = startedAt;
        public int Count { get; set; }
    }
}
