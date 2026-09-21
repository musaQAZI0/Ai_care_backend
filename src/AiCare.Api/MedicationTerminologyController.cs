using AiCare.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiCare.Api;

[ApiController]
[Authorize(Roles = "CareWorker,CareCoordinator,CareManager,Administrator")]
[Route("api/phase1/medication-terminology")]
public sealed class MedicationTerminologyController(IMedicationTerminologyService terminology) : ControllerBase
{
    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string q, [FromQuery] int count = 20, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            return BadRequest(new { message = "Enter at least two characters to search dm+d." });
        try
        {
            var results = await terminology.SearchAsync(q, count, cancellationToken);
            return Ok(new { source = "NHS dm+d", query = q.Trim(), results });
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "The NHS medication terminology service is temporarily unavailable." });
        }
    }

    [HttpGet("{code}")]
    public async Task<IActionResult> Lookup(string code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code)) return BadRequest(new { message = "A dm+d code is required." });
        try
        {
            var concept = await terminology.LookupAsync(code, cancellationToken);
            return concept is null ? NotFound(new { message = "dm+d concept not found." }) : Ok(concept);
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "The NHS medication terminology service is temporarily unavailable." });
        }
    }
}
