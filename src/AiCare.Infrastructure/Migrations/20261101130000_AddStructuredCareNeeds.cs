using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20261101130000_AddStructuredCareNeeds")]
public sealed class AddStructuredCareNeeds : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table care_plan_needs (
          id uuid primary key, root_need_id uuid not null,
          care_plan_id uuid not null references "CarePlans"("Id"), service_user_id uuid not null references "ServiceUsers"("Id"),
          organization_id uuid not null, branch_id uuid not null,
          supersedes_need_id uuid null references care_plan_needs(id), source_need_id uuid null references care_plan_needs(id),
          section text not null check(length(trim(section)) between 1 and 200),
          identified_need text not null check(length(trim(identified_need)) between 1 and 4000),
          level_of_need text not null check(length(trim(level_of_need)) between 1 and 200),
          planned_outcomes text not null check(length(trim(planned_outcomes)) between 1 and 4000),
          actions text not null check(length(trim(actions)) between 1 and 4000),
          owner_user_id uuid not null references "AppUsers"("Id"), owner_name text not null,
          status text not null check(status in ('Active','NoLongerRequired')),
          change_reason text not null check(length(trim(change_reason)) between 1 and 2000),
          created_by uuid not null references "AppUsers"("Id"), created_at timestamptz not null default now(), request_snapshot jsonb not null,
          check(supersedes_need_id is null or source_need_id is null)
        );
        create unique index care_need_one_replacement on care_plan_needs(supersedes_need_id) where supersedes_need_id is not null;
        create unique index care_need_one_root_per_plan on care_plan_needs(care_plan_id,root_need_id) where supersedes_need_id is null;
        create index care_need_plan_scope on care_plan_needs(organization_id,branch_id,care_plan_id);
        create function guard_structured_care_need() returns trigger language plpgsql as $$
        declare v care_plan_versions%rowtype; prior care_plan_needs%rowtype;
        begin
          if TG_OP<>'INSERT' then raise exception 'Care needs are immutable; append a draft correction' using errcode='23514'; end if;
          select * into v from care_plan_versions where care_plan_id=new.care_plan_id for update;
          if not found or v.status<>'Draft' or v.organization_id<>new.organization_id or v.branch_id<>new.branch_id or v.service_user_id<>new.service_user_id then
            raise exception 'Care need requires its own scoped draft plan' using errcode='23514';
          end if;
          if new.supersedes_need_id is not null or new.source_need_id is not null then
            select * into prior from care_plan_needs where id=coalesce(new.supersedes_need_id,new.source_need_id);
            if not found or prior.organization_id<>new.organization_id or prior.branch_id<>new.branch_id
              or prior.service_user_id<>new.service_user_id or prior.root_need_id<>new.root_need_id
              or exists(select 1 from care_plan_needs where supersedes_need_id=prior.id) then
              raise exception 'Care need lineage must retain the latest definition and scope' using errcode='23514';
            end if;
            if new.supersedes_need_id is not null and prior.care_plan_id<>new.care_plan_id then
              raise exception 'Correct a need only within its own draft' using errcode='23514'; end if;
            if new.source_need_id is not null and (v.previous_care_plan_id is distinct from prior.care_plan_id or
              row(new.section,new.identified_need,new.level_of_need,new.planned_outcomes,new.actions,new.owner_user_id,new.owner_name,new.status)
              is distinct from row(prior.section,prior.identified_need,prior.level_of_need,prior.planned_outcomes,prior.actions,prior.owner_user_id,prior.owner_name,prior.status)) then
              raise exception 'A copied need must retain its exact previous plan definition' using errcode='23514'; end if;
          elsif new.root_need_id<>new.id then raise exception 'New need must begin its own lineage' using errcode='23514'; end if;
          if new.source_need_id is null and not exists(select 1 from "AppUsers" u where u."Id"=new.owner_user_id and u."OrganizationId"=new.organization_id
            and u."IsActive" and u."Role" in ('Administrator','CareManager','CareCoordinator','CareWorker')
            and (u."BranchId"=new.branch_id or (u."BranchId" is null and u."Role"='Administrator'))) then
            raise exception 'Select an active care-team owner in this branch' using errcode='23514'; end if;
          return new;
        end $$;
        create trigger structured_care_need_guard before insert or update or delete on care_plan_needs
          for each row execute function guard_structured_care_need();
        """);

    protected override void Down(MigrationBuilder m) => throw new NotSupportedException("Retain structured care need history; recover forwards.");
}
