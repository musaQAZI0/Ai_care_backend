using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261101110000_AddCarePlanEvidence")]
public sealed class AddCarePlanEvidence : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table care_plan_evidence (
          id uuid primary key, care_plan_id uuid not null references "CarePlans"("Id"),
          organization_id uuid not null, branch_id uuid not null, service_user_id uuid not null,
          source_kind text not null check(source_kind in ('Assessment','Risk')), source_id uuid not null,
          source_snapshot jsonb not null, section text not null check(length(trim(section)) between 1 and 200),
          rationale text not null check(length(trim(rationale)) between 1 and 2000),
          copied_from_id uuid null references care_plan_evidence(id),
          created_by uuid not null references "AppUsers"("Id"), created_at timestamptz not null default now(),
          request_snapshot jsonb not null,
          unique(care_plan_id,source_kind,source_id,section)
        );
        create function guard_care_plan_evidence() returns trigger language plpgsql as $$
        declare p care_plan_versions%rowtype; s jsonb;
        begin
          if TG_OP<>'INSERT' then raise exception 'Plan evidence is append-only' using errcode='23514'; end if;
          select * into p from care_plan_versions where care_plan_id=new.care_plan_id for update;
          if not found or p.status<>'Draft' or p.organization_id<>new.organization_id
            or p.branch_id<>new.branch_id or p.service_user_id<>new.service_user_id then
            raise exception 'Evidence requires a matching draft plan' using errcode='23514';
          end if;
          if new.copied_from_id is not null then
            if not exists(select 1 from care_plan_evidence e where e.id=new.copied_from_id
              and e.care_plan_id=p.previous_care_plan_id and e.organization_id=new.organization_id
              and e.branch_id=new.branch_id and e.service_user_id=new.service_user_id
              and e.source_kind=new.source_kind and e.source_id=new.source_id
              and e.source_snapshot=new.source_snapshot and e.section=new.section and e.rationale=new.rationale) then
              raise exception 'Invalid evidence lineage' using errcode='23514';
            end if;
          else
            if new.source_kind='Assessment' then
              select to_jsonb(a) into s from governed_assessments a where a.id=new.source_id
                and a.organization_id=new.organization_id and a.branch_id=new.branch_id
                and a.service_user_id=new.service_user_id and a.status='Current' and a.signed_at is not null for share;
            else
              select to_jsonb(a) into s from governed_risk_assessments a where a.id=new.source_id
                and a.organization_id=new.organization_id and a.branch_id=new.branch_id
                and a.service_user_id=new.service_user_id and a.status='Current' and a.signed_at is not null for share;
            end if;
            if s is null then raise exception 'Evidence requires a current signed source in the same scope' using errcode='23514'; end if;
            new.source_snapshot=s;
          end if;
          return new;
        end $$;
        create trigger care_plan_evidence_guard before insert or update or delete on care_plan_evidence
          for each row execute function guard_care_plan_evidence();
        """);

    protected override void Down(MigrationBuilder m) => m.Sql("drop table care_plan_evidence; drop function guard_care_plan_evidence();");
}
