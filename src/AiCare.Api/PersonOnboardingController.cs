using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[ApiController]
[Route("api/phase1/service-users/{serviceUserId:guid}/onboarding")]
[Authorize(Roles = "CareCoordinator,CareManager,Administrator")]
public sealed class PersonOnboardingController : ControllerBase
{
    private readonly CareDbContext _context;
    private readonly ITenantContext _tenant;
    private readonly ICurrentUserContext _currentUser;

    public PersonOnboardingController(CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser)
    {
        _context = context;
        _tenant = tenant;
        _currentUser = currentUser;
    }

    [HttpGet("status")]
    public async Task<ActionResult<PersonOnboardingStatus>> GetStatus(Guid serviceUserId, CancellationToken cancellationToken)
    {
        var person = await FindAccessiblePerson(serviceUserId, cancellationToken);
        if (person is null) return NotFound();

        return Ok(await BuildStatus(person, cancellationToken));
    }

    [HttpPost("activate")]
    public async Task<ActionResult<PersonOnboardingStatus>> Activate(Guid serviceUserId, CancellationToken cancellationToken)
    {
        var person = await FindAccessiblePerson(serviceUserId, cancellationToken);
        if (person is null) return NotFound();

        var status = await BuildStatus(person, cancellationToken);
        if (!status.ReadyForActivation)
        {
            return BadRequest(new
            {
                message = "The person record is not ready for activation.",
                missing = status.Checks.Where(item => !item.Complete).Select(item => item.Label).ToArray()
            });
        }

        if (!string.Equals(person.Status, "Active", StringComparison.OrdinalIgnoreCase))
        {
            _context.Entry(person).CurrentValues.SetValues(person with { Status = "Active" });
            _context.AuditEvents.Add(new AuditEvent(
                Guid.NewGuid(),
                "service_user.activated",
                _currentUser.UserName,
                nameof(ServiceUser),
                person.Id,
                DateTimeOffset.UtcNow,
                person.OrganizationId,
                person.BranchId));
            await _context.SaveChangesAsync(cancellationToken);
        }

        var refreshed = await FindAccessiblePerson(serviceUserId, cancellationToken);
        return Ok(await BuildStatus(refreshed!, cancellationToken));
    }

    private async Task<ServiceUser?> FindAccessiblePerson(Guid serviceUserId, CancellationToken cancellationToken)
    {
        var person = await _context.ServiceUsers.SingleOrDefaultAsync(item => item.Id == serviceUserId, cancellationToken);
        return person is not null && _tenant.CanAccess(person.OrganizationId, person.BranchId) ? person : null;
    }

    private async Task<PersonOnboardingStatus> BuildStatus(ServiceUser person, CancellationToken cancellationToken)
    {
        var record = await _context.PersonRecords.SingleOrDefaultAsync(item => item.ServiceUserId == person.Id, cancellationToken);
        var familyCount = await _context.FamilyMembers.CountAsync(item => item.ServiceUserId == person.Id, cancellationToken);
        var assessmentCount = await _context.CareAssessments.CountAsync(item => item.ServiceUserId == person.Id, cancellationToken);
        var plans = await _context.CarePlans.Where(item => item.ServiceUserId == person.Id).ToListAsync(cancellationToken);
        var documentCount = await _context.Documents.CountAsync(item => item.ServiceUserId == person.Id, cancellationToken);

        var hasIdentity = !string.IsNullOrWhiteSpace(person.FullName)
            && person.DateOfBirth != default
            && !string.IsNullOrWhiteSpace(person.Address)
            && !string.IsNullOrWhiteSpace(person.PhoneNumber);
        var hasEmergencyContact = familyCount > 0 || !string.IsNullOrWhiteSpace(person.EmergencyContact);
        var hasConsent = record is not null && !string.IsNullOrWhiteSpace(record.ConsentStatus)
            && !string.Equals(record.ConsentStatus, "Not recorded", StringComparison.OrdinalIgnoreCase);
        var hasCapacity = record is not null && !string.IsNullOrWhiteSpace(record.MentalCapacityStatus)
            && !string.Equals(record.MentalCapacityStatus, "Not assessed", StringComparison.OrdinalIgnoreCase);
        var hasPersonCentredRecord = record is not null
            && !string.IsNullOrWhiteSpace(record.WhatMattersToMe)
            && !string.IsNullOrWhiteSpace(record.DesiredOutcomes);
        var hasFunding = !string.IsNullOrWhiteSpace(person.FundingSource);
        var hasAssessment = assessmentCount > 0;
        var hasApprovedPlan = plans.Any(item => item.Status is "Active" or "Approved");

        var checks = new[]
        {
            new PersonOnboardingCheck("identity", "Identity and contact details", hasIdentity, "Name, DOB, address and phone are recorded."),
            new PersonOnboardingCheck("emergency-contact", "Emergency or family contact", hasEmergencyContact, "At least one emergency/family contact is recorded."),
            new PersonOnboardingCheck("consent", "Consent decision", hasConsent, "Consent status is explicitly recorded."),
            new PersonOnboardingCheck("capacity", "Mental capacity decision", hasCapacity, "Capacity status is explicitly recorded."),
            new PersonOnboardingCheck("person-centred-record", "Person-centred record", hasPersonCentredRecord, "What matters to the person and desired outcomes are recorded."),
            new PersonOnboardingCheck("funding", "Funding source", hasFunding, "A funding source is recorded."),
            new PersonOnboardingCheck("assessment", "Completed assessment", hasAssessment, "At least one assessment is recorded."),
            new PersonOnboardingCheck("care-plan", "Approved care plan", hasApprovedPlan, "At least one care plan is approved or active."),
        };

        return new PersonOnboardingStatus(
            person.Id,
            person.Status,
            checks.All(item => item.Complete),
            checks,
            familyCount,
            assessmentCount,
            plans.Count,
            documentCount,
            DateTimeOffset.UtcNow);
    }
}

public sealed record PersonOnboardingCheck(string Key, string Label, bool Complete, string Requirement);

public sealed record PersonOnboardingStatus(
    Guid ServiceUserId,
    string CurrentStatus,
    bool ReadyForActivation,
    IReadOnlyCollection<PersonOnboardingCheck> Checks,
    int FamilyContactCount,
    int AssessmentCount,
    int CarePlanCount,
    int DocumentCount,
    DateTimeOffset EvaluatedAt);
