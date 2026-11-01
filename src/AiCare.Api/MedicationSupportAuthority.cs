using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

public static class MedicationSupportAuthority
{
    public const string MissingMessage = "An active, current care-plan version must explicitly authorise administration of this medication before a MAR dose can be scheduled or administered.";

    public static async Task<bool> AllowsAdministrationAsync(
        CareDbContext db, Guid organizationId, Guid branchId, Guid personId, Guid medicationId,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        var matches = await db.Database.SqlQueryRaw<int>("""
            select 1 as "Value"
            from care_plan_medication_authorities authority
            join care_plan_versions version on version.care_plan_id=authority.care_plan_id
            join "CarePlans" plan on plan."Id"=authority.care_plan_id
            where authority.organization_id={0} and authority.branch_id={1}
              and authority.service_user_id={2} and authority.medication_id={3}
              and authority.support_type='Administer' and authority.review_due_at>{4}
              and version.organization_id=authority.organization_id
              and version.branch_id=authority.branch_id
              and version.service_user_id=authority.service_user_id
              and version.status='Active'
              and plan."ServiceUserId"=authority.service_user_id
              and plan."OrganizationId"=authority.organization_id
              and plan."BranchId"=authority.branch_id
              and plan."Status"='Active'
              and plan."ReviewDueAt">{4}
            limit 1
            for share of authority,version,plan
            """, organizationId, branchId, personId, medicationId, at)
            .ToListAsync(cancellationToken);
        return matches.Count > 0;
    }
}
