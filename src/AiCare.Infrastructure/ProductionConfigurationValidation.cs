using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AiCare.Infrastructure;

public sealed class ProductionConfigurationValidationService(
    IConfiguration configuration,
    IHostEnvironment environment) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ProductionConfigurationValidator.Validate(configuration, environment.EnvironmentName);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class ProductionConfigurationValidator
{
    public static void Validate(IConfiguration configuration, string environmentName)
    {
        if (!string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var errors = new List<string>();

        Require(configuration.GetConnectionString("DefaultConnection"), "ConnectionStrings:DefaultConnection", errors);
        Require(configuration["JwtOptions:Issuer"], "JwtOptions:Issuer", errors);
        Require(configuration["JwtOptions:Audience"], "JwtOptions:Audience", errors);

        var signingKey = configuration["JwtOptions:SigningKey"];
        Require(signingKey, "JwtOptions:SigningKey", errors);
        if (!string.IsNullOrWhiteSpace(signingKey) && Encoding.UTF8.GetByteCount(signingKey) < 32)
        {
            errors.Add("JwtOptions:SigningKey must contain at least 32 bytes of secret material.");
        }
        if (LooksLikePlaceholder(signingKey))
        {
            errors.Add("JwtOptions:SigningKey must not use a placeholder/default value.");
        }

        var storageProvider = configuration["Storage:Provider"];
        if (!string.Equals(storageProvider, "Supabase", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Storage:Provider must be Supabase in Production.");
        }

        var supabaseUrl = configuration["Supabase:Url"];
        RequireHttpsUrl(supabaseUrl, "Supabase:Url", errors);
        var serviceRoleKey = configuration["Supabase:ServiceRoleKey"];
        Require(serviceRoleKey, "Supabase:ServiceRoleKey", errors);
        if (LooksLikePlaceholder(serviceRoleKey))
        {
            errors.Add("Supabase:ServiceRoleKey must not use a placeholder/default value.");
        }
        Require(configuration["Supabase:Bucket"], "Supabase:Bucket", errors);
        if (!string.IsNullOrWhiteSpace(configuration["Supabase:PublicFileBaseUrl"]))
        {
            errors.Add("Supabase:PublicFileBaseUrl must be empty in Production so care documents use short-lived signed URLs.");
        }

        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        var configuredOrigins = origins.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (configuredOrigins.Length == 0)
        {
            errors.Add("Cors:AllowedOrigins must contain at least one Production origin.");
        }
        foreach (var origin in configuredOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                uri.IsLoopback)
            {
                errors.Add("Cors:AllowedOrigins may contain only non-loopback HTTPS origins in Production.");
                break;
            }
        }

        RequireHttpsUrl(configuration["FamilyPortal:FrontendBaseUrl"], "FamilyPortal:FrontendBaseUrl", errors);

        if (configuration.GetValue<bool>("Email:RequiredInProduction"))
        {
            if (!configuration.GetValue<bool>("Email:Enabled"))
            {
                errors.Add("Email:Enabled must be true when Email:RequiredInProduction is enabled.");
            }
            Require(configuration["Email:SmtpHost"], "Email:SmtpHost", errors);
            var smtpPort = configuration.GetValue<int?>("Email:SmtpPort");
            if (smtpPort is null or < 1 or > 65535)
            {
                errors.Add("Email:SmtpPort must be a valid TCP port when production email is required.");
            }
            Require(configuration["Email:Username"], "Email:Username", errors);
            var emailPassword = configuration["Email:Password"];
            Require(emailPassword, "Email:Password", errors);
            if (LooksLikePlaceholder(emailPassword))
            {
                errors.Add("Email:Password must not use a placeholder/default value.");
            }
            Require(configuration["Email:FromAddress"], "Email:FromAddress", errors);
            if (!configuration.GetValue<bool>("Email:EnableSsl"))
            {
                errors.Add("Email:EnableSsl must be true when production email is required.");
            }
        }

        if (string.Equals(configuration["Demo:Enabled"], "true", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Demo:Enabled must be false in Production.");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Invalid production configuration: " + string.Join(" ", errors.Distinct(StringComparer.Ordinal)));
        }
    }

    private static void Require(string? value, string key, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{key} is required in Production.");
        }
    }

    private static void RequireHttpsUrl(string? value, string key, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{key} is required in Production.");
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.IsLoopback)
        {
            errors.Add($"{key} must be a non-loopback HTTPS URL in Production.");
        }
    }

    private static bool LooksLikePlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized.Contains("change-me", StringComparison.Ordinal) ||
               normalized.Contains("changeme", StringComparison.Ordinal) ||
               normalized.Contains("your_", StringComparison.Ordinal) ||
               normalized.Contains("placeholder", StringComparison.Ordinal) ||
               normalized.Contains("example-secret", StringComparison.Ordinal) ||
               normalized == "secret";
    }
}
