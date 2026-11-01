using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261101100000_AddMeasurableCareGoals")]
public sealed class AddMeasurableCareGoals : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table care_plan_measurable_goals (
          id uuid primary key, root_goal_id uuid not null,
          care_plan_id uuid not null references "CarePlans"("Id"), service_user_id uuid not null references "ServiceUsers"("Id"),
          organization_id uuid not null, branch_id uuid not null,
          supersedes_goal_id uuid null references care_plan_measurable_goals(id),
          source_goal_id uuid null references care_plan_measurable_goals(id),
          title text not null check(length(trim(title))>0), baseline_value numeric(16,4) not null, target_value numeric(16,4) not null,
          unit text not null check(length(trim(unit))>0), baseline_date date not null, review_due_date date not null, target_date date not null,
          owner_user_id uuid not null references "AppUsers"("Id"), owner_name text not null,
          planned_actions text not null check(length(trim(planned_actions))>0), change_reason text not null check(length(trim(change_reason))>0),
          created_by uuid not null references "AppUsers"("Id"), created_at timestamptz not null default now(),
          request_snapshot jsonb not null, check(review_due_date>=baseline_date and target_date>=review_due_date),
          check(supersedes_goal_id is null or source_goal_id is null)
        );
        create unique index ux_goal_replacement on care_plan_measurable_goals(supersedes_goal_id) where supersedes_goal_id is not null;
        create unique index ux_goal_plan_root on care_plan_measurable_goals(care_plan_id,root_goal_id) where supersedes_goal_id is null;
        create index ix_plan_measurable_goals on care_plan_measurable_goals(organization_id,branch_id,care_plan_id,root_goal_id);
        create table care_goal_progress (
          id uuid primary key, goal_id uuid not null references care_plan_measurable_goals(id),
          care_plan_id uuid not null references "CarePlans"("Id"), organization_id uuid not null, branch_id uuid not null,
          corrects_event_id uuid null references care_goal_progress(id), correction_reason text not null default '',
          measured_value numeric(16,4) null, status text not null check(status in ('InProgress','Achieved','NeedsReview','OnHold')),
          note text not null check(length(trim(note))>0), observed_at timestamptz not null,
          recorded_by uuid not null references "AppUsers"("Id"), recorded_by_name text not null,
          recorded_at timestamptz not null default now(), request_snapshot jsonb not null,
          check(status not in ('InProgress','Achieved') or measured_value is not null),
          check(corrects_event_id is null or length(trim(correction_reason))>0)
        );
        create unique index ux_goal_progress_correction on care_goal_progress(corrects_event_id) where corrects_event_id is not null;
        create index ix_goal_progress_history on care_goal_progress(organization_id,branch_id,goal_id,observed_at);

        create function guard_measurable_care_goal() returns trigger language plpgsql as $$
        declare p "CarePlans"%rowtype; v care_plan_versions%rowtype; old_goal care_plan_measurable_goals%rowtype;
        begin
          if TG_OP<>'INSERT' then raise exception 'Goal definitions are immutable; append a replacement in a draft plan' using errcode='23514'; end if;
          select * into v from care_plan_versions where care_plan_id=new.care_plan_id for update;
          if not found or v.status<>'Draft' then raise exception 'Goal changes require a draft plan' using errcode='23514'; end if;
          select * into p from "CarePlans" where "Id"=new.care_plan_id;
          if p."OrganizationId" is distinct from new.organization_id or p."BranchId" is distinct from new.branch_id or p."ServiceUserId"<>new.service_user_id
            then raise exception 'Goal plan/person scope mismatch' using errcode='23514'; end if;
          if new.supersedes_goal_id is not null or new.source_goal_id is not null then
            select * into old_goal from care_plan_measurable_goals where id=coalesce(new.supersedes_goal_id,new.source_goal_id);
            if not found or old_goal.organization_id<>new.organization_id or old_goal.branch_id<>new.branch_id or old_goal.service_user_id<>new.service_user_id or old_goal.root_goal_id<>new.root_goal_id
              then raise exception 'Goal lineage scope mismatch' using errcode='23514'; end if;
            if new.supersedes_goal_id is not null and old_goal.care_plan_id<>new.care_plan_id
              then raise exception 'Replace goals only in their own draft' using errcode='23514'; end if;
            if new.source_goal_id is not null and v.previous_care_plan_id is distinct from old_goal.care_plan_id
              then raise exception 'Copy goals only from the previous plan version' using errcode='23514'; end if;
          elsif new.root_goal_id<>new.id then raise exception 'A new goal starts its own lineage' using errcode='23514'; end if;
          if new.source_goal_id is null and not exists(select 1 from "AppUsers" where "Id"=new.owner_user_id and "OrganizationId"=new.organization_id and
             ("BranchId"=new.branch_id or ("BranchId" is null and "Role"='Administrator')) and
             "IsActive" and "Role" in ('Administrator','CareManager','CareCoordinator','CareWorker'))
            then raise exception 'Select an eligible goal owner in this branch' using errcode='23514'; end if;
          return new;
        end $$;
        create trigger measurable_goal_guard before insert or update or delete on care_plan_measurable_goals for each row execute function guard_measurable_care_goal();

        create function guard_care_goal_progress() returns trigger language plpgsql as $$
        declare g care_plan_measurable_goals%rowtype; current_status text;
        begin
          if TG_OP<>'INSERT' then raise exception 'Progress history is append-only; add a correction' using errcode='23514'; end if;
          select status into current_status from care_plan_versions where care_plan_id=new.care_plan_id for update;
          if current_status is null or (current_status<>'Active' and not(current_status='Superseded' and new.corrects_event_id is not null and
             exists(select 1 from "AppUsers" where "Id"=new.recorded_by and "OrganizationId"=new.organization_id and "IsActive" and "Role" in ('Administrator','CareManager','CareCoordinator'))))
            then raise exception 'New progress requires an active plan; managers may correct superseded history' using errcode='23514'; end if;
          select * into g from care_plan_measurable_goals where id=new.goal_id;
          if not found or g.care_plan_id<>new.care_plan_id or g.organization_id<>new.organization_id or g.branch_id<>new.branch_id or
             exists(select 1 from care_plan_measurable_goals where supersedes_goal_id=g.id)
            then raise exception 'Progress must reference a current goal definition in its own plan' using errcode='23514'; end if;
          if new.observed_at>now()+interval '5 minutes' or (new.observed_at at time zone 'Europe/London')::date<g.baseline_date
            then raise exception 'Progress observed time is outside the allowed period' using errcode='23514'; end if;
          if new.corrects_event_id is not null and not exists(select 1 from care_goal_progress where id=new.corrects_event_id and goal_id=g.id and organization_id=g.organization_id and branch_id=g.branch_id)
            then raise exception 'Corrections must retain their original goal and scope' using errcode='23514'; end if;
          return new;
        end $$;
        create trigger care_goal_progress_guard before insert or update or delete on care_goal_progress for each row execute function guard_care_goal_progress();
        """);

    protected override void Down(MigrationBuilder m) => throw new NotSupportedException("Retain goal/progress history; recover forwards.");
}
