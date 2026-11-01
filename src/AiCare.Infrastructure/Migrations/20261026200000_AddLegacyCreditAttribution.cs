using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20261026200000_AddLegacyCreditAttribution")]
public sealed class AddLegacyCreditAttribution:Migration
{
 protected override void Up(MigrationBuilder m)=>m.Sql("""
 create table finance_legacy_credit_attributions(
  id uuid primary key,credit_id uuid not null references finance_credit_notes(id),organization_id uuid not null,branch_id uuid not null,
  net_amount numeric(12,2) not null check(net_amount>=0),tax_amount numeric(12,2) not null check(tax_amount>=0),service_date date not null,
  reference text not null check(length(trim(reference))>0),reason text not null check(length(trim(reason))>0),request_snapshot jsonb not null,
  created_by uuid not null,approved_by uuid null,approved_at timestamptz null,authority_id uuid null references finance_adjustment_authorities(id),created_at timestamptz not null default now(),
  check(approved_by is null or (approved_by<>created_by and approved_at is not null and authority_id is not null)));
 create unique index ux_approved_legacy_credit_attribution on finance_legacy_credit_attributions(credit_id) where approved_by is not null;
 create function govern_legacy_credit_attribution() returns trigger language plpgsql as $$declare credit finance_credit_notes%rowtype;details finance_invoice_details%rowtype;known_net numeric;known_tax numeric;begin
  if TG_OP='DELETE' then raise exception 'Credit attribution history is immutable' using errcode='23514';end if;
  if TG_OP='UPDATE' and (old.approved_by is not null or (to_jsonb(new)-'approved_by'-'approved_at'-'authority_id') is distinct from (to_jsonb(old)-'approved_by'-'approved_at'-'authority_id')) then raise exception 'Credit attribution source is immutable' using errcode='23514';end if;
  select * into credit from finance_credit_notes where id=new.credit_id and organization_id=new.organization_id and branch_id=new.branch_id and status='Issued';
  if not found or credit.net_amount is not null or credit.tax_amount is not null or credit.service_date is not null or new.net_amount+new.tax_amount<>credit.amount then raise exception 'Only a wholly unattributed issued legacy credit can be attributed; exact total required' using errcode='23514';end if;
  perform 1 from "Invoices" where "Id"=credit.invoice_id for update;select * into details from finance_invoice_details where invoice_id=credit.invoice_id and organization_id=new.organization_id;
  if new.approved_by is not null then
   if not exists(select 1 from finance_adjustment_authorities where id=new.authority_id and organization_id=new.organization_id and branch_id=new.branch_id and reviewer_id=new.approved_by and approved_by is not null and currency=details.currency and maximum_amount>=credit.amount and (now() at time zone 'Europe/London')::date between valid_from and valid_to) then raise exception 'Approved reviewer authority required' using errcode='23514';end if;
   select coalesce(sum(coalesce(c.net_amount,a.net_amount)),0),coalesce(sum(coalesce(c.tax_amount,a.tax_amount)),0) into known_net,known_tax from finance_credit_notes c left join finance_legacy_credit_attributions a on a.credit_id=c.id and a.approved_by is not null and a.id<>new.id where c.invoice_id=credit.invoice_id and c.status='Issued' and c.id<>credit.id;
   if new.net_amount+known_net>details.net_amount or new.tax_amount+known_tax>details.vat_amount then raise exception 'Attribution exceeds original invoice net or tax capacity' using errcode='23514';end if;
  end if;return new;end $$;
 create trigger legacy_credit_attribution_guard before insert or update or delete on finance_legacy_credit_attributions for each row execute function govern_legacy_credit_attribution();
 create view finance_credit_reporting as select c.id,c.invoice_id,c.organization_id,c.branch_id,c.amount,c.status,
  coalesce(c.net_amount,a.net_amount) net_amount,coalesce(c.tax_amount,a.tax_amount) tax_amount,coalesce(c.service_date,a.service_date) service_date
  from finance_credit_notes c left join finance_legacy_credit_attributions a on a.credit_id=c.id and a.organization_id=c.organization_id and a.branch_id=c.branch_id and a.approved_by is not null;
 -- Later credits must include approved legacy attributions when checking remaining net/tax capacity.
 do $$declare definition text;begin
  select pg_get_functiondef('govern_controlled_posting()'::regprocedure) into definition;
  definition:=replace(definition,'sum(net_amount) from finance_credit_notes','sum(net_amount) from finance_credit_reporting');
  definition:=replace(definition,'sum(tax_amount) from finance_credit_notes','sum(tax_amount) from finance_credit_reporting');
  definition:=replace(definition,'if r.kind=''Credit'' then','if r.kind=''Credit'' then if exists(select 1 from finance_credit_reporting where invoice_id=new.invoice_id and organization_id=new.organization_id and status=''Issued'' and (net_amount is null or tax_amount is null)) then raise exception ''Attribute legacy credits before further credits'' using errcode=''23514'';end if;');execute definition;
 end $$;
 """);
 protected override void Down(MigrationBuilder m)=>throw new NotSupportedException("Retain append-only legacy attribution decisions; recover forwards.");
}
