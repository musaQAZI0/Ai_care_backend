using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiCare.Application;
using Microsoft.Extensions.Configuration;

namespace AiCare.Infrastructure;

public sealed class NhsDmdTerminologyService(HttpClient httpClient, IConfiguration configuration) : IMedicationTerminologyService
{
    private const string DmdSystem = "https://dmd.nhs.uk";
    private readonly string baseUrl = (configuration["MedicationTerminology:BaseUrl"] ?? "https://ontology.nhs.uk/production1/fhir").TrimEnd('/');
    private readonly string tokenUrl = configuration["MedicationTerminology:TokenUrl"] ?? "https://ontology.nhs.uk/authorisation/auth/realms/nhs-digital-terminology/protocol/openid-connect/token";
    private readonly string? clientId = configuration["MedicationTerminology:ClientId"];
    private readonly string? clientSecret = configuration["MedicationTerminology:ClientSecret"];

    public async Task<IReadOnlyList<MedicationTerminologyResult>> SearchAsync(string query, int count = 20, CancellationToken cancellationToken = default)
    {
        query = query.Trim();
        if (query.Length < 2) return [];
        count = Math.Clamp(count, 1, 50);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/ValueSet/$expand");
        await AuthorizeAsync(request, cancellationToken);
        request.Content = JsonContent.Create(new
        {
            resourceType = "Parameters",
            parameter = new object[]
            {
                new { name = "valueSet", resource = new { resourceType = "ValueSet", compose = new { include = new[] { new { system = DmdSystem } } } } },
                new { name = "filter", valueString = query },
                new { name = "count", valueInteger = count }
            }
        });
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("expansion", out var expansion) ||
            !expansion.TryGetProperty("contains", out var contains)) return [];
        var results = new List<MedicationTerminologyResult>();
        Flatten(contains, results);
        return results
            .Where(x => string.Equals(x.System, DmdSystem, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(x => x.Code)
            .Take(count)
            .ToArray();
    }

    public async Task<MedicationTerminologyConcept?> LookupAsync(string code, CancellationToken cancellationToken = default)
    {
        code = code.Trim();
        if (code.Length == 0) return null;
        var url = $"{baseUrl}/CodeSystem/$lookup?system={Uri.EscapeDataString(DmdSystem)}&code={Uri.EscapeDataString(code)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await AuthorizeAsync(request, cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await EnsureSuccess(response, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var display = "";
        var inactive = false;
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (document.RootElement.TryGetProperty("parameter", out var parameters))
        {
            foreach (var parameter in parameters.EnumerateArray())
            {
                var name = parameter.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name == "display" && parameter.TryGetProperty("valueString", out var d)) display = d.GetString() ?? "";
                if (name == "inactive" && parameter.TryGetProperty("valueBoolean", out var i)) inactive = i.GetBoolean();
                if (name == "property" && parameter.TryGetProperty("part", out var parts))
                {
                    string? key = null, value = null;
                    foreach (var part in parts.EnumerateArray())
                    {
                        var partName = part.TryGetProperty("name", out var pn) ? pn.GetString() : null;
                        if (partName == "code") key = ReadValue(part);
                        else if (partName == "value") value = ReadValue(part);
                    }
                    if (!string.IsNullOrWhiteSpace(key) && value is not null) properties[key] = value;
                }
            }
        }
        return string.IsNullOrWhiteSpace(display) ? null : new(code, display, DmdSystem, inactive, properties);
    }

    private async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret)) return;
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret
            })
        };
        using var tokenResponse = await httpClient.SendAsync(tokenRequest, cancellationToken);
        await EnsureSuccess(tokenResponse, cancellationToken);
        using var tokenDocument = JsonDocument.Parse(await tokenResponse.Content.ReadAsStreamAsync(cancellationToken));
        var token = tokenDocument.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("NHS Terminology Server token response did not contain an access token.");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        if (detail.Length > 500) detail = detail[..500];
        throw new HttpRequestException($"NHS Terminology Server returned {(int)response.StatusCode} {response.ReasonPhrase}. {detail}");
    }

    private static void Flatten(JsonElement contains, ICollection<MedicationTerminologyResult> results)
    {
        foreach (var item in contains.EnumerateArray())
        {
            var code = item.TryGetProperty("code", out var c) ? c.GetString() : null;
            var display = item.TryGetProperty("display", out var d) ? d.GetString() : null;
            var system = item.TryGetProperty("system", out var s) ? s.GetString() : DmdSystem;
            var inactive = item.TryGetProperty("inactive", out var i) && i.GetBoolean();
            if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(display))
                results.Add(new(code, display, system ?? DmdSystem, inactive));
            if (item.TryGetProperty("contains", out var nested)) Flatten(nested, results);
        }
    }

    private static string? ReadValue(JsonElement element)
    {
        foreach (var property in element.EnumerateObject())
            if (property.Name.StartsWith("value", StringComparison.Ordinal) && property.Value.ValueKind != JsonValueKind.Null)
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString();
        return null;
    }
}
