using System.Data;
using System.Data.Common;
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

        var structured = await ReadGovernanceCounts(person.Id, cancellationToken);
        var contactCount = structured?.ContactCount ?? familyCount;
        var emergencyContactCount = structured?.EmergencyContactCount ?? 0;
        var activeConsentCount = structured?.ActiveConsentCount ?? 0;
        var capacityDecisionCount = structured?.CapacityDecisionCount ?? 0;
        var activeFundingCount = structured?.ActiveFundingCount ?? 0;

        var hasIdentity = !string.IsNullOrWhiteSpace(person.FullName)
            && person.DateOfBirth != default
            && !string.IsNullOrWhiteSpace(person.Address)
            && !string.IsNullOrWhiteSpace(person.PhoneNumber);
        var hasEmergencyContact = emergencyContactCount > 0 || familyCount > 0 || !string.IsNullOrWhiteSpace(person.EmergencyContact);
        var hasConsent = activeConsentCount > 0 || (record is not null
            && !string.IsNullOrWhiteSpace(record.ConsentStatus)
            && !string.Equals(record.ConsentStatus, "Not recorded", StringComparison.OrdinalIgnoreCase));
        var hasCapacity = capacityDecisionCount > 0 || (record is not null
            && !string.IsNullOrWhiteSpace(record.MentalCapacityStatus)
            && !string.Equals(record.MentalCapacityStatus, "Not assessed", StringComparison.OrdinalIgnoreCase));
        var hasPersonCentredRecord = record is not null
            && !string.IsNullOrWhiteSpace(record.WhatMattersToMe)
            && !string.IsNullOrWhiteSpace(record.DesiredOutcomes);
        var hasFunding = activeFundingCount > 0 || !string.IsNullOrWhiteSpace(person.FundingSource);
        var hasAssessment = assessmentCount > 0;
        var hasActivePlan = plans.Any(item => string.Equals(item.Status, "Active", StringComparison.OrdinalIgnoreCase));

        var checks = new[]
        {
            new PersonOnboardingCheck("identity", "Identity and contact details", hasIdentity, "Name, DOB, address and phone are recorded."),
            new PersonOnboardingCheck("emergency-contact", "Emergency or family contact", hasEmergencyContact, "At least one emergency/family contact is recorded."),
            new PersonOnboardingCheck("consent", "Consent decision", hasConsent, "An active consent decision is recorded with its scope."),
            new PersonOnboardingCheck("capacity", "Mental capacity decision", hasCapacity, "Capacity basis or a best-interest/legal representative decision is recorded."),
            new PersonOnboardingCheck("person-centred-record", "Person-centred record", hasPersonCentredRecord, "What matters to the person and desired outcomes are recorded."),
            new PersonOnboardingCheck("funding", "Funding arrangement", hasFunding, "An active funding source or arrangement is recorded."),
            new PersonOnboardingCheck("assessment", "Completed assessment", hasAssessment, "At least one assessment is recorded."),
            new PersonOnboardingCheck("care-plan", "Active signed care plan", hasActivePlan, "At least one governed care-plan version has completed review, signatures and activation."),
        };

        return new PersonOnboardingStatus(
            person.Id,
            person.Status,
            checks.All(item => item.Complete),
            checks,
            contactCount,
            activeConsentCount,
            activeFundingCount,
            assessmentCount,
            plans.Count,
            documentCount,
            DateTimeOffset.UtcNow);
    }

    private async Task<GovernanceCounts?> ReadGovernanceCounts(Guid serviceUserId, CancellationToken cancellationToken)
    {
        try
        {
            var connection = _context.Database.GetDbConnection();
            var openedHere = connection.State != ConnectionState.Open;
            if (openedHere) await connection.OpenAsync(cancellationToken);
            try
            {
                var contacts = await Scalar(connection, "select count(*) from person_contacts where service_user_id=@person and organization_id=@organization", serviceUserId, cancellationToken);
                var emergency = await Scalar(connection, "select count(*) from person_contacts where service_user_id=@person and organization_id=@organization and is_emergency=true", serviceUserId, cancellationToken);
                var consents = await Scalar(connection, "select count(*) from consent_records where service_user_id=@person and organization_id=@organization and status='Active'", serviceUserId, cancellationToken);
                var capacity = await Scalar(connection, "select count(*) from consent_records where service_user_id=@person and organization_id=@organization and status='Active' and lower(capacity_basis) not in ('not recorded','capacity assessment pending')", serviceUserId, cancellationToken);
                var funding = await Scalar(connection, "select count(*) from funding_arrangements where service_user_id=@person and organization_id=@organization and status='Active'", serviceUserId, cancellationToken);
                return new GovernanceCounts(contacts, emergency, consents, capacity, funding);
            }
            finally
            {
                if (openedHere) await connection.CloseAsync();
            }
        }
        catch (DbException)
        {
            return null;
        }
    }

    private async Task<int> Scalar(DbConnection connection, string sql, Guid serviceUserId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var person = command.CreateParameter();
        person.ParameterName = "person";
        person.Value = serviceUserId;
        command.Parameters.Add(person);
        var organization = command.CreateParameter();
        organization.ParameterName = "organization";
        organization.Value = _tenant.OrganizationId;
        command.Parameters.Add(organization);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private sealed record GovernanceCounts(int ContactCount, int EmergencyContactCount, int ActiveConsentCount, int CapacityDecisionCount, int ActiveFundingCount);
}

public sealed record PersonOnboardingCheck(string Key, string Label, bool Complete, string Requirement);

public sealed record PersonOnboardingStatus(
    Guid ServiceUserId,
    string CurrentStatus,
    bool ReadyForActivation,
    IReadOnlyCollection<PersonOnboardingCheck> Checks,
    int ContactCount,
    int ActiveConsentCount,
    int ActiveFundingCount,
    int AssessmentCount,
    int CarePlanCount,
    int DocumentCount,
    DateTimeOffset EvaluatedAt);
