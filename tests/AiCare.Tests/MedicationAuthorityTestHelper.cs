using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiCare.Tests;

internal static class MedicationAuthorityTestHelper
{
    // Synthetic active-plan fixture. Lifecycle transition tests exercise approval and activation separately.
    public static async Task SeedAsync(PostgresRegressionFactory factory, Guid personId, Guid medicationId, string supportType = "Administer")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        var activePlans = await db.Database.SqlQueryRaw<Guid>("""
            select care_plan_id as "Value" from care_plan_versions
            where organization_id={0} and branch_id={1} and service_user_id={2} and status='Active'
            order by version_number desc limit 1
            """, TenantDefaults.OrganizationId, TenantDefaults.BranchId, personId).ToListAsync();
        var previousPlanId = activePlans.FirstOrDefault();
        var carePlanId = Guid.NewGuid();
        db.CarePlans.Add(new CarePlan(carePlanId, personId, "v1", "Draft", "Regression care",
            "Administer medication", "Regression mobility", "Regression nutrition",
            DateTimeOffset.UtcNow.AddMonths(6), TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        await db.SaveChangesAsync();
        var adminId = (await db.AppUsers.AsNoTracking().SingleAsync(x => x.UserName == "admin")).Id;
        if (previousPlanId != Guid.Empty)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                insert into care_plan_medication_authorities(
                    care_plan_id,medication_id,service_user_id,organization_id,branch_id,
                    support_type,instructions,review_due_at,family_medicine_applies,
                    family_recording_method,family_responsible_person,family_escalation_route,
                    prior_dose_check,updated_by_user_id)
                select {carePlanId},medication_id,service_user_id,organization_id,branch_id,
                    support_type,instructions,review_due_at,family_medicine_applies,
                    family_recording_method,family_responsible_person,family_escalation_route,
                    prior_dose_check,{adminId}
                from care_plan_medication_authorities where care_plan_id={previousPlanId}
                """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into care_plan_medication_authorities(
                care_plan_id,medication_id,service_user_id,organization_id,branch_id,
                support_type,instructions,review_due_at,family_medicine_applies,
                family_recording_method,family_responsible_person,family_escalation_route,
                prior_dose_check,updated_by_user_id)
            values({carePlanId},{medicationId},{personId},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},
                {supportType},'Regression approved administration',{DateTimeOffset.UtcNow.AddMonths(6)},
                false,'','','','Check current MAR and care plan before giving', {adminId})
            on conflict(care_plan_id,medication_id) do update set
                support_type=excluded.support_type,review_due_at=excluded.review_due_at
            """);
        if (previousPlanId != Guid.Empty)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                update care_plan_versions set status='Superseded' where care_plan_id={previousPlanId}
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                update "CarePlans" set "Status"='Superseded' where "Id"={previousPlanId}
                """);
        }
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            update care_plan_versions set status='Active' where care_plan_id={carePlanId}
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            update "CarePlans" set "Status"='Active' where "Id"={carePlanId}
            """);
    }
}
