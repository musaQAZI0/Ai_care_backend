using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20261101140000_AddEvidenceWithdrawals")]
public sealed class AddEvidenceWithdrawals : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table care_plan_evidence_withdrawals (
          id uuid primary key, evidence_id uuid not null unique references care_plan_evidence(id),
          care_plan_id uuid not null references "CarePlans"("Id"), organization_id uuid not null, branch_id uuid not null,
          reason text not null check(length(trim(reason)) between 1 and 2000),
          created_by uuid not null references "AppUsers"("Id"), created_by_name text not null,
          created_at timestamptz not null default now(), request_snapshot jsonb not null
        );
        create function guard_evidence_withdrawal() returns trigger language plpgsql as $$
        declare v care_plan_versions%rowtype;
        begin
          if TG_OP<>'INSERT' then raise exception 'Evidence withdrawals are append-only' using errcode='23514'; end if;
          select * into v from care_plan_versions where care_plan_id=new.care_plan_id for update;
          if not found or v.status<>'Draft' or v.organization_id<>new.organization_id or v.branch_id<>new.branch_id
            or not exists(select 1 from care_plan_evidence e where e.id=new.evidence_id and e.care_plan_id=v.care_plan_id
              and e.organization_id=v.organization_id and e.branch_id=v.branch_id and e.service_user_id=v.service_user_id) then
            raise exception 'Withdraw evidence only from its matching draft plan' using errcode='23514';
          end if;
          return new;
        end $$;
        create trigger evidence_withdrawal_guard before insert or update or delete on care_plan_evidence_withdrawals
          for each row execute function guard_evidence_withdrawal();
        do $$ declare constraint_name text; begin
          for constraint_name in select conname from pg_constraint where conrelid='care_plan_evidence'::regclass and contype='u'
            and pg_get_constraintdef(oid)='UNIQUE (care_plan_id, source_kind, source_id, section)'
          loop execute format('alter table care_plan_evidence drop constraint %I',constraint_name); end loop;
        end $$;
        create function guard_active_evidence_link() returns trigger language plpgsql as $$
        begin
          perform 1 from care_plan_versions where care_plan_id=new.care_plan_id for update;
          if exists(select 1 from care_plan_evidence e where e.care_plan_id=new.care_plan_id and e.source_kind=new.source_kind
            and e.source_id=new.source_id and e.section=new.section and not exists(select 1 from care_plan_evidence_withdrawals w where w.evidence_id=e.id)) then
            raise exception 'This source is already linked to this section' using errcode='23514';
          end if;
          if new.copied_from_id is not null and exists(select 1 from care_plan_evidence_withdrawals where evidence_id=new.copied_from_id) then
            raise exception 'Withdrawn evidence must not be copied into a revision' using errcode='23514'; end if;
          return new;
        end $$;
        create trigger active_evidence_link_guard before insert on care_plan_evidence for each row execute function guard_active_evidence_link();
        """);

    protected override void Down(MigrationBuilder m) => throw new NotSupportedException("Retain evidence correction history; recover forwards.");
}
