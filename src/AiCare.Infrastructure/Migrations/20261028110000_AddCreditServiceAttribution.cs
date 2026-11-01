using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261028110000_AddCreditServiceAttribution")]
public sealed class AddCreditServiceAttribution : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table finance_credit_service_attributions(
          id uuid primary key,credit_id uuid not null references finance_credit_notes(id),organization_id uuid not null,branch_id uuid not null,
          reference text not null check(length(trim(reference))>0),reason text not null check(length(trim(reason))>0),
          created_by uuid not null,approved_by uuid null,approved_at timestamptz null,
          authority_id uuid null references finance_adjustment_authorities(id),request_snapshot jsonb not null,
          created_at timestamptz not null default now(),
          check(approved_by is null or (approved_by<>created_by and approved_at is not null and authority_id is not null)));
        create unique index ux_approved_credit_service_attribution on finance_credit_service_attributions(credit_id) where approved_by is not null;
        create table finance_credit_service_lines(
          id uuid primary key,attribution_id uuid not null references finance_credit_service_attributions(id),
          invoice_line_id uuid not null references finance_invoice_lines(id),service_date date not null,
          net_amount numeric(12,2) not null check(net_amount>=0),tax_amount numeric(12,2) not null check(tax_amount>=0),
          check(net_amount+tax_amount>0),unique(attribution_id,invoice_line_id));
        create function govern_credit_service_attribution() returns trigger language plpgsql as $$
        declare credit finance_credit_reporting%rowtype;details finance_invoice_details%rowtype;net numeric;tax numeric;
        begin
          if TG_OP='DELETE' then raise exception 'Credit service attribution history is immutable' using errcode='23514';end if;
          if TG_OP='UPDATE' and (old.approved_by is not null or (to_jsonb(new)-'approved_by'-'approved_at'-'authority_id') is distinct from
            (to_jsonb(old)-'approved_by'-'approved_at'-'authority_id')) then raise exception 'Credit service attribution is immutable' using errcode='23514';end if;
          select * into credit from finance_credit_reporting where id=new.credit_id and organization_id=new.organization_id and branch_id=new.branch_id and status='Issued';
          if not found or credit.net_amount is null or credit.tax_amount is null then
            raise exception 'Resolve aggregate legacy credit net/tax attribution first' using errcode='23514';end if;
          perform 1 from "Invoices" where "Id"=credit.invoice_id for update;
          if new.approved_by is not null then
            select * into details from finance_invoice_details where invoice_id=credit.invoice_id;
            if not exists(select 1 from finance_adjustment_authorities where id=new.authority_id and organization_id=new.organization_id
              and branch_id=new.branch_id and reviewer_id=new.approved_by and approved_by is not null and currency=details.currency
              and maximum_amount>=credit.amount and (now() at time zone 'Europe/London')::date between valid_from and valid_to) then
              raise exception 'Independent approved credit attribution authority required' using errcode='23514';end if;
            select sum(net_amount),sum(tax_amount) into net,tax from finance_credit_service_lines where attribution_id=new.id;
            if net is distinct from credit.net_amount or tax is distinct from credit.tax_amount then
              raise exception 'Service allocations must exactly conserve the original credit net and tax' using errcode='23514';end if;
            if exists(select 1 from finance_credit_service_lines allocation join finance_invoice_lines source on source.id=allocation.invoice_line_id
              where allocation.attribution_id=new.id and (source.invoice_id<>credit.invoice_id or source.organization_id<>new.organization_id
                or source.branch_id<>new.branch_id or source.vat_amount is null
                or allocation.net_amount+coalesce((select sum(other.net_amount) from finance_credit_service_lines other
                  join finance_credit_service_attributions a on a.id=other.attribution_id where a.approved_by is not null
                  and a.id<>new.id and other.invoice_line_id=source.id),0)>source.amount
                or allocation.tax_amount+coalesce((select sum(other.tax_amount) from finance_credit_service_lines other
                  join finance_credit_service_attributions a on a.id=other.attribution_id where a.approved_by is not null
                  and a.id<>new.id and other.invoice_line_id=source.id),0)>source.vat_amount)) then
              raise exception 'Credit service allocation exceeds an original line net/tax capacity or lacks VAT lineage' using errcode='23514';end if;
          end if;return new;
        end $$;
        create trigger credit_service_attribution_guard before insert or update or delete on finance_credit_service_attributions
          for each row execute function govern_credit_service_attribution();
        create function govern_credit_service_line() returns trigger language plpgsql as $$begin
          if TG_OP<>'INSERT' then raise exception 'Credit allocation lines are immutable' using errcode='23514';end if;
          perform 1 from finance_credit_service_attributions a join finance_credit_notes c on c.id=a.credit_id
            join finance_invoice_lines l on l.invoice_id=c.invoice_id and l.organization_id=a.organization_id and l.branch_id=a.branch_id
            where a.id=new.attribution_id and a.approved_by is null and l.id=new.invoice_line_id for update of a;
          if not found then raise exception 'Credit service line requires an accessible original invoice line and draft attribution' using errcode='23514';end if;
          return new;end $$;
        create trigger credit_service_line_guard before insert or update or delete on finance_credit_service_lines for each row execute function govern_credit_service_line();
        """);

    protected override void Down(MigrationBuilder m) => throw new NotSupportedException("Preserve reviewed credit allocations; recover forwards.");
}
