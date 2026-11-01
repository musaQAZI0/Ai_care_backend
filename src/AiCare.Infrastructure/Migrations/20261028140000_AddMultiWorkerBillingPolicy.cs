using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261028140000_AddMultiWorkerBillingPolicy")]
public sealed class AddMultiWorkerBillingPolicy : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table finance_multi_worker_policies(
          id uuid primary key,contract_version_id uuid not null unique references finance_contract_versions(id),
          organization_id uuid not null,branch_id uuid not null,billing_unit text not null check(billing_unit='PerVisit'),
          reference text not null check(length(trim(reference))>0),created_by uuid not null,
          approved_by uuid null,approved_at timestamptz null,created_at timestamptz not null default now(),
          check(approved_by is null or (approved_by<>created_by and approved_at is not null)));
        create function govern_multi_worker_policy() returns trigger language plpgsql as $$begin
          if TG_OP='DELETE' then raise exception 'Multi-worker billing policy is immutable' using errcode='23514';end if;
          if TG_OP='UPDATE' and (old.approved_by is not null or (to_jsonb(new)-'approved_by'-'approved_at') is distinct from (to_jsonb(old)-'approved_by'-'approved_at')) then
            raise exception 'Create a new contract version for changed multi-worker policy' using errcode='23514';end if;
          perform 1 from finance_contract_versions where id=new.contract_version_id and organization_id=new.organization_id and branch_id=new.branch_id and status='Approved' for update;
          if not found then raise exception 'Approved scoped contract required' using errcode='23514';end if;
          return new;end $$;
        create trigger multi_worker_policy_guard before insert or update or delete on finance_multi_worker_policies for each row execute function govern_multi_worker_policy();
        alter function finance_visit_source("Visits") rename to finance_visit_source_without_crew;
        create function finance_visit_source(v "Visits") returns jsonb language sql stable as $$
          select finance_visit_source_without_crew(v) || case when exists(select 1 from visit_care_worker_assignments where visit_id=v."Id" and care_worker_id<>v."CareWorkerId") then
            jsonb_build_object('additionalWorkers',(select jsonb_agg(jsonb_build_object('worker',care_worker_id,'role',assignment_role,'organization',organization_id,'branch',branch_id) order by care_worker_id)
              from visit_care_worker_assignments where visit_id=v."Id" and care_worker_id<>v."CareWorkerId")) else '{}'::jsonb end;
        $$;
        alter function finance_source_current(uuid) rename to finance_source_current_without_crew;
        create function finance_source_current(event uuid) returns boolean language sql volatile as $$
          select finance_source_current_without_crew(event) and not exists(select 1 from finance_billable_events e join "Visits" v on v."Id"=e.visit_id
            where e.id=event and exists(select 1 from visit_care_worker_assignments where visit_id=e.visit_id and care_worker_id<>v."CareWorkerId")
              and (not exists(select 1 from finance_multi_worker_policies where contract_version_id=e.contract_version_id and approved_by is not null and billing_unit='PerVisit')
                or exists(select 1 from visit_care_worker_assignments a left join "CareWorkers" w on w."Id"=a.care_worker_id
                  where a.visit_id=e.visit_id and (a.organization_id<>e.organization_id or a.branch_id<>e.branch_id or w."OrganizationId" is distinct from e.organization_id or w."BranchId" is distinct from e.branch_id))));
        $$;
        create function protect_billed_crew() returns trigger language plpgsql as $$declare source uuid;begin
          source:=case when TG_OP='DELETE' then old.visit_id else new.visit_id end;
          perform 1 from "Visits" where "Id"=source for update;
          if exists(select 1 from finance_billable_events where visit_id=source and status in ('Approved','Invoiced')) or
            (TG_OP='UPDATE' and exists(select 1 from finance_billable_events where visit_id=old.visit_id and status in ('Approved','Invoiced'))) then
            raise exception 'Approved billing crew requires a linked source correction' using errcode='23514';end if;
          if TG_OP='DELETE' then return old;end if;return new;end $$;
        create trigger billed_crew_guard before insert or update or delete on visit_care_worker_assignments for each row execute function protect_billed_crew();
        create view finance_multi_worker_legacy_findings as select e.id,e.organization_id,e.branch_id,e.status,'CREW_SOURCE_REVIEW_REQUIRED'::text code
          from finance_billable_events e join "Visits" v on v."Id"=e.visit_id where e.status in ('Approved','Invoiced')
            and exists(select 1 from visit_care_worker_assignments where visit_id=e.visit_id and care_worker_id<>v."CareWorkerId")
            and e.source_snapshot is distinct from finance_visit_source(v);
        """);

    protected override void Down(MigrationBuilder m) => throw new NotSupportedException("Retain approved crew policy and source history; recover forwards.");
}
