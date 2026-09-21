using System.Text.Json;

namespace AiCare.Api;

internal static class IntegrationConnectorPolicy
{
    private const int MaxConfigurationBytes = 16 * 1024;
    private static readonly string[] CredentialTerms = ["secret", "password", "token", "apikey", "api_key", "credential", "privatekey", "private_key"];
    private static readonly string[] ManagedClinicalTerms = ["nhs", "pharmacy", "clinical", "emar", "e-mar"];

    internal static string? ValidateCreate(CreateIntegrationConnectorRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.ConnectorType)) return "Name and connector type are required.";
        if (request.Name.Trim().Length > 120 || request.ConnectorType.Trim().Length > 80) return "Connector name or type is too long.";
        if (request.ScheduleMinutes is < 1 or > 10080) return "Schedule must be between 1 minute and 7 days.";
        if (RequiresManagedAdapter(request.ConnectorType)) return "Clinical, NHS, pharmacy and eMAR connectors require an AI Care managed adapter and cannot be configured as arbitrary tenant endpoints.";
        var endpointError = ValidateEndpoint(request.EndpointUrl); if (endpointError is not null) return endpointError;
        return ValidateConfiguration(request.Configuration);
    }

    internal static string? ValidateEndpoint(string? endpointUrl)
    {
        if (string.IsNullOrWhiteSpace(endpointUrl)) return null;
        if (!Uri.TryCreate(endpointUrl.Trim(), UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(endpoint.UserInfo)) return "Connector endpoint must be an absolute HTTPS URL without embedded credentials.";
        return endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || endpoint.IsLoopback ? "Connector endpoint cannot target localhost." : null;
    }

    private static string? ValidateConfiguration(Dictionary<string,string>? configuration)
    {
        if (configuration is null) return null;
        if (configuration.Count > 50) return "Connector configuration cannot contain more than 50 entries.";
        foreach (var item in configuration)
        {
            var normalized = item.Key.Replace("-", "", StringComparison.Ordinal).Replace(".", "", StringComparison.Ordinal).ToLowerInvariant();
            if (CredentialTerms.Any(term => normalized.Contains(term.Replace("_", "", StringComparison.Ordinal), StringComparison.Ordinal))) return "Credentials and secrets must use protected provider storage and cannot be included in connector configuration.";
            if (string.IsNullOrWhiteSpace(item.Key) || item.Key.Length > 100 || item.Value?.Length > 2048) return "Connector configuration keys or values exceed the allowed length.";
        }
        return JsonSerializer.SerializeToUtf8Bytes(configuration).Length > MaxConfigurationBytes ? "Connector configuration cannot exceed 16 KiB." : null;
    }

    private static bool RequiresManagedAdapter(string connectorType) => ManagedClinicalTerms.Any(term => connectorType.Contains(term, StringComparison.OrdinalIgnoreCase));
}
