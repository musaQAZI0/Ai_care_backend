using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20261026130000_AddFundingPeriodCaps")]
public sealed class AddFundingPeriodCaps:Migration
{
 protected override void Up(MigrationBuilder m)=>m.Sql("""
  create table finance_period_authorisations(
   id uuid primary key,contract_version_id uuid not null references finance_contract_versions(id),funding_arrangement_id uuid not null references funding_arrangements(id),
   organization_id uuid not null,branch_id uuid not null,valid_from date not null,valid_to date not null check(valid_to>=valid_from),
   reference text not null check(length(trim(reference))>0),amount_limit numeric(12,2) null check(amount_limit>0),minutes_limit numeric(16,4) null check(minutes_limit>0),
   request_snapshot jsonb not null,created_by uuid not null,created_at timestamptz not null default now(),approved_by uuid null,approved_at timestamptz null,approval_reason text not null default '',
   check(amount_limit is not null or minutes_limit is not null),check(approved_by is null or (approved_by<>created_by and approved_at is not null)));
  alter table finance_billable_events add column period_authorisation_id uuid null references finance_period_authorisations(id);
  create function govern_period_authorisation() returns trigger language plpgsql as $$
  declare c finance_contract_versions%rowtype;used_amount numeric;used_minutes numeric;
  begin
   if TG_OP='DELETE' then raise exception 'Authorisation history cannot be deleted' using errcode='23514'; end if;
   select * into c from finance_contract_versions where id=new.contract_version_id;
   if not found or c.status<>'Approved' or c.funding_arrangement_id<>new.funding_arrangement_id or c.organization_id<>new.organization_id or c.branch_id<>new.branch_id or new.valid_from<c.authorised_from or new.valid_to>c.authorised_to then raise exception 'Period authorisation contract coverage mismatch' using errcode='23514'; end if;
   perform 1 from funding_arrangements where id=new.funding_arrangement_id for update;
   if TG_OP='UPDATE' and (old.approved_by is not null or (to_jsonb(new)-'approved_by'-'approved_at'-'approval_reason') is distinct from (to_jsonb(old)-'approved_by'-'approved_at'-'approval_reason')) then raise exception 'Period authorisations are immutable; create a replacement period' using errcode='23514'; end if;
   if new.approved_by is not null then
    if new.minutes_limit is not null and exists(select 1 from finance_billable_events where funding_arrangement_id=new.funding_arrangement_id and service_date between new.valid_from and new.valid_to and status<>'Rejected' and billing_minutes is null) then raise exception 'Legacy minute commitments require review before approving a unit cap' using errcode='23514'; end if;
    if exists(select 1 from finance_period_authorisations a where a.id<>new.id and a.funding_arrangement_id=new.funding_arrangement_id and a.approved_by is not null and daterange(a.valid_from,a.valid_to,'[]') && daterange(new.valid_from,new.valid_to,'[]')) then raise exception 'Approved authorisation periods overlap' using errcode='23P01'; end if;
    select coalesce(sum(final_amount),0),coalesce(sum(billing_minutes),0) into used_amount,used_minutes from finance_billable_events where funding_arrangement_id=new.funding_arrangement_id and service_date between new.valid_from and new.valid_to and status<>'Rejected';
    if (new.amount_limit is not null and used_amount>new.amount_limit) or (new.minutes_limit is not null and used_minutes>new.minutes_limit) then raise exception 'Period limit is below committed service' using errcode='23514'; end if;
   end if;return new;
  end $$;
  create trigger finance_period_authorisation_guard before insert or update or delete on finance_period_authorisations for each row execute function govern_period_authorisation();
  create function enforce_period_authorisation() returns trigger language plpgsql as $$
  declare a finance_period_authorisations%rowtype;used_amount numeric;used_minutes numeric;
  begin
   if new.status='Rejected' then return new; end if;
   perform 1 from funding_arrangements where id=new.funding_arrangement_id for update;
   if TG_OP='UPDATE' and old.status in ('Approved','Invoiced') then return new; end if;
   select * into a from finance_period_authorisations where funding_arrangement_id=new.funding_arrangement_id and approved_by is not null and new.service_date between valid_from and valid_to;
   if not found then
    if exists(select 1 from finance_period_authorisations where funding_arrangement_id=new.funding_arrangement_id) then raise exception 'No approved period authorisation covers this service date' using errcode='23514'; end if;
    return new;
   end if;
   select coalesce(sum(final_amount),0),coalesce(sum(billing_minutes),0) into used_amount,used_minutes from finance_billable_events where funding_arrangement_id=new.funding_arrangement_id and id<>new.id and service_date between a.valid_from and a.valid_to and status<>'Rejected';
   if new.billing_minutes is null or (a.amount_limit is not null and used_amount+new.final_amount>a.amount_limit) or (a.minutes_limit is not null and used_minutes+new.billing_minutes>a.minutes_limit) then raise exception 'Period amount/minute authorisation exceeded' using errcode='23514'; end if;
   new.period_authorisation_id:=a.id;return new;
  end $$;
  create trigger a_finance_period_cap before insert or update on finance_billable_events for each row execute function enforce_period_authorisation();
  """);
 protected override void Down(MigrationBuilder m)=>throw new NotSupportedException("Period authorisation history requires forward recovery.");
}
