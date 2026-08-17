using System.Data;
using AiCare.Application;
using AiCare.Application.CarePlans;
using AiCare.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiCare.Api;

[ApiController]
[Authorize(Policy = "Phase1User")]
[Route("api/phase1/care-plans")]
public sealed class CarePlanLifecycleController(
    ICarePlanLifecycleService lifecycle,
    ITenantContext tenant,
    ICurrentUserContext currentUser) : ControllerBase
{
    [HttpGet("{carePlanId:guid}/lifecycle")]
    public async Task<IActionResult> GetLifecycle(Guid carePlanId, CancellationToken cancellationToken)
    {
        var result = await lifecycle.GetAsync(carePlanId, Actor(), cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("service-user/{serviceUserId:guid}/versions")]
    public async Task<IActionResult> GetVersions(Guid serviceUserId, CancellationToken cancellationToken) =>
        Ok(await lifecycle.GetVersionsAsync(serviceUserId, Actor(), cancellationToken));

    [HttpPost("{carePlanId:guid}/submit-review")]
    public Task<IActionResult> SubmitForReview(Guid carePlanId, LifecycleActionRequest request, CancellationToken cancellationToken) =>
        Execute(() => lifecycle.SubmitForReviewAsync(carePlanId, request.ExpectedRevision, request.Comment ?? string.Empty, Actor(), cancellationToken));

    [HttpPost("{carePlanId:guid}/return-to-draft")]
    public Task<IActionResult> ReturnToDraft(Guid carePlanId, LifecycleActionRequest request, CancellationToken cancellationToken) =>
        Execute(() => lifecycle.ReturnToDraftAsync(carePlanId, request.ExpectedRevision, request.Reason ?? string.Empty, request.Comment ?? string.Empty, Actor(), cancellationToken));

    [HttpPost("{carePlanId:guid}/approve")]
    public Task<IActionResult> Approve(Guid carePlanId, LifecycleActionRequest request, CancellationToken cancellationToken) =>
        Execute(() => lifecycle.ApproveAsync(carePlanId, request.ExpectedRevision, request.Comment ?? string.Empty, Actor(), cancellationToken));

    [HttpPost("{carePlanId:guid}/signatures")]
    public async Task<IActionResult> Sign(Guid carePlanId, SignCarePlanRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<CarePlanSignerType>(request.SignerType, true, out var signerType))
            return BadRequest(new { message = "Invalid signer type." });
        if (!Enum.TryParse<CarePlanSignatureMethod>(request.SignatureMethod, true, out var method))
            return BadRequest(new { message = "Invalid signature method." });

        return await Execute(() => lifecycle.SignAsync(carePlanId,
            new SignCarePlanCommand(signerType, request.SignerName ?? string.Empty, request.Relationship ?? string.Empty, request.Declaration ?? string.Empty, method, request.ExpectedRevision),
            Actor(), cancellationToken));
    }

    [HttpPost("{carePlanId:guid}/activate")]
    public Task<IActionResult> Activate(Guid carePlanId, RevisionRequest request, CancellationToken cancellationToken) =>
        Execute(() => lifecycle.ActivateAsync(carePlanId, request.ExpectedRevision, Actor(), cancellationToken));

    [HttpPost("{carePlanId:guid}/revisions")]
    public Task<IActionResult> CreateRevision(Guid carePlanId, CreateRevisionRequest request, CancellationToken cancellationToken) =>
        Execute(() => lifecycle.CreateRevisionAsync(carePlanId, new CreateCarePlanRevisionCommand(request.ChangeReason ?? string.Empty, request.ExpectedRevision), Actor(), cancellationToken), created: true);

    [HttpPost("{carePlanId:guid}/acknowledgements")]
    public Task<IActionResult> Acknowledge(Guid carePlanId, RevisionRequest request, CancellationToken cancellationToken) =>
        Execute(() => lifecycle.AcknowledgeAsync(carePlanId, request.ExpectedRevision, Actor(), cancellationToken));

    private CarePlanActor Actor() => new(
        currentUser.UserId,
        currentUser.UserName,
        currentUser.Role,
        currentUser.CareWorkerId,
        currentUser.FamilyMemberId,
        tenant.OrganizationId,
        tenant.BranchId);

    private async Task<IActionResult> Execute(Func<Task<CarePlanLifecycleSnapshot>> action, bool created = false)
    {
        try
        {
            var result = await action();
            return created ? Created($"/api/phase1/care-plans/{result.CarePlan.Id}/lifecycle", result) : Ok(result);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (DBConcurrencyException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}

public sealed record LifecycleActionRequest(long ExpectedRevision, string? Reason, string? Comment);
public sealed record RevisionRequest(long ExpectedRevision);
public sealed record CreateRevisionRequest(long ExpectedRevision, string? ChangeReason);
public sealed record SignCarePlanRequest(long ExpectedRevision, string? SignerType, string? SignerName, string? Relationship, string? Declaration, string? SignatureMethod);
