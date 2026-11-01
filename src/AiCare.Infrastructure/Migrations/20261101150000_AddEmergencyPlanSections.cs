using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20261101150000_AddEmergencyPlanSections")]
public sealed class AddEmergencyPlanSections : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table care_plan_emergency_selections (
          id uuid primary key, care_plan_id uuid not null references "CarePlans"("Id"),
          organization_id uuid not null, branch_id uuid not null, selection_version integer not null check(selection_version>0),
          sections_json jsonb not null check(jsonb_typeof(sections_json)='array'),
          source_selection_id uuid null references care_plan_emergency_selections(id),
          reason text not null check(length(trim(reason)) between 1 and 2000),
          created_by uuid not null references "AppUsers"("Id"), created_at timestamptz not null default now(), request_snapshot jsonb not null,
          unique(care_plan_id,selection_version)
        );
        create table care_plan_handover_copies (
          id uuid primary key, care_plan_id uuid not null references "CarePlans"("Id"),
          selection_id uuid not null references care_plan_emergency_selections(id),
          organization_id uuid not null, branch_id uuid not null, plan_revision bigint not null,
          purpose text not null check(length(trim(purpose)) between 1 and 2000),
          html text not null, sha256 text not null, file_name text not null,
          created_by uuid not null references "AppUsers"("Id"), created_at timestamptz not null default now(), request_snapshot jsonb not null
        );
        create function guard_emergency_history() returns trigger language plpgsql as $$
        begin raise exception 'Emergency selection and handover history is append-only' using errcode='23514'; end $$;
        create trigger emergency_selection_immutable before update or delete on care_plan_emergency_selections for each row execute function guard_emergency_history();
        create trigger handover_copy_immutable before update or delete on care_plan_handover_copies for each row execute function guard_emergency_history();
        create function guard_emergency_selection() returns trigger language plpgsql as $$
        declare v care_plan_versions%rowtype; latest integer; prior care_plan_emergency_selections%rowtype;
        begin
          select * into v from care_plan_versions where care_plan_id=new.care_plan_id for update;
          if not found or v.status<>'Draft' or v.organization_id<>new.organization_id or v.branch_id<>new.branch_id then
            raise exception 'Emergency section changes require a matching draft' using errcode='23514'; end if;
          if exists(select 1 from jsonb_array_elements(new.sections_json) x where jsonb_typeof(x)<>'string'
            or x#>>'{}' not in ('PersonalCare','MedicationSupport','MobilityAndTransfers','Nutrition'))
            or jsonb_array_length(new.sections_json)<>(select count(distinct x) from jsonb_array_elements(new.sections_json) x) then
            raise exception 'Choose distinct supported care-plan sections' using errcode='23514'; end if;
          select coalesce(max(selection_version),0) into latest from care_plan_emergency_selections where care_plan_id=new.care_plan_id;
          if new.selection_version<>latest+1 then raise exception 'Selection changed' using errcode='23514'; end if;
          if new.source_selection_id is not null then
            select * into prior from care_plan_emergency_selections where id=new.source_selection_id;
            if not found or prior.care_plan_id is distinct from v.previous_care_plan_id or prior.organization_id<>new.organization_id
              or prior.branch_id<>new.branch_id or prior.sections_json<>new.sections_json
              or exists(select 1 from care_plan_emergency_selections where care_plan_id=prior.care_plan_id and selection_version>prior.selection_version) then
              raise exception 'Copy only the exact latest previous-plan selection' using errcode='23514'; end if;
          end if;
          return new;
        end $$;
        create trigger emergency_selection_valid before insert on care_plan_emergency_selections for each row execute function guard_emergency_selection();
        create function guard_handover_copy() returns trigger language plpgsql as $$
        declare v care_plan_versions%rowtype;
        begin
          select * into v from care_plan_versions where care_plan_id=new.care_plan_id for share;
          if not found or v.status<>'Active' or v.revision<>new.plan_revision or v.organization_id<>new.organization_id or v.branch_id<>new.branch_id
            or not exists(select 1 from care_plan_emergency_selections s where s.id=new.selection_id and s.care_plan_id=v.care_plan_id
              and s.organization_id=v.organization_id and s.branch_id=v.branch_id and jsonb_array_length(s.sections_json)>0
              and not exists(select 1 from care_plan_emergency_selections newer where newer.care_plan_id=s.care_plan_id and newer.selection_version>s.selection_version)) then
            raise exception 'Handover copy requires the exact active plan and designated sections' using errcode='23514'; end if;
          return new;
        end $$;
        create trigger handover_copy_valid before insert on care_plan_handover_copies for each row execute function guard_handover_copy();
        """);

    protected override void Down(MigrationBuilder m) => throw new NotSupportedException("Retain handover disclosure history; recover forwards.");
}
