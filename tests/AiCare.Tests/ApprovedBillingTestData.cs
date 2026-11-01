using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Tests;

// Synthetic, explicit policies and attendance for invoice tests which seed validated events.
internal static class ApprovedBillingTestData
{
    internal static async Task Attendance(CareDbContext db,Guid visit)
    {
        var period=Guid.NewGuid();var submitter=Guid.NewGuid();var reviewer=Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync("""
            update "Visits" set "CheckedInAt"=coalesce("CheckedInAt","StartsAt"),"CheckedOutAt"=coalesce("CheckedOutAt","StartsAt"+"DurationMinutes"*interval '1 minute') where "Id"={0};
            insert into timesheet_periods(id,organization_id,branch_id,period_start,period_end,hourly_rate,travel_rate,mileage_rate,created_by)
              select {1},"OrganizationId","BranchId","StartsAt","StartsAt"+interval '1 day',0,0,0,{2} from "Visits" where "Id"={0};
            insert into timesheet_entries(id,period_id,organization_id,branch_id,visit_id,care_worker_id,planned_minutes,actual_minutes,payable_minutes,hourly_rate,status,reason,changed_by,submitted_by,approved_by)
              select gen_random_uuid(),{1},"OrganizationId","BranchId","Id","CareWorkerId","DurationMinutes",extract(epoch from("CheckedOutAt"-"CheckedInAt"))/60,"DurationMinutes",0,'Approved','Explicit synthetic approval',{3},{2},{3} from "Visits" where "Id"={0};
            """,visit,period,submitter,reviewer);
    }

    internal static async Task Bind(CareDbContext db, Guid billable)
    {
        var contract=Guid.NewGuid(); var period=Guid.NewGuid(); var entry=Guid.NewGuid(); var submitter=Guid.NewGuid(); var reviewer=Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync("""
            update "Visits" v set "CheckedInAt"=coalesce(v."CheckedInAt",v."StartsAt"),"CheckedOutAt"=coalesce(v."CheckedOutAt",v."StartsAt"+v."DurationMinutes"*interval '1 minute'),
              "VisitType"=card.service_type
            from finance_billable_events e join finance_rate_versions rv on rv.id=e.rate_version_id join finance_rate_cards card on card.id=rv.rate_card_id
            where e.id={0} and v."Id"=e.visit_id;
            insert into timesheet_periods(id,organization_id,branch_id,period_start,period_end,hourly_rate,travel_rate,mileage_rate,created_by)
              select {1},e.organization_id,e.branch_id,v."StartsAt",v."StartsAt"+interval '1 day',0,0,0,{3}
              from finance_billable_events e join "Visits" v on v."Id"=e.visit_id where e.id={0};
            insert into timesheet_entries(id,period_id,organization_id,branch_id,visit_id,care_worker_id,planned_minutes,actual_minutes,payable_minutes,hourly_rate,status,reason,changed_by,submitted_by,approved_by)
              select {2},{1},e.organization_id,e.branch_id,e.visit_id,v."CareWorkerId",v."DurationMinutes",extract(epoch from(v."CheckedOutAt"-v."CheckedInAt"))/60,extract(epoch from(v."CheckedOutAt"-v."CheckedInAt"))/60,0,'Approved','Explicit invoice test attendance',{4},{3},{4}
              from finance_billable_events e join "Visits" v on v."Id"=e.visit_id where e.id={0} on conflict(organization_id,visit_id) do nothing;
            insert into finance_contract_versions(id,organization_id,branch_id,funding_arrangement_id,person_id,funder_id,rate_version_id,version,service_type,currency,
              authorised_from,authorised_to,authorisation_reference,billing_basis,cancellation_policy,rounding_mode,policy_reference,cap_mode,created_by,status,approved_by,approved_at,request_snapshot)
              select {5},e.organization_id,e.branch_id,e.funding_arrangement_id,e.service_user_id,e.funder_id,e.rate_version_id,1,card.service_type,e.currency,
                e.service_date,e.service_date,'TEST-AUTH','Planned','NonChargeable','AwayFromZero','SYNTHETIC-TEST-POLICY','Uncapped',{3},'Approved',{4},now(),'{{}}'::jsonb
              from finance_billable_events e join finance_rate_versions rv on rv.id=e.rate_version_id join finance_rate_cards card on card.id=rv.rate_card_id where e.id={0};
            update finance_billable_events e set contract_version_id={5},timesheet_entry_id=t.id,source_revision=t.revision,
              created_by_user_id={3},approved_by_user_id={4},source_snapshot=finance_visit_source(v),billing_minutes=v."DurationMinutes",status='Approved'
              from "Visits" v join timesheet_entries t on t.visit_id=v."Id" and t.organization_id=v."OrganizationId" where e.id={0} and v."Id"=e.visit_id;
            """,billable,period,entry,submitter,reviewer,contract);
        var scope=await db.Database.SqlQueryRaw<ApprovedVatTestData.Scope>("select e.organization_id as \"Org\",e.branch_id as \"Branch\",c.service_type as \"Service\",coalesce(p.default_vat_rate,0) as \"Rate\" from finance_billable_events e join finance_contract_versions c on c.id=e.contract_version_id left join finance_invoice_profiles p on p.organization_id=e.organization_id and p.branch_id=e.branch_id where e.id={0}",billable).SingleAsync();
        await ApprovedVatTestData.Ensure(db,scope.Org,scope.Branch,scope.Service,scope.Rate);
    }
}
