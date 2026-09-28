using System.Text;
using System.Text.Json;
using AiCare.Application.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiCare.Api;

[ApiController]
[Route("api/webhooks/resend")]
public sealed class ResendWebhookController(IResendWebhookProcessor processor) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var rawBody = await reader.ReadToEndAsync(cancellationToken);
        try
        {
            await processor.ProcessAsync(
                rawBody,
                Request.Headers.ToDictionary(header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                cancellationToken);
            return Ok();
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
        catch (JsonException)
        {
            return BadRequest();
        }
    }
}
