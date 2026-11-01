using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20261026170000_AddApprovedVatTreatments")]
public sealed class AddApprovedVatTreatments:Migration
{
 protected override void Up(MigrationBuilder m)=>m.Sql("""
 create table finance_vat_authorities(
  id uuid primary key,organization_id uuid not null,branch_id uuid not null,adviser_user_id uuid not null references "AppUsers"("Id"),
  qualification_reference text not null check(length(trim(qualification_reference))>0),valid_from date not null,valid_to date not null check(valid_to>=valid_from),
  created_by uuid not null,approved_by uuid null,approved_at timestamptz null,request_snapshot jsonb not null,created_at timestamptz not null default now(),
  check(approved_by is null or (approved_by<>created_by and approved_by<>adviser_user_id and approved_at is not null)));
 create table finance_vat_treatments(
  id uuid primary key,organization_id uuid not null,branch_id uuid not null,service_type text not null check(length(trim(service_type))>0),
  valid_from date not null,valid_to date not null check(valid_to>=valid_from),treatment text not null check(treatment in ('Exempt','ZeroRated','Taxable')),
  rate numeric(7,4) not null check(rate>=0 and rate<=100),tax_point text not null check(tax_point in ('ServiceDate','InvoiceDate')),
  rounding_mode text not null check(rounding_mode in ('AwayFromZero','ToEven')),reference text not null check(length(trim(reference))>0),
  created_by uuid not null,approved_by uuid null,approved_at timestamptz null,authority_id uuid null references finance_vat_authorities(id),
  request_snapshot jsonb not null,created_at timestamptz not null default now(),check((treatment='Taxable' and rate>0) or (treatment<>'Taxable' and rate=0)),
  check(approved_by is null or (approved_by<>created_by and approved_at is not null and authority_id is not null)));
 alter table finance_invoice_lines add column vat_treatment_id uuid null references finance_vat_treatments(id),add column vat_treatment text null,
  add column vat_rate numeric(7,4) null,add column vat_amount numeric(12,2) null,add column tax_point_date date null;
 create function govern_vat_configuration() returns trigger language plpgsql as $$declare authority finance_vat_authorities%rowtype;begin
  if TG_OP='DELETE' then raise exception 'VAT authority and treatment history is immutable' using errcode='23514';end if;
  if TG_OP='UPDATE' and (old.approved_by is not null or (to_jsonb(new)-'approved_by'-'approved_at'-'authority_id') is distinct from (to_jsonb(old)-'approved_by'-'approved_at'-'authority_id')) then raise exception 'Create a new VAT version' using errcode='23514';end if;
  perform 1 from "Branches" where "Id"=new.branch_id and "OrganizationId"=new.organization_id for update;if not found then raise exception 'VAT branch mismatch' using errcode='23514';end if;
  if TG_TABLE_NAME='finance_vat_authorities' then
   if not exists(select 1 from "AppUsers" where "Id"=new.adviser_user_id and "OrganizationId"=new.organization_id and ("BranchId" is null or "BranchId"=new.branch_id)) then raise exception 'VAT adviser tenant mismatch' using errcode='23514';end if;
  elsif new.approved_by is not null then
   select * into authority from finance_vat_authorities where id=new.authority_id;
   if not found or authority.approved_by is null or authority.organization_id<>new.organization_id or authority.branch_id<>new.branch_id or authority.adviser_user_id<>new.approved_by or (now() at time zone 'Europe/London')::date not between authority.valid_from and authority.valid_to then raise exception 'An approved adviser authority is required' using errcode='23514';end if;
   if exists(select 1 from finance_vat_treatments where id<>new.id and organization_id=new.organization_id and branch_id=new.branch_id and lower(service_type)=lower(new.service_type) and approved_by is not null and daterange(valid_from,valid_to,'[]')&&daterange(new.valid_from,new.valid_to,'[]')) then raise exception 'Approved VAT periods overlap, including different tax point bases' using errcode='23514';end if;
  end if;return new;end $$;
 create trigger vat_authority_guard before insert or update or delete on finance_vat_authorities for each row execute function govern_vat_configuration();
 create trigger vat_treatment_guard before insert or update or delete on finance_vat_treatments for each row execute function govern_vat_configuration();
 create function finance_resolve_vat(event_id uuid,invoice_day date) returns table(treatment_id uuid,treatment text,rate numeric,tax_day date,rounding_mode text) language sql stable as $$
  select t.id,t.treatment,t.rate,case t.tax_point when 'ServiceDate' then e.service_date else invoice_day end,t.rounding_mode
  from finance_billable_events e join finance_contract_versions c on c.id=e.contract_version_id
  join finance_vat_treatments t on t.organization_id=e.organization_id and t.branch_id=e.branch_id and lower(t.service_type)=lower(c.service_type) and t.approved_by is not null
  where e.id=event_id and (case t.tax_point when 'ServiceDate' then e.service_date else invoice_day end) between t.valid_from and t.valid_to;
 $$;
 create function govern_invoice_line_vat() returns trigger language plpgsql as $$declare resolved record;matches integer;invoice_day date;begin
  if TG_OP='UPDATE' and (new.vat_treatment_id is distinct from old.vat_treatment_id or new.vat_treatment is distinct from old.vat_treatment or new.vat_rate is distinct from old.vat_rate or new.vat_amount is distinct from old.vat_amount or new.tax_point_date is distinct from old.tax_point_date) then raise exception 'VAT snapshots are immutable' using errcode='23514';end if;
  if TG_OP<>'INSERT' or new.billable_event_id is null then return new;end if;
  select invoice_date into invoice_day from finance_invoice_details where invoice_id=new.invoice_id and organization_id=new.organization_id;
  select count(*) into matches from finance_resolve_vat(new.billable_event_id,invoice_day);if matches<>1 then raise exception 'Exactly one approved VAT treatment must cover this supply' using errcode='23514';end if;
  select * into resolved from finance_resolve_vat(new.billable_event_id,invoice_day);
  new.vat_treatment_id:=resolved.treatment_id;new.vat_treatment:=resolved.treatment;new.vat_rate:=resolved.rate;new.tax_point_date:=resolved.tax_day;
  new.vat_amount:=finance_policy_round(new.amount*resolved.rate/100,2,resolved.rounding_mode);return new;end $$;
 create trigger invoice_line_vat_guard before insert or update on finance_invoice_lines for each row execute function govern_invoice_line_vat();
 create function verify_invoice_vat_totals() returns trigger language plpgsql as $$declare details finance_invoice_details%rowtype;line_net numeric;line_tax numeric;begin
  if new."Status" in ('Reviewed','Approved','Issued') and exists(select 1 from finance_invoice_lines where invoice_id=new."Id" and billable_event_id is not null) then
   if exists(select 1 from finance_invoice_lines where invoice_id=new."Id" and billable_event_id is not null and vat_treatment_id is null) then raise exception 'Legacy unissued supplies require reviewed VAT configuration and controlled regeneration' using errcode='23514';end if;
   select * into details from finance_invoice_details where invoice_id=new."Id" and organization_id=new."OrganizationId";
   select sum(amount),sum(vat_amount) into line_net,line_tax from finance_invoice_lines where invoice_id=new."Id";
   if details.net_amount<>line_net or details.vat_amount<>line_tax or details.gross_amount<>line_net+line_tax or new."Amount"<>line_net+line_tax then raise exception 'Invoice totals must reconcile to immutable line VAT snapshots' using errcode='23514';end if;
  end if;return new;end $$;
 create trigger invoice_vat_total_guard before update on "Invoices" for each row execute function verify_invoice_vat_totals();
 create view finance_vat_legacy_findings as select i."Id" invoice_id,i."OrganizationId" organization_id,i."BranchId" branch_id,i."Status" status,count(*) missing_lines from "Invoices" i join finance_invoice_lines l on l.invoice_id=i."Id" where l.billable_event_id is not null and l.vat_treatment_id is null group by i."Id",i."OrganizationId",i."BranchId",i."Status";
 """);
 protected override void Down(MigrationBuilder m)=>throw new NotSupportedException("Preserve VAT snapshots and authority history; recover forwards.");
}
