using AiCare.Application;
using AiCare.Application.FamilyPortal;
using AiCare.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiCare.Api;

[ApiController]
[Route("api/phase1/family-access")]
[Authorize(Policy = "Phase1User")]
public sealed class FamilyAccessAdminController(IFamilyPortalService familyPortal, ITenantContext tenant, ICurrentUserContext currentUser, IConfiguration configuration) : ControllerBase
{
    [HttpGet("{familyMemberId:guid}")]
    public async Task<IActionResult> Get(Guid familyMemberId, CancellationToken ct)
    {
        if (!IsStaff()) return Forbid();
        var result = await familyPortal.GetAccessAsync(tenant.OrganizationId, familyMemberId, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPut("{familyMemberId:guid}")]
    public async Task<IActionResult> Configure(Guid familyMemberId, ConfigureFamilyAccessRequest request, CancellationToken ct)
    {
        if (!IsStaff()) return Forbid();
        try
        {
            var result = await familyPortal.ConfigureAccessAsync(tenant.OrganizationId, tenant.BranchId, ActorId(), currentUser.UserName,
                new ConfigureFamilyAccessCommand(familyMemberId, request.AuthorityType ?? string.Empty, request.VerificationStatus ?? "Pending", request.ValidFrom, request.ValidUntil, request.Permissions ?? Array.Empty<string>(), request.ExpectedRevision), ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message.Contains("changed by another user", StringComparison.OrdinalIgnoreCase) ? Conflict(new { message = ex.Message }) : BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{familyMemberId:guid}/invite")]
    public async Task<IActionResult> Invite(Guid familyMemberId, CancellationToken ct)
    {
        if (!IsStaff()) return Forbid();
        try
        {
            var baseUrl = configuration["FamilyPortal:FrontendBaseUrl"] ?? configuration["Frontend:BaseUrl"] ?? "http://localhost:5173";
            return Ok(await familyPortal.InviteAsync(tenant.OrganizationId, tenant.BranchId, ActorId(), currentUser.UserName, familyMemberId, baseUrl, ct));
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("{familyMemberId:guid}/suspend")]
    public Task<IActionResult> Suspend(Guid familyMemberId, CancellationToken ct) => SetStatus(familyMemberId, "Suspended", ct);
    [HttpPost("{familyMemberId:guid}/restore")]
    public Task<IActionResult> Restore(Guid familyMemberId, CancellationToken ct) => SetStatus(familyMemberId, "Active", ct);
    [HttpPost("{familyMemberId:guid}/revoke")]
    public Task<IActionResult> Revoke(Guid familyMemberId, CancellationToken ct) => SetStatus(familyMemberId, "Revoked", ct);

    private async Task<IActionResult> SetStatus(Guid familyMemberId, string status, CancellationToken ct)
    {
        if (!IsStaff()) return Forbid();
        try
        {
            await familyPortal.SetAccessStatusAsync(tenant.OrganizationId, ActorId(), currentUser.UserName, familyMemberId, status, ct);
            return Ok(await familyPortal.GetAccessAsync(tenant.OrganizationId, familyMemberId, ct));
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    private bool IsStaff() => currentUser.HasAnyRole(UserRole.Administrator, UserRole.CareManager, UserRole.CareCoordinator);
    private Guid ActorId() => currentUser.UserId ?? throw new InvalidOperationException("Authenticated user identifier is required.");
}

[ApiController]
[Route("api/family")]
public sealed class FamilyPortalGovernanceController(IFamilyPortalService familyPortal, ITenantContext tenant, ICurrentUserContext currentUser) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("invitations/validate")]
    public async Task<IActionResult> ValidateInvitation(ValidateFamilyInvitationRequest request, CancellationToken ct)
        => Ok(await familyPortal.ValidateInvitationAsync(request.Token ?? string.Empty, ct));

    [AllowAnonymous]
    [HttpPost("invitations/accept")]
    public async Task<IActionResult> AcceptInvitation(AcceptFamilyInvitationRequest request, CancellationToken ct)
    {
        try
        {
            await familyPortal.AcceptInvitationAsync(request.Token ?? string.Empty, request.Password ?? string.Empty, ct);
            return Ok(new { status = "Activated", message = "Family Portal account activated. You can now sign in." });
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [Authorize(Policy = "Phase1User")]
    [HttpGet("people")]
    public async Task<IActionResult> GetAuthorizedPeople(CancellationToken ct)
    {
        if (!currentUser.IsFamilyMember || currentUser.FamilyMemberId is null) return Forbid();
        return Ok(await familyPortal.GetAuthorizedPeopleAsync(tenant.OrganizationId, currentUser.FamilyMemberId.Value, ct));
    }

    [Authorize(Policy = "Phase1User")]
    [HttpPost("feedback")]
    public async Task<IActionResult> SubmitFeedback(SubmitFamilyFeedbackRequest request, CancellationToken ct)
    {
        if (!currentUser.IsFamilyMember || currentUser.FamilyMemberId is null) return Forbid();
        try
        {
            var result = await familyPortal.SubmitFeedbackAsync(tenant.OrganizationId, tenant.BranchId, currentUser.FamilyMemberId.Value,
                new FamilyFeedbackInput(request.ServiceUserId, request.Type ?? "Feedback", request.Subject ?? string.Empty, request.Description ?? string.Empty, request.Priority ?? "Routine"), ct);
            return Created($"/api/family/feedback/{result.Id}", result);
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }
}

public sealed record ConfigureFamilyAccessRequest(string? AuthorityType, string? VerificationStatus, DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil, IReadOnlyCollection<string>? Permissions, long? ExpectedRevision);
public sealed record ValidateFamilyInvitationRequest(string? Token);
public sealed record AcceptFamilyInvitationRequest(string? Token, string? Password);
public sealed record SubmitFamilyFeedbackRequest(Guid ServiceUserId, string? Type, string? Subject, string? Description, string? Priority);
