using AiCare.Application;
using AiCare.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Infrastructure;

public static class PublishedRotaAccess
{
    public static HashSet<Guid> AdditionalVisitIds(CareDbContext db, ITenantContext tenant, Guid workerId)
    {
        return db.Database.SqlQuery<Guid>($"""
            select a.visit_id as "Value" from visit_care_worker_assignments a
            where a.organization_id={tenant.OrganizationId} and a.care_worker_id={workerId}
            """).ToHashSet();
    }

    public static bool IsVisibleToWorker(CareDbContext db, ITenantContext tenant, Visit visit, Guid workerId)
    {
        if (visit.Status != VisitStatus.Scheduled || visit.StartsAt <= DateTimeOffset.UtcNow) return true;
        return CurrentVisitIds(db, tenant, workerId).Contains(visit.Id);
    }

    public static HashSet<Guid> CurrentVisitIds(CareDbContext db, ITenantContext tenant, Guid workerId)
    {
        return db.Database.SqlQuery<Guid>($"""
            select pv.visit_id as "Value"
            from rota_publications p
            join rota_publication_visits pv on pv.publication_id=p.id
            join "Visits" v on v."Id"=pv.visit_id
            where p.organization_id={tenant.OrganizationId}
              and (pv.care_worker_id={workerId} or exists(
                select 1 from rota_publication_additional_workers pa
                join visit_care_worker_assignments a on a.visit_id=pa.visit_id and a.care_worker_id=pa.care_worker_id
                where pa.publication_id=p.id and pa.visit_id=pv.visit_id and pa.care_worker_id={workerId}
                  and a.organization_id=p.organization_id))
              and not exists(select 1 from rota_publications newer where newer.organization_id=p.organization_id
                and newer.branch_id=p.branch_id and newer.week_start=p.week_start and newer.revision>p.revision)
              and (select count(*) from "Visits" current_visit where current_visit."OrganizationId"=p.organization_id
                and coalesce(current_visit."BranchId",{TenantDefaults.BranchId})=p.branch_id and current_visit."Status"<>'Cancelled'
                and (current_visit."StartsAt" at time zone 'Europe/London')::date>=p.week_start
                and (current_visit."StartsAt" at time zone 'Europe/London')::date<p.week_start+7)
                = (select count(*) from rota_publication_visits snapshot where snapshot.publication_id=p.id)
              and not exists(select 1 from unassigned_visits u where u.organization_id=p.organization_id and u.branch_id=p.branch_id
                and u.status='Open' and (u.starts_at at time zone 'Europe/London')::date>=p.week_start
                and (u.starts_at at time zone 'Europe/London')::date<p.week_start+7)
              and not exists(select 1 from rota_publication_visits snapshot join "Visits" current_visit on current_visit."Id"=snapshot.visit_id
                where snapshot.publication_id=p.id and (current_visit."CareWorkerId"<>snapshot.care_worker_id
                  or current_visit."StartsAt"<>snapshot.starts_at or current_visit."DurationMinutes"<>snapshot.duration_minutes
                  or current_visit."VisitType"<>snapshot.visit_type or current_visit."Status"='Cancelled'))
              and not exists(select 1 from visit_care_worker_assignments assignment join rota_publication_visits snapshot on snapshot.visit_id=assignment.visit_id
                where snapshot.publication_id=p.id and assignment.organization_id=p.organization_id
                  and not exists(select 1 from rota_publication_additional_workers pa where pa.publication_id=p.id
                    and pa.visit_id=assignment.visit_id and pa.care_worker_id=assignment.care_worker_id))
              and not exists(select 1 from rota_publication_additional_workers pa where pa.publication_id=p.id
                and not exists(select 1 from visit_care_worker_assignments assignment where assignment.visit_id=pa.visit_id
                  and assignment.care_worker_id=pa.care_worker_id and assignment.organization_id=p.organization_id))
              and v."CareWorkerId"=pv.care_worker_id and v."StartsAt"=pv.starts_at
              and v."DurationMinutes"=pv.duration_minutes and v."VisitType"=pv.visit_type
              and v."Status"<>'Cancelled'
            """).ToHashSet();
    }
}
