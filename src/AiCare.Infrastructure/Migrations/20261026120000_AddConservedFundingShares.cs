using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20261026120000_AddConservedFundingShares")]
public sealed class AddConservedFundingShares:Migration
{
 protected override void Up(MigrationBuilder m)=>m.Sql("""
  create table finance_allocation_versions(
   id uuid primary key,contract_version_id uuid not null references finance_contract_versions(id),organization_id uuid not null,branch_id uuid not null,
   version integer not null,valid_from date not null,valid_to date not null check(valid_to>=valid_from),reference text not null check(length(trim(reference))>0),
   rounding_policy text not null check(rounding_policy='FloorPercentThenDesignatedRemainder'),
   request_snapshot jsonb not null,created_by uuid not null,created_at timestamptz not null default now(),approved_by uuid null,approved_at timestamptz null,approval_reason text not null default '',
   check(approved_by is null or (approved_by<>created_by and approved_at is not null)),unique(contract_version_id,version));
  create table finance_allocation_rules(
   id uuid primary key,allocation_version_id uuid not null references finance_allocation_versions(id),funder_id uuid not null references finance_funders(id),
   rule_type text not null check(rule_type in ('FixedAmount','Percentage','Remainder')),value numeric(12,4) not null check(value>=0),
   priority integer not null,unique(allocation_version_id,funder_id),unique(allocation_version_id,priority),
   check((rule_type='Percentage' and value<=100) or (rule_type='Remainder' and value=0) or (rule_type='FixedAmount' and value=round(value,2))));
  alter table finance_billable_events add column allocation_version_id uuid null references finance_allocation_versions(id);
  create table finance_event_shares(
   id uuid primary key default gen_random_uuid(),billable_event_id uuid not null references finance_billable_events(id),
   allocation_rule_id uuid not null references finance_allocation_rules(id),organization_id uuid not null,branch_id uuid not null,
   funder_id uuid not null references finance_funders(id),amount numeric(12,2) not null check(amount>=0),currency char(3) not null,
   created_at timestamptz not null default now(),unique(billable_event_id,funder_id));
  alter table finance_invoice_lines add column event_share_id uuid null references finance_event_shares(id);
  drop index ux_invoice_line_billable_event;
  create unique index ux_invoice_line_billable_event on finance_invoice_lines(organization_id,billable_event_id) where billable_event_id is not null and event_share_id is null;
  create unique index ux_invoice_line_event_share on finance_invoice_lines(event_share_id) where event_share_id is not null;
  drop index ux_finance_invoice_visit;
  create unique index ux_finance_invoice_visit on finance_invoice_lines(organization_id,visit_id) where visit_id is not null and event_share_id is null;

  create function govern_finance_allocation() returns trigger language plpgsql as $$
  declare c finance_contract_versions%rowtype;
  begin
   if TG_OP='DELETE' then raise exception 'Allocation history cannot be deleted' using errcode='23514'; end if;
   select * into c from finance_contract_versions where id=new.contract_version_id for update;
   if not found or c.status<>'Approved' or c.organization_id<>new.organization_id or c.branch_id<>new.branch_id or new.valid_from<c.authorised_from or new.valid_to>c.authorised_to then raise exception 'Allocation contract or date scope mismatch' using errcode='23514'; end if;
   if TG_OP='UPDATE' and (old.approved_by is not null or (to_jsonb(new)-'approved_by'-'approved_at'-'approval_reason') is distinct from (to_jsonb(old)-'approved_by'-'approved_at'-'approval_reason')) then raise exception 'Create a new allocation version' using errcode='23514'; end if;
   if new.approved_by is not null then
    if exists(select 1 from finance_allocation_versions a where a.id<>new.id and a.contract_version_id=new.contract_version_id and a.approved_by is not null and daterange(a.valid_from,a.valid_to,'[]') && daterange(new.valid_from,new.valid_to,'[]')) then raise exception 'Approved allocation versions overlap' using errcode='23P01'; end if;
    if (select count(*) from finance_allocation_rules where allocation_version_id=new.id and rule_type='Remainder')<>1
     or (select count(*) from finance_allocation_rules where allocation_version_id=new.id)<2
     or (select coalesce(sum(value),0) from finance_allocation_rules where allocation_version_id=new.id and rule_type='Percentage')>100 then raise exception 'An allocation needs distinct payers, at most 100 percent, and one designated remainder payer' using errcode='23514'; end if;
    if exists(select 1 from finance_allocation_rules r left join finance_funders f on f.id=r.funder_id where r.allocation_version_id=new.id
     and (f.organization_id is distinct from c.organization_id or (f.branch_id is not null and f.branch_id<>c.branch_id) or f.currency<>c.currency or not f.active)) then raise exception 'Allocation payer scope/currency mismatch' using errcode='23514'; end if;
   end if;return new;
  end $$;
  create trigger finance_allocation_version_guard before insert or update or delete on finance_allocation_versions for each row execute function govern_finance_allocation();
  create function govern_finance_allocation_rule() returns trigger language plpgsql as $$
  declare target uuid;c finance_contract_versions%rowtype;
  begin
   if TG_OP='DELETE' then raise exception 'Allocation rules cannot be deleted' using errcode='23514'; end if;
   target:=new.allocation_version_id;
   perform 1 from finance_allocation_versions where id=target and approved_by is null for update;
   if not found or TG_OP='UPDATE' then raise exception 'Allocation rules are immutable; create a new version' using errcode='23514'; end if;
   select contract.* into c from finance_contract_versions contract join finance_allocation_versions a on a.contract_version_id=contract.id where a.id=target;
   perform 1 from finance_funders where id=new.funder_id and organization_id=c.organization_id and (branch_id is null or branch_id=c.branch_id) and currency=c.currency and active for share;
   if not found then raise exception 'Allocation payer scope/currency mismatch' using errcode='23514'; end if;
   return new;
  end $$;
  create trigger finance_allocation_rule_guard before insert or update or delete on finance_allocation_rules for each row execute function govern_finance_allocation_rule();

  create function preserve_allocation_payer_identity() returns trigger language plpgsql as $$ begin
   if (new.organization_id,new.branch_id,new.currency) is distinct from (old.organization_id,old.branch_id,old.currency) and exists(select 1 from finance_allocation_rules where funder_id=old.id) then raise exception 'Referenced allocation payer identity is immutable' using errcode='23514'; end if;return new;
  end $$;
  create trigger finance_allocation_payer_guard before update on finance_funders for each row execute function preserve_allocation_payer_identity();

  create function pin_finance_allocation() returns trigger language plpgsql as $$
  declare selected uuid;
  begin
   if new.status='Approved' and (TG_OP='INSERT' or old.status='Validated') then
    perform 1 from finance_contract_versions where id=new.contract_version_id for update;
    select id into selected from finance_allocation_versions where contract_version_id=new.contract_version_id and approved_by is not null and new.service_date between valid_from and valid_to;
    if selected is null and exists(select 1 from finance_allocation_versions where contract_version_id=new.contract_version_id) then raise exception 'Funding allocation needs independent approval' using errcode='23514'; end if;
    new.allocation_version_id:=selected;
   end if;return new;
  end $$;
  -- Pin before the existing lineage guard records the approved snapshot.
  create trigger a_finance_pin_allocation before insert or update on finance_billable_events for each row execute function pin_finance_allocation();
  create function materialise_finance_shares() returns trigger language plpgsql as $$
  declare used numeric;remainder_rule uuid;
  begin
   if new.status='Approved' and new.allocation_version_id is not null and (TG_OP='INSERT' or old.status='Validated') then
    insert into finance_event_shares(billable_event_id,allocation_rule_id,organization_id,branch_id,funder_id,amount,currency)
     select new.id,r.id,new.organization_id,new.branch_id,r.funder_id,
      case r.rule_type when 'FixedAmount' then r.value else floor(new.final_amount*r.value)/100 end,new.currency
     from finance_allocation_rules r where allocation_version_id=new.allocation_version_id and rule_type<>'Remainder';
    select coalesce(sum(amount),0) into used from finance_event_shares where billable_event_id=new.id;
    if used>new.final_amount then raise exception 'Fixed/percentage funding exceeds the authorised event amount' using errcode='23514'; end if;
    insert into finance_event_shares(billable_event_id,allocation_rule_id,organization_id,branch_id,funder_id,amount,currency)
     select new.id,r.id,new.organization_id,new.branch_id,r.funder_id,new.final_amount-used,new.currency from finance_allocation_rules r where allocation_version_id=new.allocation_version_id and rule_type='Remainder';
   end if;return new;
  end $$;
  create trigger finance_materialise_shares after insert or update on finance_billable_events for each row execute function materialise_finance_shares();
  create function guard_finance_share() returns trigger language plpgsql as $$ begin
   if TG_OP<>'INSERT' then raise exception 'Approved funding shares are immutable' using errcode='23514'; end if;
   if not exists(select 1 from finance_billable_events e join finance_allocation_rules r on r.allocation_version_id=e.allocation_version_id where e.id=new.billable_event_id and e.status='Approved' and e.organization_id=new.organization_id and e.branch_id=new.branch_id and e.currency=new.currency and r.id=new.allocation_rule_id and r.funder_id=new.funder_id) then raise exception 'Funding share lineage mismatch' using errcode='23514'; end if;
   if not exists(select 1 from finance_allocation_rules r join finance_billable_events e on e.id=new.billable_event_id where r.id=new.allocation_rule_id
     and new.amount=case r.rule_type when 'FixedAmount' then r.value when 'Percentage' then floor(e.final_amount*r.value)/100
      else e.final_amount-(select coalesce(sum(case x.rule_type when 'FixedAmount' then x.value else floor(e.final_amount*x.value)/100 end),0) from finance_allocation_rules x where x.allocation_version_id=e.allocation_version_id and x.rule_type<>'Remainder') end)
     then raise exception 'Funding share does not match its approved calculation' using errcode='23514'; end if;
   return new;
  end $$;
  create trigger finance_share_guard before insert or update or delete on finance_event_shares for each row execute function guard_finance_share();
  create function check_finance_share_total() returns trigger language plpgsql as $$
  declare event_id uuid;expected numeric;actual numeric;
  begin
   if TG_TABLE_NAME='finance_event_shares' then event_id:=new.billable_event_id; else event_id:=new.id; end if;
   select final_amount into expected from finance_billable_events where id=event_id and allocation_version_id is not null and status in ('Approved','Invoiced');
   if found then select coalesce(sum(amount),0) into actual from finance_event_shares where billable_event_id=event_id;
    if actual<>expected then raise exception 'Funding shares must conserve the approved event amount exactly' using errcode='23514'; end if;
   end if;return null;
  end $$;
  create constraint trigger finance_share_total_event after insert or update on finance_billable_events deferrable initially deferred for each row execute function check_finance_share_total();
  create constraint trigger finance_share_total_row after insert on finance_event_shares deferrable initially deferred for each row execute function check_finance_share_total();

  create or replace function guard_invoice_billing_line() returns trigger language plpgsql as $$ begin
   if new.event_share_id is not null and exists(select 1 from finance_invoice_lines where organization_id=new.organization_id and visit_id=new.visit_id and event_share_id is null) then raise exception 'Visit already has an unsplit invoice line' using errcode='23514'; end if;
   if new.billable_event_id is not null then
    perform 1 from finance_billable_events e join "Invoices" i on i."Id"=new.invoice_id join finance_invoice_details d on d.invoice_id=i."Id" and d.organization_id=i."OrganizationId"
     where e.id=new.billable_event_id and e.organization_id=new.organization_id and e.branch_id=new.branch_id and e.service_user_id=new.service_user_id and e.visit_id=new.visit_id
      and i."OrganizationId"=e.organization_id and i."BranchId"=e.branch_id and d.currency=e.currency
      and e.status in ('Approved','Invoiced') and finance_source_current(e.id)
      and ((new.event_share_id is null and e.allocation_version_id is null and e.final_amount=new.amount and e.quantity=new.quantity and e.unit_rate=new.unit_rate)
        or exists(select 1 from finance_event_shares s where s.id=new.event_share_id and s.billable_event_id=e.id and s.organization_id=e.organization_id and s.branch_id=e.branch_id and s.amount=new.amount and new.quantity=1 and new.unit_rate=s.amount and d.payer_id=s.funder_id)) for update of e;
    if not found then raise exception 'Invoice line must match an approved event or its exact payer share' using errcode='23514'; end if;
   elsif new.event_share_id is not null then raise exception 'A share requires its source event' using errcode='23514'; end if;return new;
  end $$;
  """);
 protected override void Down(MigrationBuilder m)=>throw new NotSupportedException("Funding-share history requires forward recovery.");
}
