using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiCare.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiCare.Api;

[ApiController, Authorize(Policy = "Phase1User", Roles = "CareCoordinator,CareManager,Administrator")]
[Route("api/phase1/care-operations/postcode-lookup")]
public sealed class PostcodeLookupController(ITenantContext tenant, IHttpClientFactory clients) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Lookup([FromQuery] string? postcode, CancellationToken cancellationToken)
    {
        if (tenant.OrganizationId == Guid.Empty || (!tenant.IsOrganizationWide && tenant.BranchId is null))
            return Forbid();

        if (postcode is null || postcode.Length > 16)
            return BadRequest(new { message = "Enter a valid Great Britain postcode." });
        var compact = Regex.Replace(postcode.Trim().ToUpperInvariant(), @"\s+", "");
        if (compact.StartsWith("BT", StringComparison.Ordinal))
            return UnprocessableEntity(new { message = "Northern Ireland postcode lookup is not available in AiCare yet." });
        if (compact.Length is < 5 or > 7 || !Regex.IsMatch(compact, @"^[A-Z]{1,2}[0-9][A-Z0-9]?[0-9][A-Z]{2}$", RegexOptions.CultureInvariant))
            return BadRequest(new { message = "Enter a valid Great Britain postcode." });

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var response = await clients.CreateClient().GetAsync(
                $"https://api.postcodes.io/postcodes/{Uri.EscapeDataString(compact)}", timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return NotFound(new { message = "That postcode was not found." });
            if (!response.IsSuccessStatusCode)
                return StatusCode(502, new { message = "Postcode lookup is temporarily unavailable." });

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            if (!document.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty("postcode", out var matchedPostcode) || matchedPostcode.ValueKind != JsonValueKind.String ||
                !result.TryGetProperty("latitude", out var latitudeValue) || latitudeValue.ValueKind != JsonValueKind.Number || !latitudeValue.TryGetDouble(out var latitude) ||
                !result.TryGetProperty("longitude", out var longitudeValue) || longitudeValue.ValueKind != JsonValueKind.Number || !longitudeValue.TryGetDouble(out var longitude) ||
                !double.IsFinite(latitude) || !double.IsFinite(longitude) || latitude is < 49 or > 61 || longitude is < -9 or > 3)
                return StatusCode(502, new { message = "Postcode lookup returned no usable Great Britain location." });

            return Ok(new { postcode = matchedPostcode.GetString(), latitude, longitude, approximate = true, source = "Postcodes.io" });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(504, new { message = "Postcode lookup timed out. Try again." });
        }
        catch (HttpRequestException)
        {
            return StatusCode(502, new { message = "Postcode lookup is temporarily unavailable." });
        }
        catch (IOException)
        {
            return StatusCode(502, new { message = "Postcode lookup is temporarily unavailable." });
        }
        catch (JsonException)
        {
            return StatusCode(502, new { message = "Postcode lookup returned an invalid response." });
        }
    }
}
