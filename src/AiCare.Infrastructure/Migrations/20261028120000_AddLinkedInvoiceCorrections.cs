using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261028120000_AddLinkedInvoiceCorrections")]
public sealed class AddLinkedInvoiceCorrections : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table finance_invoice_corrections(
          id uuid primary key,original_invoice_id uuid not null references "Invoices"("Id"),
          replacement_invoice_id uuid null unique references "Invoices"("Id"),organization_id uuid not null,branch_id uuid not null,
          invoice_date date not null,payment_terms_days integer not null check(payment_terms_days between 1 and 365),
          source_correction_reference text not null check(length(trim(source_correction_reference))>0),reason text not null check(length(trim(reason))>0),
          request_snapshot jsonb not null,status text not null default 'Submitted' check(status in ('Submitted','Approved','Rejected')),
          created_by uuid not null,reviewed_by uuid null,reviewed_at timestamptz null,review_reason text not null default '',
          authority_id uuid null references finance_adjustment_authorities(id),created_at timestamptz not null default now(),
          check(status='Submitted' or (reviewed_by is not null and reviewed_by<>created_by and reviewed_at is not null and length(trim(review_reason))>0)),
          check(status<>'Approved' or (replacement_invoice_id is not null and authority_id is not null)),
          check(replacement_invoice_id is null or replacement_invoice_id<>original_invoice_id));
        create unique index ux_active_invoice_correction on finance_invoice_corrections(original_invoice_id) where status<>'Rejected';
        create table finance_invoice_correction_lines(
          id uuid primary key,correction_id uuid not null references finance_invoice_corrections(id),
          original_line_id uuid not null references finance_invoice_lines(id),service_date date not null,service_type text not null check(length(trim(service_type))>0),
          description text not null check(length(trim(description))>0),quantity numeric(16,8) not null check(quantity>0),unit_rate numeric(12,4) not null check(unit_rate>=0),
          rounding_mode text not null check(rounding_mode in ('AwayFromZero','ToEven')),net_amount numeric(12,2) not null check(net_amount>=0),
          vat_treatment_id uuid not null references finance_vat_treatments(id),vat_treatment text not null,vat_rate numeric(7,4) not null,
          vat_amount numeric(12,2) not null check(vat_amount>=0),tax_point_date date not null,unique(correction_id,original_line_id));
        alter table finance_invoice_lines add column correction_line_id uuid null unique references finance_invoice_correction_lines(id);
        create function govern_invoice_correction() returns trigger language plpgsql as $$
        declare original finance_invoice_ledger%rowtype;details finance_invoice_details%rowtype;new_gross numeric;
        begin
          if TG_OP='DELETE' then raise exception 'Correction history is immutable' using errcode='23514';end if;
          if TG_OP='INSERT' and new.status<>'Submitted' then raise exception 'Submit correction before review' using errcode='23514';end if;
          if TG_OP='UPDATE' and (old.status<>'Submitted' or new.status not in ('Approved','Rejected') or
            (to_jsonb(new)-'status'-'reviewed_by'-'reviewed_at'-'review_reason'-'authority_id'-'replacement_invoice_id') is distinct from
            (to_jsonb(old)-'status'-'reviewed_by'-'reviewed_at'-'review_reason'-'authority_id'-'replacement_invoice_id')) then
            raise exception 'Correction source is immutable' using errcode='23514';end if;
          perform 1 from "Invoices" where "Id"=new.original_invoice_id for update;
          select * into original from finance_invoice_ledger where invoice_id=new.original_invoice_id and organization_id=new.organization_id and branch_id=new.branch_id;
          if not found or not original.is_issued then raise exception 'Correction requires an issued original in scope' using errcode='23514';end if;
          select * into details from finance_invoice_details where invoice_id=new.original_invoice_id;
          if details.payer_id is null or details.care_recipient_id is null then raise exception 'Legacy payer/recipient provenance requires explicit reconciliation before replacement' using errcode='23514';end if;
          if new.status='Approved' then
            if original.credits<>original.gross_amount or original.write_offs<>0 or exists(select 1 from finance_credit_reporting
              where invoice_id=new.original_invoice_id and status='Issued' and (net_amount is null or tax_amount is null)) then
              raise exception 'Fully credit and attribute the original invoice before creating a replacement; cash stays on the original' using errcode='23514';end if;
            if (select count(*) from finance_invoice_correction_lines where correction_id=new.id)<>(select count(*) from finance_invoice_lines where invoice_id=new.original_invoice_id) then
              raise exception 'Every original source line must be represented exactly once in the replacement' using errcode='23514';end if;
            select sum(net_amount+vat_amount) into new_gross from finance_invoice_correction_lines where correction_id=new.id;
            if new_gross is null or not exists(select 1 from finance_adjustment_authorities where id=new.authority_id and organization_id=new.organization_id
              and branch_id=new.branch_id and reviewer_id=new.reviewed_by and approved_by is not null and currency=original.currency
              and maximum_amount>=greatest(new_gross,original.gross_amount) and (now() at time zone 'Europe/London')::date between valid_from and valid_to) then
              raise exception 'Independent approved authority must cover both original and replacement amounts' using errcode='23514';end if;
          end if;return new;
        end $$;
        create trigger invoice_correction_guard before insert or update or delete on finance_invoice_corrections for each row execute function govern_invoice_correction();
        create function govern_invoice_correction_line() returns trigger language plpgsql as $$
        declare header finance_invoice_corrections%rowtype;policy finance_vat_treatments%rowtype;
        begin
          if TG_OP<>'INSERT' then raise exception 'Correction lines are immutable' using errcode='23514';end if;
          select * into header from finance_invoice_corrections where id=new.correction_id for update;
          if not found or header.status<>'Submitted' or not exists(select 1 from finance_invoice_lines where id=new.original_line_id
            and invoice_id=header.original_invoice_id and organization_id=header.organization_id and branch_id=header.branch_id) then
            raise exception 'Correction must link a line on its original invoice' using errcode='23514';end if;
          select * into policy from finance_vat_treatments where id=new.vat_treatment_id and approved_by is not null
            and organization_id=header.organization_id and branch_id=header.branch_id and lower(service_type)=lower(new.service_type);
          if not found or (case policy.tax_point when 'ServiceDate' then new.service_date else header.invoice_date end) not between policy.valid_from and policy.valid_to
            or new.net_amount<>finance_policy_round(new.quantity*new.unit_rate,2,new.rounding_mode)
            or new.vat_treatment<>policy.treatment or new.vat_rate<>policy.rate
            or new.tax_point_date<>(case policy.tax_point when 'ServiceDate' then new.service_date else header.invoice_date end)
            or new.vat_amount<>finance_policy_round(new.net_amount*policy.rate/100,2,policy.rounding_mode) then
            raise exception 'Correction calculation and adviser VAT snapshots must match approved policy' using errcode='23514';end if;
          return new;
        end $$;
        create trigger invoice_correction_line_guard before insert or update or delete on finance_invoice_correction_lines for each row execute function govern_invoice_correction_line();
        create function govern_replacement_invoice_line() returns trigger language plpgsql as $$begin
          if TG_OP='UPDATE' and new.correction_line_id is distinct from old.correction_line_id then raise exception 'Correction source is immutable' using errcode='23514';end if;
          if TG_OP<>'INSERT' or new.correction_line_id is null then return new;end if;
          if new.visit_id is not null or new.billable_event_id is not null or new.event_share_id is not null or new.recharge_id is not null then
            raise exception 'Replacement line must use its explicit correction lineage' using errcode='23514';end if;
          perform 1 from finance_invoice_correction_lines l join finance_invoice_corrections c on c.id=l.correction_id
            join finance_invoice_details d on d.invoice_id=c.replacement_invoice_id where l.id=new.correction_line_id and c.status='Approved'
              and c.replacement_invoice_id=new.invoice_id and c.organization_id=new.organization_id and c.branch_id=new.branch_id
              and d.care_recipient_id=new.service_user_id and l.quantity=new.quantity and l.unit_rate=new.unit_rate and l.net_amount=new.amount
              and l.description=new.description;
          if not found then raise exception 'Replacement invoice line must match its approved source correction' using errcode='23514';end if;
          select vat_treatment_id,vat_treatment,vat_rate,vat_amount,tax_point_date into
            new.vat_treatment_id,new.vat_treatment,new.vat_rate,new.vat_amount,new.tax_point_date
            from finance_invoice_correction_lines where id=new.correction_line_id;
          return new;
        end $$;
        create trigger a_replacement_invoice_line before insert or update on finance_invoice_lines for each row execute function govern_replacement_invoice_line();
        create function verify_replacement_invoice() returns trigger language plpgsql as $$declare expected numeric;actual numeric;begin
          if new.status='Approved' then
            select sum(net_amount+vat_amount) into expected from finance_invoice_correction_lines where correction_id=new.id;
            select sum(l.amount+l.vat_amount) into actual from finance_invoice_lines l join finance_invoice_correction_lines c on c.id=l.correction_line_id
              where c.correction_id=new.id and l.invoice_id=new.replacement_invoice_id;
            if actual is distinct from expected or (select count(*) from finance_invoice_lines where invoice_id=new.replacement_invoice_id)<>
              (select count(*) from finance_invoice_correction_lines where correction_id=new.id) or not exists(select 1 from finance_invoice_ledger
                where invoice_id=new.replacement_invoice_id and organization_id=new.organization_id and branch_id=new.branch_id and gross_amount=expected) then
              raise exception 'Approved correction and complete replacement invoice must commit atomically' using errcode='23514';end if;
          end if;return null;end $$;
        create constraint trigger correction_replacement_complete after insert or update on finance_invoice_corrections
          deferrable initially deferred for each row execute function verify_replacement_invoice();
        do $$declare definition text;begin
          select pg_get_functiondef('verify_invoice_vat_totals()'::regprocedure) into definition;
          definition:=replace(definition,'recharge_id is not null','recharge_id is not null or correction_line_id is not null');execute definition;
        end $$;
        """);

    protected override void Down(MigrationBuilder m) => throw new NotSupportedException("Preserve correction and replacement history; recover forwards.");
}
