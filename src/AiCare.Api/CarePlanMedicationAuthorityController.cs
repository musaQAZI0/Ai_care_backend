using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[ApiController]
[Authorize(Policy = "Phase1User")]
[Route("api/phase1/care-plans/{carePlanId:guid}/medication-authorities")]
public sealed class CarePlanMedicationAuthorityController(
    CareDbContext db, ITenantContext tenant, ICurrentUserContext user) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid carePlanId, CancellationToken token)
    {
        if (!user.HasAnyRole(UserRole.Administrator, UserRole.CareManager, UserRole.CareCoordinator) || user.UserId is null) return Forbid();
        var branchId = tenant.BranchId ?? TenantDefaults.BranchId;
        if (!await PlanExists(carePlanId, branchId, token)) return NotFound();
        var authorities = await db.Database.SqlQueryRaw<CarePlanMedicationAuthorityView>("""
            select medication_id as "MedicationId",support_type as "SupportType",
                instructions as "Instructions",review_due_at as "ReviewDueAt",
                family_medicine_applies as "FamilyMedicineApplies",
                family_recording_method as "FamilyRecordingMethod",
                family_responsible_person as "FamilyResponsiblePerson",
                family_escalation_route as "FamilyEscalationRoute",
                prior_dose_check as "PriorDoseCheck"
            from care_plan_medication_authorities
            where care_plan_id={0} and organization_id={1} and branch_id={2}
            order by medication_id
            """, carePlanId, tenant.OrganizationId, branchId).ToListAsync(token);
        return Ok(authorities);
    }

    [HttpPut("{medicationId:guid}")]
    public async Task<IActionResult> Save(Guid carePlanId, Guid medicationId, SaveCarePlanMedicationAuthorityRequest request, CancellationToken token)
    {
        if (!user.HasAnyRole(UserRole.Administrator, UserRole.CareManager, UserRole.CareCoordinator) || user.UserId is null) return Forbid();
        if (request.ExpectedRevision < 1 ||
            request.SupportType is not ("Prompt" or "Assist" or "Administer" or "Observe") ||
            string.IsNullOrWhiteSpace(request.Instructions) ||
            string.IsNullOrWhiteSpace(request.PriorDoseCheck) ||
            request.ReviewDueAt <= DateTimeOffset.UtcNow ||
            request.FamilyMedicineApplies &&
                (string.IsNullOrWhiteSpace(request.FamilyRecordingMethod) ||
                 string.IsNullOrWhiteSpace(request.FamilyResponsiblePerson) ||
                 string.IsNullOrWhiteSpace(request.FamilyEscalationRoute)))
            return BadRequest(new { message = "Support type, instructions, current review date, prior-dose check and any family-given medicine arrangement are required." });

        var branchId = tenant.BranchId ?? TenantDefaults.BranchId;
        var plan = await db.CarePlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == carePlanId &&
            x.OrganizationId == tenant.OrganizationId && x.BranchId == branchId, token);
        if (plan is null) return NotFound();
        var medicationBelongsToPerson = await db.Medications.AsNoTracking().AnyAsync(x => x.Id == medicationId &&
            x.ServiceUserId == plan.ServiceUserId && x.OrganizationId == tenant.OrganizationId && x.BranchId == branchId, token);
        if (!medicationBelongsToPerson) return NotFound();

        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var revisions = await db.Database.SqlQueryRaw<long>("""
            select revision as "Value" from care_plan_versions
            where care_plan_id={0} and organization_id={1} and branch_id={2} and status='Draft'
            for update
            """, carePlanId, tenant.OrganizationId, branchId).ToListAsync(token);
        var revision = revisions.SingleOrDefault();
        if (revision != request.ExpectedRevision)
            return (IActionResult)Conflict(new { message = "Only the current Draft care-plan revision can change medication authority." });

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into care_plan_medication_authorities(
                care_plan_id,medication_id,service_user_id,organization_id,branch_id,
                support_type,instructions,review_due_at,family_medicine_applies,
                family_recording_method,family_responsible_person,family_escalation_route,
                prior_dose_check,updated_by_user_id,updated_at)
            values({carePlanId},{medicationId},{plan.ServiceUserId},{tenant.OrganizationId},{branchId},
                {request.SupportType},{request.Instructions.Trim()},{request.ReviewDueAt},
                {request.FamilyMedicineApplies},{(request.FamilyMedicineApplies ? request.FamilyRecordingMethod.Trim() : "")},
                {(request.FamilyMedicineApplies ? request.FamilyResponsiblePerson.Trim() : "")},
                {(request.FamilyMedicineApplies ? request.FamilyEscalationRoute.Trim() : "")},
                {request.PriorDoseCheck.Trim()},{user.UserId.Value},now())
            on conflict(care_plan_id,medication_id) do update set
                support_type=excluded.support_type,instructions=excluded.instructions,
                review_due_at=excluded.review_due_at,family_medicine_applies=excluded.family_medicine_applies,
                family_recording_method=excluded.family_recording_method,
                family_responsible_person=excluded.family_responsible_person,
                family_escalation_route=excluded.family_escalation_route,
                prior_dose_check=excluded.prior_dose_check,
                updated_by_user_id=excluded.updated_by_user_id,updated_at=now()
            """, token);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            update care_plan_versions set revision=revision+1,updated_at=now()
            where care_plan_id={carePlanId} and revision={revision}
            """, token);
        db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "care_plan.medication_authority_saved", user.UserName,
            nameof(CarePlan), carePlanId, DateTimeOffset.UtcNow, tenant.OrganizationId, branchId));
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return (IActionResult)Ok(new { carePlanId, medicationId, revision = revision + 1 });
        });
    }

    private Task<bool> PlanExists(Guid carePlanId, Guid branchId, CancellationToken token) =>
        db.CarePlans.AsNoTracking().AnyAsync(x => x.Id == carePlanId &&
            x.OrganizationId == tenant.OrganizationId && x.BranchId == branchId, token);
}

public sealed record SaveCarePlanMedicationAuthorityRequest(
    long ExpectedRevision, string SupportType, string Instructions, DateTimeOffset ReviewDueAt,
    bool FamilyMedicineApplies, string FamilyRecordingMethod, string FamilyResponsiblePerson,
    string FamilyEscalationRoute, string PriorDoseCheck);

public sealed record CarePlanMedicationAuthorityView(
    Guid MedicationId, string SupportType, string Instructions, DateTimeOffset ReviewDueAt,
    bool FamilyMedicineApplies, string FamilyRecordingMethod, string FamilyResponsiblePerson,
    string FamilyEscalationRoute, string PriorDoseCheck);
