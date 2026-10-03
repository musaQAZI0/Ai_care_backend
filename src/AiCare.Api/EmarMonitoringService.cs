using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace AiCare.Api;

public sealed class EmarMonitoringService(CareDbContext db, IConfiguration configuration)
{
    public async Task<(int OverdueDoses, int OverduePrnReviews, int MedicationAlerts)> ScanAsync(CancellationToken token)
    {
        var pilotBranchId = Guid.TryParse(configuration["MedicationSafety:PilotBranchId"], out var configuredBranch)
            ? configuredBranch : (Guid?)null;
        NpgsqlParameter BranchParameter() => new("pilotBranchId", NpgsqlDbType.Uuid)
        {
            Value = pilotBranchId.HasValue ? (object)pilotBranchId.Value : DBNull.Value
        };
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var overdueDoses = await db.Database.ExecuteSqlRawAsync("""
                insert into emar_escalations(id,mar_record_id,ledger_id,organization_id,branch_id,
                  escalation_type,severity,status,message,owner,due_at)
                select gen_random_uuid(),r."Id",null,r."OrganizationId",r."BranchId",
                  'OverdueDose','High','Open','Scheduled dose remains unrecorded after its permitted window.',
                  'Medication lead',r."ScheduledAt" + p.dose_window_minutes * interval '1 minute'
                from "MedicationAdministrationRecords" r
                join medication_safety_profiles p on p.medication_id=r."MedicationId"
                  and p.organization_id=r."OrganizationId" and p.branch_id=r."BranchId"
                where r."Outcome"='Scheduled' and p.reconciliation_status='Verified'
                  and (@pilotBranchId is null or r."BranchId"=@pilotBranchId)
                  and r."ScheduledAt" + p.dose_window_minutes * interval '1 minute' < now()
                on conflict do nothing
                """,[BranchParameter()],token);
            var overduePrnReviews = await db.Database.ExecuteSqlRawAsync("""
                update emar_escalations set severity='High',
                  message='PRN effect review is overdue. Record the observed effect or escalate to the medication lead.'
                where escalation_type='PRNEffectReview' and due_at<=now()
                  and (@pilotBranchId is null or branch_id=@pilotBranchId)
                  and status in ('Open','Acknowledged','InProgress') and severity<>'High'
                """,[BranchParameter()],token);
            var reviewAlerts = await db.Database.ExecuteSqlRawAsync("""
                insert into medication_monitoring_alerts(id,medication_id,organization_id,branch_id,
                  alert_type,due_at,severity,message,owner)
                select gen_random_uuid(),p.medication_id,p.organization_id,p.branch_id,
                  'ReviewDue',p.review_due_at,
                  case when p.review_due_at<now() then 'High' else 'Medium' end,
                  case when p.review_due_at<now() then 'Medication review is overdue.'
                       else 'Medication review is due within 14 days.' end,'Medication lead'
                from medication_safety_profiles p
                where p.reconciliation_status='Verified' and p.review_due_at is not null
                  and (@pilotBranchId is null or p.branch_id=@pilotBranchId)
                  and p.review_due_at<=now()+interval '14 days'
                on conflict do nothing
                """,[BranchParameter()],token);
            var endAlerts = await db.Database.ExecuteSqlRawAsync("""
                insert into medication_monitoring_alerts(id,medication_id,organization_id,branch_id,
                  alert_type,due_at,severity,message,owner)
                select gen_random_uuid(),p.medication_id,p.organization_id,p.branch_id,
                  'EndDateApproaching',p.end_date,
                  case when p.end_date<now() then 'High' else 'Medium' end,
                  case when p.end_date<now() then 'Medication prescription end date has passed.'
                       else 'Medication prescription end date is within 7 days.' end,'Medication lead'
                from medication_safety_profiles p
                where p.reconciliation_status='Verified' and p.end_date is not null
                  and (@pilotBranchId is null or p.branch_id=@pilotBranchId)
                  and p.end_date<=now()+interval '7 days'
                on conflict do nothing
                """,[BranchParameter()],token);
            await transaction.CommitAsync(token);
            return (overdueDoses,overduePrnReviews,reviewAlerts+endAlerts);
        });
    }
}
