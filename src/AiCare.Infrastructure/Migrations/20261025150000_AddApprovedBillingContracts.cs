using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261025150000_AddApprovedBillingContracts")]
public sealed class AddApprovedBillingContracts : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table finance_contract_versions (
          id uuid primary key, organization_id uuid not null, branch_id uuid not null,
          funding_arrangement_id uuid not null references funding_arrangements(id),
          person_id uuid not null references "ServiceUsers"("Id"), funder_id uuid not null references finance_funders(id),
          rate_version_id uuid not null references finance_rate_versions(id),
          version integer not null check(version>0), service_type text not null, currency char(3) not null,
          authorised_from date not null, authorised_to date not null check(authorised_to>=authorised_from),
          authorisation_reference text not null check(length(trim(authorisation_reference))>0),
          billing_basis text not null check(billing_basis in ('Planned','Actual')),
          cancellation_policy text not null check(cancellation_policy in ('NonChargeable','ManualReview')),
          rounding_mode text not null check(rounding_mode in ('AwayFromZero','ToEven')),
          request_snapshot jsonb not null, approval_reason text not null default '',
          policy_reference text not null check(length(trim(policy_reference))>0),
          cap_mode text not null check(cap_mode in ('Uncapped','LifetimeAmount')),
          cap_amount numeric(12,2) null,
          status text not null default 'Draft' check(status in ('Draft','Approved')),
          created_by uuid not null, created_at timestamptz not null default now(), approved_by uuid null, approved_at timestamptz null,
          check((cap_mode='Uncapped' and cap_amount is null) or (cap_mode='LifetimeAmount' and cap_amount is not null and cap_amount>0)),
          check(status<>'Approved' or (approved_by is not null and approved_by<>created_by and approved_at is not null)),
          unique(funding_arrangement_id,version)
        );
        create index ix_contract_effective on finance_contract_versions(organization_id,person_id,authorised_from,authorised_to) where status='Approved';
        create function guard_finance_contract() returns trigger language plpgsql as $$
        declare arrangement funding_arrangements%rowtype;
        begin
          if TG_OP='DELETE' then raise exception 'Contract history cannot be deleted' using errcode='23514'; end if;
          if TG_OP='UPDATE' and (old.status='Approved' or
            (to_jsonb(new)-'status'-'approved_by'-'approved_at'-'approval_reason') is distinct from (to_jsonb(old)-'status'-'approved_by'-'approved_at'-'approval_reason')) then
            raise exception 'Create a new contract version; contract content is immutable' using errcode='23514';
          end if;
          select * into arrangement from funding_arrangements where id=new.funding_arrangement_id for update;
          if not found or arrangement.organization_id<>new.organization_id or arrangement.branch_id<>new.branch_id
            or arrangement.service_user_id<>new.person_id or arrangement.funder_id is distinct from new.funder_id
            or arrangement.rate_card_id is null or arrangement.status<>'Active'
            or not exists(select 1 from "ServiceUsers" s where s."Id"=new.person_id and s."OrganizationId"=new.organization_id and s."BranchId"=new.branch_id)
            or not exists(select 1 from finance_funders f where f.id=new.funder_id and f.organization_id=new.organization_id and (f.branch_id is null or f.branch_id=new.branch_id) and f.currency=new.currency and f.active)
            or not exists(select 1 from finance_rate_versions v join finance_rate_cards c on c.id=v.rate_card_id
              where v.id=new.rate_version_id and v.organization_id=new.organization_id and c.organization_id=new.organization_id
                and c.id=arrangement.rate_card_id and (c.branch_id is null or c.branch_id=new.branch_id) and (v.branch_id is null or v.branch_id=new.branch_id)
                and c.active and c.currency=new.currency and lower(c.service_type)=lower(new.service_type) and v.status='Active'
                and v.effective_from<=new.authorised_from and (v.effective_to is null or v.effective_to>=new.authorised_to))
            or (arrangement.valid_from at time zone 'Europe/London')::date>new.authorised_from
            or (arrangement.valid_to is not null and (arrangement.valid_to at time zone 'Europe/London')::date<new.authorised_to) then
            raise exception 'Contract references or effective coverage do not match' using errcode='23514';
          end if;
          if exists(select 1 from finance_contract_versions where funding_arrangement_id=new.funding_arrangement_id and currency<>new.currency) or exists(select 1 from finance_billable_events where funding_arrangement_id=new.funding_arrangement_id and currency<>new.currency) then raise exception 'A funding authorisation cannot mix currencies' using errcode='23514'; end if;
          if new.status='Approved' and new.cap_mode='LifetimeAmount' and new.cap_amount<(select coalesce(sum(final_amount),0) from finance_billable_events where funding_arrangement_id=new.funding_arrangement_id and status<>'Rejected') then raise exception 'Authorisation is below committed charges' using errcode='23514'; end if;
          if new.status='Approved' and exists(select 1 from finance_contract_versions v where v.id<>new.id
            and v.funding_arrangement_id=new.funding_arrangement_id and v.status='Approved'
            and daterange(v.authorised_from,v.authorised_to,'[]') && daterange(new.authorised_from,new.authorised_to,'[]')) then
            raise exception 'Approved contract dates overlap' using errcode='23P01';
          end if;
          return new;
        end $$;
        create trigger finance_contract_guard before insert or update or delete on finance_contract_versions for each row execute function guard_finance_contract();

        create function guard_finance_rate_version() returns trigger language plpgsql as $$
        begin
          if TG_OP<>'INSERT' and (exists(select 1 from finance_contract_versions where rate_version_id=old.id) or exists(select 1 from finance_billable_events where rate_version_id=old.id and status in ('Approved','Invoiced'))) then
            raise exception 'Referenced rate versions are immutable' using errcode='23514';
          end if;
          if TG_OP='DELETE' then return old; end if;
          perform 1 from finance_rate_cards c where c.id=new.rate_card_id and c.organization_id=new.organization_id and (new.branch_id is null or c.branch_id is null or c.branch_id=new.branch_id) for update;
          if not found then raise exception 'Rate version scope mismatch' using errcode='23514'; end if;
          if new.status='Active' and exists(select 1 from finance_rate_versions v where v.rate_card_id=new.rate_card_id and v.id<>new.id and v.status='Active' and daterange(v.effective_from,v.effective_to,'[]') && daterange(new.effective_from,new.effective_to,'[]')) then
            raise exception 'Active rate versions overlap' using errcode='23P01';
          end if; return new;
        end $$;
        create trigger finance_rate_version_guard before insert or update or delete on finance_rate_versions for each row execute function guard_finance_rate_version();
        create function guard_finance_rate_rule() returns trigger language plpgsql as $$
        declare target uuid;
        begin
          target:=case when TG_OP='DELETE' then old.rate_version_id else new.rate_version_id end;
          perform 1 from finance_rate_versions where id=target for update;
          if TG_OP<>'DELETE' and not exists(select 1 from finance_rate_versions where id=target and organization_id=new.organization_id) then raise exception 'Rate rule scope mismatch' using errcode='23514'; end if;
          if exists(select 1 from finance_contract_versions where rate_version_id=target) or exists(select 1 from finance_billable_events where rate_version_id=target and status in ('Approved','Invoiced')) then
            raise exception 'Referenced rate rules are immutable' using errcode='23514';
          end if;
          if TG_OP='UPDATE' and old.rate_version_id<>new.rate_version_id then raise exception 'Rate rule parent is immutable' using errcode='23514'; end if;
          if TG_OP='DELETE' then return old; end if; return new;
        end $$;
        create trigger finance_rate_rule_guard before insert or update or delete on finance_rate_rules for each row execute function guard_finance_rate_rule();

        alter table finance_billable_events alter column quantity type numeric(16,8);
        alter table finance_invoice_lines alter column quantity type numeric(16,8), alter column unit_rate type numeric(16,4);
        alter table finance_billable_events add column contract_version_id uuid null references finance_contract_versions(id),
          add column timesheet_entry_id uuid null references timesheet_entries(id), add column source_revision integer null,
          add column source_snapshot jsonb null, add column billing_minutes numeric null, add column created_by_user_id uuid null, add column approved_by_user_id uuid null;
        alter table finance_billable_events drop constraint finance_billable_events_organization_id_visit_id_key;
        create unique index ux_active_billable_source on finance_billable_events(organization_id,visit_id) where status<>'Rejected';
        create table finance_billable_event_history(id uuid primary key default gen_random_uuid(),event_id uuid not null references finance_billable_events(id),snapshot jsonb not null,recorded_at timestamptz not null default now());
        create function finance_visit_source(v "Visits") returns jsonb language sql immutable as $$
          select jsonb_build_object('person',v."ServiceUserId",'worker',v."CareWorkerId",'branch',v."BranchId",'organization',v."OrganizationId",'startsAt',extract(epoch from v."StartsAt"),'minutes',v."DurationMinutes",'type',v."VisitType",'status',v."Status",'checkIn',extract(epoch from v."CheckedInAt"),'checkOut',extract(epoch from v."CheckedOutAt"))
        $$;
        create function finance_policy_round(value numeric,places integer,mode text) returns numeric language sql immutable as $$
          select case when mode='ToEven' and abs(value*power(10::numeric,places))-floor(abs(value*power(10::numeric,places)))=0.5
            and mod(floor(abs(value*power(10::numeric,places))),2)=0
            then sign(value)*floor(abs(value*power(10::numeric,places)))/power(10::numeric,places)
            else round(value,places) end
        $$;
        create function finance_source_current(event uuid) returns boolean language sql volatile as $$
          select exists(select 1 from finance_billable_events e join "Visits" v on v."Id"=e.visit_id
            join timesheet_entries t on t.id=e.timesheet_entry_id join finance_contract_versions c on c.id=e.contract_version_id
            where e.id=event and v."OrganizationId"=e.organization_id and v."BranchId"=e.branch_id and v."ServiceUserId"=e.service_user_id and v."Status"='Completed' and t.status='Approved' and t.approved_by is not null
              and t.revision=e.source_revision and e.source_snapshot=finance_visit_source(v)
              and t.submitted_by is not null and t.approved_by<>t.submitted_by and t.planned_minutes=v."DurationMinutes"
              and v."CheckedOutAt">v."CheckedInAt" and t.actual_minutes>0
              and abs(t.actual_minutes-extract(epoch from(v."CheckedOutAt"-v."CheckedInAt"))/60)<0.000001
              and t.organization_id=e.organization_id and t.branch_id=e.branch_id and t.visit_id=e.visit_id and t.care_worker_id=v."CareWorkerId"
              and c.organization_id=e.organization_id and c.branch_id=e.branch_id and c.person_id=e.service_user_id
              and c.funder_id=e.funder_id and c.funding_arrangement_id=e.funding_arrangement_id and c.currency=e.currency
              and e.service_date=(v."StartsAt" at time zone 'Europe/London')::date
              and e.billing_minutes=case c.billing_basis when 'Actual' then t.actual_minutes else t.planned_minutes end
              and e.final_amount=e.gross_amount+e.adjustment_amount
              and exists(select 1 from finance_rate_rules r where r.id=e.rate_rule_id and r.rate_version_id=e.rate_version_id and r.organization_id=e.organization_id
                and r.unit=e.unit and r.unit_rate=e.unit_rate and r.unit in ('Hour','Visit')
                and e.quantity=finance_policy_round(greatest(case r.unit when 'Visit' then 1 else e.billing_minutes/60 end,r.minimum_quantity),8,c.rounding_mode)
                and e.gross_amount=finance_policy_round(greatest(case r.unit when 'Visit' then 1 else e.billing_minutes/60 end,r.minimum_quantity)*r.unit_rate,2,c.rounding_mode))
              and lower(c.service_type)=lower(v."VisitType") and c.rate_version_id=e.rate_version_id and c.status='Approved' and e.service_date between c.authorised_from and c.authorised_to)
        $$;
        create function guard_billable_lineage() returns trigger language plpgsql as $$
        declare contract finance_contract_versions%rowtype; consumed numeric;
        begin
          if TG_OP='DELETE' then raise exception 'Billable history cannot be deleted' using errcode='23514'; end if;
          if TG_OP='UPDATE' and old.status='Rejected' then raise exception 'Rejected event history cannot be changed; create a replacement' using errcode='23514'; end if;
          if TG_OP='UPDATE' and old.status in ('Approved','Invoiced') and
            ((old.status='Invoiced' and new.status<>'Invoiced') or new.status not in ('Approved','Invoiced') or (to_jsonb(new)-'status'-'revision') is distinct from (to_jsonb(old)-'status'-'revision')) then
            raise exception 'Approved billable history is immutable' using errcode='23514';
          end if;
          if new.contract_version_id is not null then
            -- Lock the funding parent to serialize all versions sharing a lifetime authorisation.
            perform 1 from funding_arrangements where id=new.funding_arrangement_id for update;
            select * into contract from finance_contract_versions where id=new.contract_version_id;
            if contract.cap_mode='LifetimeAmount' and new.status<>'Rejected' and (TG_OP='INSERT' or old.final_amount is distinct from new.final_amount or old.funding_arrangement_id is distinct from new.funding_arrangement_id) then
              select coalesce(sum(final_amount),0) into consumed from finance_billable_events where funding_arrangement_id=new.funding_arrangement_id and id<>new.id and status<>'Rejected';
              if consumed+new.final_amount>contract.cap_amount then raise exception 'Contract authorisation cap exceeded' using errcode='23514'; end if;
            end if;
          end if;
          if new.status in ('Approved','Invoiced') then
            if new.created_by_user_id is null or new.approved_by_user_id is null or new.created_by_user_id=new.approved_by_user_id then raise exception 'Independent billable event approval is required' using errcode='23514'; end if;
            if new.contract_version_id is null or new.timesheet_entry_id is null then raise exception 'Approved billing requires contract and attendance lineage' using errcode='23514'; end if;
            perform 1 from "Visits" where "Id"=new.visit_id for update;
            perform 1 from timesheet_entries where id=new.timesheet_entry_id for update;
          end if;
          return new;
        end $$;
        create trigger finance_billable_lineage before insert or update or delete on finance_billable_events for each row execute function guard_billable_lineage();
        create function record_billable_lineage() returns trigger language plpgsql as $$ begin
          if new.status in ('Approved','Invoiced') and not finance_source_current(new.id) then raise exception 'Billing source approval is stale or mismatched' using errcode='23514'; end if;
          insert into finance_billable_event_history(event_id,snapshot) values(new.id,to_jsonb(new)); return new;
        end $$;
        create trigger finance_billable_history after insert or update on finance_billable_events for each row execute function record_billable_lineage();
        create trigger finance_billable_history_immutable before update or delete on finance_billable_event_history for each row execute function protect_timesheet_history();
        create function protect_billed_attendance() returns trigger language plpgsql as $$ begin
          if TG_TABLE_NAME='Visits' then
            if finance_visit_source(new) is distinct from finance_visit_source(old) and exists(select 1 from finance_billable_events where visit_id=old."Id" and status in ('Approved','Invoiced')) then
              raise exception 'Approved billing source requires a controlled financial correction' using errcode='23514';
            end if;
          elsif exists(select 1 from finance_billable_events where timesheet_entry_id=old.id and status in ('Approved','Invoiced')) and new is distinct from old then
            raise exception 'Approved billing attendance requires a controlled financial correction' using errcode='23514';
          end if; return new;
        end $$;
        create trigger finance_visit_billing_guard before update on "Visits" for each row execute function protect_billed_attendance();
        create trigger finance_timesheet_billing_guard before update on timesheet_entries for each row execute function protect_billed_attendance();
        create function guard_invoice_billing_line() returns trigger language plpgsql as $$ begin
          if new.billable_event_id is not null and not exists(select 1 from finance_billable_events e where e.id=new.billable_event_id
            and e.organization_id=new.organization_id and e.branch_id=new.branch_id and e.service_user_id=new.service_user_id and e.visit_id=new.visit_id
            and e.status in ('Approved','Invoiced') and finance_source_current(e.id)
            and e.final_amount=new.amount and e.quantity=new.quantity and e.unit_rate=new.unit_rate) then
            raise exception 'Invoice line does not match an approved current billing source' using errcode='23514';
          end if; return new;
        end $$;
        create trigger finance_invoice_lineage before insert or update on finance_invoice_lines for each row execute function guard_invoice_billing_line();

        """);

    protected override void Down(MigrationBuilder m) => throw new NotSupportedException("Forward-only financial history migration; restore using the approved database recovery process.");
}
