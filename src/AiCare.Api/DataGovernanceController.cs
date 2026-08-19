using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[ApiController]
[Route("api/phase1/data-governance")]
[Authorize(Policy = "Phase1User")]
public sealed class DataGovernanceController(
    CareDbContext db,
    ITenantContext tenant,
    ICurrentUserContext currentUser) : ControllerBase
{
    [HttpGet("retention-policies")]
    public async Task<IActionResult> GetRetentionPolicies(CancellationToken ct)
    {
        if (!IsGovernanceStaff()) return Forbid();
        var policies = await db.RetentionPolicies.AsNoTracking()
            .Where(x => x.OrganizationId == tenant.OrganizationId)
            .OrderBy(x => x.DataCategory)
            .ToListAsync(ct);
        return Ok(policies);
    }

    [HttpPut("retention-policies/{dataCategory}")]
    public async Task<IActionResult> UpsertRetentionPolicy(string dataCategory, UpsertRetentionPolicyRequest request, CancellationToken ct)
    {
        if (!currentUser.IsAdministrator) return Forbid();
        dataCategory = (dataCategory ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(dataCategory)) return BadRequest(new { message = "Data category is required." });
        if (request.RetentionDays < 30 || request.RetentionDays > 36500) return BadRequest(new { message = "Retention days must be between 30 and 36500." });
        if (string.IsNullOrWhiteSpace(request.LegalBasis)) return BadRequest(new { message = "Legal basis is required." });
        if (request.DispositionAction is not ("Review" or "Anonymize" or "Delete")) return BadRequest(new { message = "Disposition action must be Review, Anonymize, or Delete." });
        if (request.ReviewDueAt <= DateTimeOffset.UtcNow) return BadRequest(new { message = "Review due date must be in the future." });

        var existing = await db.RetentionPolicies.FirstOrDefaultAsync(
            x => x.OrganizationId == tenant.OrganizationId && x.DataCategory == dataCategory, ct);
        var policy = new RetentionPolicy(
            existing?.Id ?? Guid.NewGuid(),
            dataCategory,
            request.RetentionDays,
            request.LegalBasis.Trim(),
            request.DispositionAction,
            request.IsActive,
            request.ReviewDueAt,
            DateTimeOffset.UtcNow,
            tenant.OrganizationId,
            tenant.BranchId);

        if (existing is null) db.RetentionPolicies.Add(policy);
        else db.Entry(existing).CurrentValues.SetValues(policy);

        AddAudit("governance.retention_policy_updated", nameof(RetentionPolicy), policy.Id);
        await db.SaveChangesAsync(ct);
        return Ok(policy);
    }

    [HttpGet("service-users/{serviceUserId:guid}/export")]
    public async Task<IActionResult> ExportSubjectData(Guid serviceUserId, CancellationToken ct)
    {
        if (!IsGovernanceStaff()) return Forbid();
        var person = await db.ServiceUsers.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == serviceUserId && x.OrganizationId == tenant.OrganizationId, ct);
        if (person is null || !tenant.CanAccess(person.OrganizationId, person.BranchId)) return NotFound();

        var request = new DataGovernanceRequest(
            Guid.NewGuid(), serviceUserId, "SubjectAccessExport", "Completed", currentUser.UserName,
            "Authorized data subject export", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            tenant.OrganizationId, tenant.BranchId);
        db.DataGovernanceRequests.Add(request);
        AddAudit("governance.subject_exported", nameof(ServiceUser), serviceUserId);
        await db.SaveChangesAsync(ct);

        var payload = new
        {
            generatedAt = DateTimeOffset.UtcNow,
            requestId = request.Id,
            serviceUser = person,
            personRecord = await db.PersonRecords.AsNoTracking().FirstOrDefaultAsync(x => x.ServiceUserId == serviceUserId && x.OrganizationId == tenant.OrganizationId, ct),
            assessments = await db.CareAssessments.AsNoTracking().Where(x => x.ServiceUserId == serviceUserId && x.OrganizationId == tenant.OrganizationId).ToListAsync(ct),
            carePlans = await db.CarePlans.AsNoTracking().Where(x => x.ServiceUserId == serviceUserId && x.OrganizationId == tenant.OrganizationId).ToListAsync(ct),
            risks = await db.RiskAssessments.AsNoTracking().Where(x => x.ServiceUserId == serviceUserId && x.OrganizationId == tenant.OrganizationId).ToListAsync(ct),
            family = await db.FamilyMembers.AsNoTracking().Where(x => x.ServiceUserId == serviceUserId && x.OrganizationId == tenant.OrganizationId).ToListAsync(ct),
            visits = await db.Visits.AsNoTracking().Where(x => x.ServiceUserId == serviceUserId && x.OrganizationId == tenant.OrganizationId).ToListAsync(ct),
            medications = await db.Medications.AsNoTracking().Where(x => x.ServiceUserId == serviceUserId && x.OrganizationId == tenant.OrganizationId).ToListAsync(ct),
            careNotes = await db.CareNotes.AsNoTracking().Where(x => x.ServiceUserId == serviceUserId && x.OrganizationId == tenant.OrganizationId).ToListAsync(ct),
            observations = await db.HealthObservations.AsNoTracking().Where(x => x.ServiceUserId == serviceUserId && x.OrganizationId == tenant.OrganizationId).ToListAsync(ct),
            incidents = await db.Incidents.AsNoTracking().Where(x => x.ServiceUserId == serviceUserId && x.OrganizationId == tenant.OrganizationId).ToListAsync(ct),
            documents = await db.Documents.AsNoTracking().Where(x => x.ServiceUserId == serviceUserId && x.OrganizationId == tenant.OrganizationId).Select(x => new { x.Id, x.FileName, x.Category, x.UploadedBy, x.UploadedAt }).ToListAsync(ct),
            invoices = await db.Invoices.AsNoTracking().Where(x => x.ServiceUserId == serviceUserId && x.OrganizationId == tenant.OrganizationId).ToListAsync(ct)
        };
        return Ok(payload);
    }

    [HttpPost("service-users/{serviceUserId:guid}/anonymize")]
    public async Task<IActionResult> Anonymize(Guid serviceUserId, AnonymizeServiceUserRequest request, CancellationToken ct)
    {
        if (!currentUser.IsAdministrator) return Forbid();
        if (!string.Equals(request.Confirmation, "ANONYMIZE", StringComparison.Ordinal))
            return BadRequest(new { message = "Confirmation must exactly equal ANONYMIZE." });
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new { message = "A governance reason is required." });

        var person = await db.ServiceUsers.FirstOrDefaultAsync(x => x.Id == serviceUserId && x.OrganizationId == tenant.OrganizationId, ct);
        if (person is null || !tenant.CanAccess(person.OrganizationId, person.BranchId)) return NotFound();
        if (string.Equals(person.Status, "Active", StringComparison.OrdinalIgnoreCase))
            return Conflict(new { message = "Active service users cannot be anonymized. Complete discharge/closure first." });

        var now = DateTimeOffset.UtcNow;
        if (await db.Visits.AnyAsync(x => x.OrganizationId == tenant.OrganizationId && x.ServiceUserId == serviceUserId && x.StartsAt > now, ct))
            return Conflict(new { message = "Future visits must be cancelled or completed before anonymisation." });
        if (await db.Incidents.AnyAsync(x => x.OrganizationId == tenant.OrganizationId && x.ServiceUserId == serviceUserId && x.Status != "Closed", ct))
            return Conflict(new { message = "Open incidents must be resolved before anonymisation." });

        var governanceRequest = new DataGovernanceRequest(
            Guid.NewGuid(), serviceUserId, "Anonymization", "Completed", currentUser.UserName,
            request.Reason.Trim(), now, now, tenant.OrganizationId, tenant.BranchId);
        db.DataGovernanceRequests.Add(governanceRequest);

        var anonymousLabel = $"Anonymized-{serviceUserId:N}";
        db.Entry(person).CurrentValues.SetValues(person with
        {
            FullName = anonymousLabel,
            PhoneNumber = "REDACTED",
            EmergencyContact = "REDACTED",
            Address = "REDACTED",
            PhotoUrl = string.Empty,
            Gender = "REDACTED",
            CulturalPreferences = "REDACTED",
            Status = "Anonymized"
        });

        var record = await db.PersonRecords.FirstOrDefaultAsync(x => x.OrganizationId == tenant.OrganizationId && x.ServiceUserId == serviceUserId, ct);
        if (record is not null)
        {
            db.Entry(record).CurrentValues.SetValues(record with
            {
                PreferredName = anonymousLabel,
                Pronouns = "REDACTED",
                HealthIdentifier = "REDACTED",
                GpDetails = "REDACTED",
                PharmacyDetails = "REDACTED",
                LegalRepresentative = "REDACTED",
                CommunicationPassport = "REDACTED",
                PersonalHistory = "REDACTED"
            });
        }

        var family = await db.FamilyMembers.Where(x => x.OrganizationId == tenant.OrganizationId && x.ServiceUserId == serviceUserId).ToListAsync(ct);
        foreach (var member in family)
        {
            db.Entry(member).CurrentValues.SetValues(member with
            {
                FullName = "Anonymized family member",
                Email = $"anonymized-{member.Id:N}@invalid.local",
                Status = "Revoked"
            });
        }
        var familyIds = family.Select(x => x.Id).ToList();
        var familyUsers = await db.AppUsers.Where(x => x.OrganizationId == tenant.OrganizationId && x.FamilyMemberId != null && familyIds.Contains(x.FamilyMemberId.Value)).ToListAsync(ct);
        foreach (var user in familyUsers)
            db.Entry(user).CurrentValues.SetValues(user with { IsActive = false, Email = $"anonymized-{user.Id:N}@invalid.local", UserName = $"anonymized-{user.Id:N}" });

        AddAudit("governance.service_user_anonymized", nameof(ServiceUser), serviceUserId);
        await db.SaveChangesAsync(ct);
        return Ok(new { requestId = governanceRequest.Id, serviceUserId, status = "Anonymized", completedAt = now });
    }

    [HttpGet("requests")]
    public async Task<IActionResult> GetGovernanceRequests(CancellationToken ct)
    {
        if (!IsGovernanceStaff()) return Forbid();
        var requests = await db.DataGovernanceRequests.AsNoTracking()
            .Where(x => x.OrganizationId == tenant.OrganizationId)
            .OrderByDescending(x => x.RequestedAt)
            .Take(250)
            .ToListAsync(ct);
        return Ok(requests);
    }

    private bool IsGovernanceStaff() => currentUser.HasAnyRole(UserRole.Administrator, UserRole.CareManager);

    private void AddAudit(string action, string entityType, Guid entityId) => db.AuditEvents.Add(new AuditEvent(
        Guid.NewGuid(), action, currentUser.UserName, entityType, entityId, DateTimeOffset.UtcNow,
        tenant.OrganizationId, tenant.BranchId));
}

public sealed record UpsertRetentionPolicyRequest(
    int RetentionDays,
    string LegalBasis,
    string DispositionAction,
    bool IsActive,
    DateTimeOffset ReviewDueAt);

public sealed record AnonymizeServiceUserRequest(string Confirmation, string Reason);
