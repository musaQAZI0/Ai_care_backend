using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20261101120000_AddCarePlanReviewTriggers")]
public sealed class AddCarePlanReviewTriggers : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table care_plan_review_rules (
          id uuid primary key, service_user_id uuid not null references "ServiceUsers"("Id"),
          organization_id uuid not null, branch_id uuid not null,
          event_kind text not null check(event_kind in ('AssessmentApproved','RiskApproved','IncidentReported')),
          version integer not null check(version>0), enabled boolean not null,
          reviewer_id uuid not null references "AppUsers"("Id"), reviewer_name text not null,
          due_hours integer not null check(due_hours between 1 and 2160),
          reason text not null check(length(trim(reason)) between 1 and 2000),
          created_by uuid not null references "AppUsers"("Id"), created_at timestamptz not null default now(), request_snapshot jsonb not null,
          unique(organization_id,branch_id,service_user_id,event_kind,version)
        );
        create table care_plan_review_items (
          id uuid primary key default gen_random_uuid(), rule_id uuid not null references care_plan_review_rules(id),
          service_user_id uuid not null references "ServiceUsers"("Id"), organization_id uuid not null, branch_id uuid not null,
          event_kind text not null, source_id uuid not null, source_version integer null,
          care_plan_id uuid null references "CarePlans"("Id"),
          reviewer_id uuid not null references "AppUsers"("Id"), reviewer_name text not null,
          created_at timestamptz not null default now(), due_at timestamptz not null,
          unique(event_kind,source_id)
        );
        create index care_plan_review_items_person on care_plan_review_items(organization_id,branch_id,service_user_id,due_at);
        create table care_plan_review_events (
          id uuid primary key, item_id uuid not null references care_plan_review_items(id), revision integer not null check(revision>0),
          action text not null check(action in ('Reassigned','NoChangeRequired','RevisedPlanActivated')),
          reviewer_id uuid not null references "AppUsers"("Id"), reviewer_name text not null,
          reason text not null check(length(trim(reason)) between 1 and 2000),
          resolved_plan_id uuid null references "CarePlans"("Id"),
          created_by uuid not null references "AppUsers"("Id"), created_at timestamptz not null default now(), request_snapshot jsonb not null,
          unique(item_id,revision), check((action='RevisedPlanActivated')=(resolved_plan_id is not null))
        );
        create function guard_review_history() returns trigger language plpgsql as $$
        begin raise exception 'Review history is append-only' using errcode='23514'; end $$;
        create trigger review_rule_immutable before update or delete on care_plan_review_rules for each row execute function guard_review_history();
        create trigger review_item_immutable before update or delete on care_plan_review_items for each row execute function guard_review_history();
        create trigger review_event_immutable before update or delete on care_plan_review_events for each row execute function guard_review_history();

        create function guard_review_rule() returns trigger language plpgsql as $$
        declare latest integer;
        begin
          perform pg_advisory_xact_lock(hashtextextended(new.service_user_id::text,27));
          if not exists(select 1 from "ServiceUsers" p where p."Id"=new.service_user_id and p."OrganizationId"=new.organization_id and p."BranchId"=new.branch_id)
            or not exists(select 1 from "AppUsers" u where u."Id"=new.reviewer_id and u."OrganizationId"=new.organization_id and u."IsActive"
              and u."Role" in ('CareManager','Administrator') and (u."BranchId"=new.branch_id or (u."BranchId" is null and u."Role"='Administrator'))) then
            raise exception 'Select a current reviewer in the person branch' using errcode='23514';
          end if;
          select coalesce(max(version),0) into latest from care_plan_review_rules where service_user_id=new.service_user_id
            and organization_id=new.organization_id and branch_id=new.branch_id and event_kind=new.event_kind;
          if new.version<>latest+1 then raise exception 'Stale rule version' using errcode='23514'; end if;
          return new;
        end $$;
        create trigger review_rule_valid before insert on care_plan_review_rules for each row execute function guard_review_rule();

        create function generate_care_plan_review() returns trigger language plpgsql as $$
        declare payload jsonb; person uuid; org uuid; branch uuid; source uuid; sourceversion integer; kind text; r care_plan_review_rules%rowtype;
        begin
          payload=to_jsonb(new);
          if TG_TABLE_NAME='Incidents' then
            person=(payload->>'ServiceUserId')::uuid; org=(payload->>'OrganizationId')::uuid; branch=(payload->>'BranchId')::uuid;
            source=(payload->>'Id')::uuid; kind='IncidentReported';
          else
            if new.status<>'Current' or new.signed_at is null then return new; end if;
            if TG_OP='UPDATE' then if old.status='Current' then return new; end if; end if;
            person=new.service_user_id; org=new.organization_id; branch=new.branch_id; source=new.id; sourceversion=new.version;
            kind=case when TG_TABLE_NAME='governed_assessments' then 'AssessmentApproved' else 'RiskApproved' end;
          end if;
          perform pg_advisory_xact_lock(hashtextextended(person::text,27));
          select * into r from care_plan_review_rules where service_user_id=person and organization_id=org and branch_id=branch
            and event_kind=kind order by version desc limit 1;
          if found and r.enabled then
            insert into care_plan_review_items(rule_id,service_user_id,organization_id,branch_id,event_kind,source_id,source_version,
              care_plan_id,reviewer_id,reviewer_name,due_at)
            values(r.id,person,org,branch,kind,source,sourceversion,
              (select care_plan_id from care_plan_versions where service_user_id=person and organization_id=org and branch_id=branch and status='Active' limit 1),
              r.reviewer_id,r.reviewer_name,now()+make_interval(hours=>r.due_hours)) on conflict(event_kind,source_id) do nothing;
          end if;
          return new;
        end $$;
        create trigger assessment_plan_review after insert or update on governed_assessments for each row execute function generate_care_plan_review();
        create trigger risk_plan_review after insert or update on governed_risk_assessments for each row execute function generate_care_plan_review();
        create trigger incident_plan_review after insert on "Incidents" for each row execute function generate_care_plan_review();

        create function guard_review_event() returns trigger language plpgsql as $$
        declare i care_plan_review_items%rowtype; latest integer;
        begin
          select * into i from care_plan_review_items where id=new.item_id for update;
          if not found then raise exception 'Unknown review' using errcode='23514'; end if;
          select coalesce(max(revision),0) into latest from care_plan_review_events where item_id=i.id;
          if new.revision<>latest+1 or exists(select 1 from care_plan_review_events where item_id=i.id and action<>'Reassigned') then
            raise exception 'Review changed or already resolved' using errcode='23514';
          end if;
          if not exists(select 1 from "AppUsers" u where u."Id"=new.reviewer_id and u."OrganizationId"=i.organization_id and u."IsActive"
            and u."Role" in ('CareManager','Administrator') and (u."BranchId"=i.branch_id or (u."BranchId" is null and u."Role"='Administrator'))) then
            raise exception 'Select a current branch reviewer' using errcode='23514';
          end if;
          if new.action='RevisedPlanActivated' and not exists(select 1 from care_plan_versions p
            join care_plan_lifecycle_events e on e.care_plan_id=p.care_plan_id and e.to_status='Active'
            where p.care_plan_id=new.resolved_plan_id and p.service_user_id=i.service_user_id and p.organization_id=i.organization_id
              and p.branch_id=i.branch_id and p.status in ('Active','Superseded') and e.performed_at>=i.created_at
              and (i.care_plan_id is null or p.version_number>(select version_number from care_plan_versions where care_plan_id=i.care_plan_id))) then
            raise exception 'Select a subsequent plan activated after this review arose' using errcode='23514';
          end if;
          return new;
        end $$;
        create trigger review_event_valid before insert on care_plan_review_events for each row execute function guard_review_event();
        """);

    protected override void Down(MigrationBuilder m) => m.Sql("""
        drop trigger assessment_plan_review on governed_assessments;
        drop trigger risk_plan_review on governed_risk_assessments;
        drop trigger incident_plan_review on "Incidents";
        drop function generate_care_plan_review();
        drop table care_plan_review_events; drop function guard_review_event();
        drop table care_plan_review_items;
        drop table care_plan_review_rules; drop function guard_review_rule(); drop function guard_review_history();
        """);
}
