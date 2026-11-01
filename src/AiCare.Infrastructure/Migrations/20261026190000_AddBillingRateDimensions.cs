using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20261026190000_AddBillingRateDimensions")]
public sealed class AddBillingRateDimensions:Migration
{
 protected override void Up(MigrationBuilder m)=>m.Sql("""
 alter table finance_rate_rules add column staff_level text null check(staff_level is null or length(trim(staff_level))>0),add column location_code text null check(location_code is null or length(trim(location_code))>0),
  add column minimum_minutes numeric null check(minimum_minutes>=0),add column maximum_minutes numeric null check(maximum_minutes>0),
  add constraint ck_billing_duration_band check(maximum_minutes is null or minimum_minutes is null or maximum_minutes>minimum_minutes);
 create table finance_visit_billing_context(
  visit_id uuid primary key references "Visits"("Id"),organization_id uuid not null,branch_id uuid not null,
  staff_level text not null check(length(trim(staff_level))>0),location_code text not null check(length(trim(location_code))>0),
  reference text not null check(length(trim(reference))>0),created_by uuid not null,created_at timestamptz not null default now());
 create function govern_visit_billing_context() returns trigger language plpgsql as $$begin
  if TG_OP='DELETE' then raise exception 'Billing context cannot be deleted' using errcode='23514';end if;
  perform 1 from "Visits" where "Id"=new.visit_id and "OrganizationId"=new.organization_id and "BranchId"=new.branch_id for update;
  if not found then raise exception 'Billing context visit scope mismatch' using errcode='23514';end if;
  if TG_OP='UPDATE' and (new.visit_id<>old.visit_id or new.organization_id<>old.organization_id or new.branch_id<>old.branch_id) then raise exception 'Billing context source is immutable' using errcode='23514';end if;
  if exists(select 1 from finance_billable_events where visit_id=new.visit_id and status<>'Rejected') then raise exception 'Reject the unapproved billing draft before correcting context; approved source requires controlled correction' using errcode='23514';end if;
  return new;end $$;
 create trigger visit_billing_context_guard before insert or update or delete on finance_visit_billing_context for each row execute function govern_visit_billing_context();
 create table finance_visit_billing_context_history(id uuid primary key default gen_random_uuid(),visit_id uuid not null,snapshot jsonb not null,recorded_at timestamptz not null default now());
 create function record_billing_context() returns trigger language plpgsql as $$begin insert into finance_visit_billing_context_history(visit_id,snapshot) values(new.visit_id,to_jsonb(new));return new;end $$;
 create trigger visit_billing_context_history after insert or update on finance_visit_billing_context for each row execute function record_billing_context();
 create trigger immutable_billing_context_history before update or delete on finance_visit_billing_context_history for each row execute function protect_timesheet_history();
 alter function finance_source_current(uuid) rename to finance_source_current_base;
 create function finance_source_current(event uuid) returns boolean language sql volatile as $$
  select finance_source_current_base(event) and exists(select 1 from finance_billable_events e join finance_rate_rules r on r.id=e.rate_rule_id left join finance_visit_billing_context c on c.visit_id=e.visit_id and c.organization_id=e.organization_id and c.branch_id=e.branch_id
   where e.id=event and (r.staff_level is null or r.staff_level=c.staff_level) and (r.location_code is null or r.location_code=c.location_code) and (r.minimum_minutes is null or e.billing_minutes>=r.minimum_minutes) and (r.maximum_minutes is null or e.billing_minutes<r.maximum_minutes));
 $$;
 """);
 protected override void Down(MigrationBuilder m)=>throw new NotSupportedException("Retain source contexts and dimension history; recover forwards.");
}
