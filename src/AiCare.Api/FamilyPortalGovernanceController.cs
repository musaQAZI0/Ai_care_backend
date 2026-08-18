using AiCare.Application;
using AiCare.Application.FamilyPortal;
using AiCare.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiCare.Api;

[ApiController]
[Route("api/phase1/family-access")]
[Authorize(Policy = "Phase1User")]
public sealed class FamilyAccessAdminController(
    IFamilyPortalService familyPortal,
    ITenantContext tenant,
    ICurrentUserContext currentUser,
    IConfiguration configuration,
    IWebHostEnvironment environment) : ControllerBase
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
            var result = await familyPortal.ConfigureAccessAsync(
                tenant.OrganizationId,
                tenant.BranchId,
                ActorId(),
                currentUser.UserName,
                new ConfigureFamilyAccessCommand(
                    familyMemberId,
                    request.AuthorityType ?? string.Empty,
                    request.VerificationStatus ?? "Pending",
                    request.VerificationReference ?? string.Empty,
                    request.ValidFrom,
                    request.ValidUntil,
                    request.Permissions ?? Array.Empty<string>(),
                    request.ExpectedRevision),
                ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message.Contains("changed by another user", StringComparison.OrdinalIgnoreCase)
                ? Conflict(new { message = ex.Message })
                : BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{familyMemberId:guid}/invite")]
    public async Task<IActionResult> Invite(Guid familyMemberId, CancellationToken ct)
    {
        if (!IsStaff()) return Forbid();
        try
        {
            var baseUrl = configuration["FamilyPortal:FrontendBaseUrl"] ?? configuration["Frontend:BaseUrl"] ?? "http://localhost:5173";
            var result = await familyPortal.InviteAsync(tenant.OrganizationId, tenant.BranchId, ActorId(), currentUser.UserName, familyMemberId, baseUrl, ct);
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
                result = result with { DevelopmentActivationUrl = null };
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
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
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private bool IsStaff() => currentUser.HasAnyRole(UserRole.Administrator, UserRole.CareManager, UserRole.CareCoordinator);
    private Guid ActorId() => currentUser.UserId ?? throw new InvalidOperationException("Authenticated user identifier is required.");
}

[ApiController]
[Route("api/phase1/family-documents")]
[Authorize(Policy = "Phase1User")]
public sealed class FamilyDocumentAccessAdminController(
    IFamilyPortalQueryStore familyQueries,
    ITenantContext tenant,
    ICurrentUserContext currentUser) : ControllerBase
{
    [HttpPut("{documentId:guid}/visibility")]
    public async Task<IActionResult> SetVisibility(Guid documentId, SetFamilyDocumentVisibilityRequest request, CancellationToken ct)
    {
        if (!currentUser.HasAnyRole(UserRole.Administrator, UserRole.CareManager, UserRole.CareCoordinator)) return Forbid();
        try
        {
            await familyQueries.SetDocumentVisibilityAsync(
                tenant.OrganizationId,
                tenant.BranchId,
                currentUser.UserName,
                new SetFamilyDocumentVisibilityCommand(documentId, request.Visibility ?? "InternalOnly", request.FamilyMemberIds ?? Array.Empty<Guid>()),
                ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}

[ApiController]
[Route("api/family")]
public sealed class FamilyPortalGovernanceController(
    IFamilyPortalService familyPortal,
    IFamilyPortalQueryService familyQueries,
    ITenantContext tenant,
    ICurrentUserContext currentUser) : ControllerBase
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
            await familyPortal.AcceptInvitationAsync(request.Token ?? string.Empty, request.Password ?? string.Empty, request.AcceptTerms, ct);
            return Ok(new { status = "Activated", message = "Family Portal account activated. You can now sign in." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [Authorize(Policy = "Phase1User")]
    [HttpGet("people")]
    public async Task<IActionResult> GetAuthorizedPeople(CancellationToken ct)
    {
        if (!TryFamilyMember(out var familyMemberId)) return Forbid();
        return Ok(await familyPortal.GetAuthorizedPeopleAsync(tenant.OrganizationId, familyMemberId, ct));
    }

    [Authorize(Policy = "Phase1User")]
    [HttpGet("service-users/{serviceUserId:guid}/overview")]
    public Task<IActionResult> GetOverview(Guid serviceUserId, CancellationToken ct) =>
        ExecuteFamily(async familyMemberId =>
        {
            var result = await familyQueries.GetOverviewAsync(tenant.OrganizationId, familyMemberId, serviceUserId, ct);
            return result is null ? NotFound() : Ok(result);
        });

    [Authorize(Policy = "Phase1User")]
    [HttpGet("service-users/{serviceUserId:guid}/timeline")]
    public Task<IActionResult> GetTimeline(Guid serviceUserId, CancellationToken ct) =>
        ExecuteFamily(async familyMemberId => Ok(await familyQueries.GetTimelineAsync(tenant.OrganizationId, familyMemberId, serviceUserId, ct)));

    [Authorize(Policy = "Phase1User")]
    [HttpGet("service-users/{serviceUserId:guid}/visits")]
    public Task<IActionResult> GetVisits(Guid serviceUserId, CancellationToken ct) =>
        ExecuteFamily(async familyMemberId => Ok(await familyQueries.GetVisitsAsync(tenant.OrganizationId, familyMemberId, serviceUserId, false, ct)));

    [Authorize(Policy = "Phase1User")]
    [HttpGet("service-users/{serviceUserId:guid}/appointments")]
    public Task<IActionResult> GetAppointments(Guid serviceUserId, CancellationToken ct) =>
        ExecuteFamily(async familyMemberId => Ok(await familyQueries.GetVisitsAsync(tenant.OrganizationId, familyMemberId, serviceUserId, true, ct)));

    [Authorize(Policy = "Phase1User")]
    [HttpGet("service-users/{serviceUserId:guid}/documents")]
    public Task<IActionResult> GetDocuments(Guid serviceUserId, CancellationToken ct) =>
        ExecuteFamily(async familyMemberId => Ok(await familyQueries.GetDocumentsAsync(tenant.OrganizationId, familyMemberId, serviceUserId, ct)));

    [Authorize(Policy = "Phase1User")]
    [HttpGet("service-users/{serviceUserId:guid}/monthly-report")]
    public Task<IActionResult> GetMonthlyReport(Guid serviceUserId, CancellationToken ct) =>
        ExecuteFamily(async familyMemberId =>
        {
            var result = await familyQueries.GetMonthlyReportAsync(tenant.OrganizationId, familyMemberId, serviceUserId, ct);
            return result is null ? NotFound() : Ok(result);
        });

    [Authorize(Policy = "Phase1User")]
    [HttpGet("service-users/{serviceUserId:guid}/preferences")]
    public Task<IActionResult> GetPreferences(Guid serviceUserId, CancellationToken ct) =>
        ExecuteFamily(async familyMemberId => Ok(await familyQueries.GetPreferencesAsync(tenant.OrganizationId, familyMemberId, serviceUserId, ct)));

    [Authorize(Policy = "Phase1User")]
    [HttpPut("service-users/{serviceUserId:guid}/preferences")]
    public Task<IActionResult> SavePreferences(Guid serviceUserId, SaveFamilyNotificationPreferencesRequest request, CancellationToken ct) =>
        ExecuteFamily(async familyMemberId => Ok(await familyQueries.SavePreferencesAsync(
            tenant.OrganizationId,
            familyMemberId,
            serviceUserId,
            new SaveFamilyNotificationPreferencesCommand(
                request.EmailUpdates,
                request.SmsAlerts,
                request.MonthlyDigest,
                request.IncidentAlerts,
                request.CarePlanSignatureRequests,
                request.CarePlanUpdates,
                request.AppointmentReminders,
                request.VisitUpdates,
                request.DocumentShared,
                request.NewMessages,
                request.ComplaintResponses,
                request.ExpectedRevision),
            ct)));

    [Authorize(Policy = "Phase1User")]
    [HttpPost("feedback")]
    public async Task<IActionResult> SubmitFeedback(SubmitFamilyFeedbackRequest request, CancellationToken ct)
    {
        if (!TryFamilyMember(out var familyMemberId)) return Forbid();
        try
        {
            var result = await familyPortal.SubmitFeedbackAsync(
                tenant.OrganizationId,
                tenant.BranchId,
                familyMemberId,
                new FamilyFeedbackInput(request.ServiceUserId, request.Type ?? "Feedback", request.Subject ?? string.Empty, request.Description ?? string.Empty, request.Priority ?? "Routine"),
                ct);
            return Created($"/api/family/feedback/{result.Id}", result);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private async Task<IActionResult> ExecuteFamily(Func<Guid, Task<IActionResult>> action)
    {
        if (!TryFamilyMember(out var familyMemberId)) return Forbid();
        try
        {
            return await action(familyMemberId);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message.Contains("changed elsewhere", StringComparison.OrdinalIgnoreCase)
                ? Conflict(new { message = ex.Message })
                : BadRequest(new { message = ex.Message });
        }
    }

    private bool TryFamilyMember(out Guid familyMemberId)
    {
        familyMemberId = currentUser.FamilyMemberId ?? Guid.Empty;
        return currentUser.IsFamilyMember && familyMemberId != Guid.Empty;
    }
}

public sealed record ConfigureFamilyAccessRequest(
    string? AuthorityType,
    string? VerificationStatus,
    string? VerificationReference,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidUntil,
    IReadOnlyCollection<string>? Permissions,
    long? ExpectedRevision);

public sealed record SetFamilyDocumentVisibilityRequest(string? Visibility, IReadOnlyCollection<Guid>? FamilyMemberIds);
public sealed record ValidateFamilyInvitationRequest(string? Token);
public sealed record AcceptFamilyInvitationRequest(string? Token, string? Password, bool AcceptTerms);
public sealed record SubmitFamilyFeedbackRequest(Guid ServiceUserId, string? Type, string? Subject, string? Description, string? Priority);
public sealed record SaveFamilyNotificationPreferencesRequest(
    bool EmailUpdates,
    bool SmsAlerts,
    bool MonthlyDigest,
    bool IncidentAlerts,
    bool CarePlanSignatureRequests,
    bool CarePlanUpdates,
    bool AppointmentReminders,
    bool VisitUpdates,
    bool DocumentShared,
    bool NewMessages,
    bool ComplaintResponses,
    long? ExpectedRevision);
