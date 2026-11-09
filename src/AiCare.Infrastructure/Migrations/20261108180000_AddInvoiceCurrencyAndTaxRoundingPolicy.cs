using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261108180000_AddInvoiceCurrencyAndTaxRoundingPolicy")]
public sealed class AddInvoiceCurrencyAndTaxRoundingPolicy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table finance_invoice_profiles
          add column default_currency char(3) not null default 'GBP' check(default_currency ~ '^[A-Z]{3}$'),
          add column currency_symbol text not null default '£' check(length(currency_symbol) between 1 and 8),
          add column tax_label text not null default 'VAT' check(length(trim(tax_label)) between 1 and 30),
          add column vat_rounding_policy text not null default 'Early' check(vat_rounding_policy in ('Early','Late'));

        create or replace function populate_invoice_snapshot() returns trigger language plpgsql as $$declare profile jsonb;begin
          if new.settings_snapshot is null then
            select profile_snapshot into profile from finance_invoice_profile_versions where organization_id=new.organization_id and branch_id=new.branch_id and effective_from<=now() order by effective_from desc,version desc limit 1;
            new.settings_snapshot:=jsonb_build_object('providerName',new.provider_name,'providerAddress',new.provider_address,'providerEmail',new.provider_email,'providerPhone',new.provider_phone,'companyNumber',new.company_number,'vatNumber',new.vat_number,'remittanceDetails',new.remittance_details,'paymentTerms',new.payment_terms,'vatRate',new.vat_rate,'vatExemptionReason',new.vat_exemption_reason,'currency',new.currency,'defaultCurrency',coalesce(profile->>'defaultCurrency',trim(new.currency)),'currencySymbol',coalesce(profile->>'currencySymbol',trim(new.currency)),'taxLabel',coalesce(profile->>'taxLabel','VAT'),'vatRoundingPolicy',coalesce(profile->>'vatRoundingPolicy','Early'),'logoSha256',new.logo_sha256,'profileVersion',(select version from finance_invoice_profile_versions where organization_id=new.organization_id and branch_id=new.branch_id and effective_from<=now() order by effective_from desc,version desc limit 1));
          end if;
          if new.calculation_snapshot is null then new.calculation_snapshot:=jsonb_build_object('net',new.net_amount,'vatRate',new.vat_rate,'vat',new.vat_amount,'gross',new.gross_amount,'currency',new.currency);end if;return new;end $$;

        create or replace function govern_invoice_line_vat() returns trigger language plpgsql as $$declare resolved record;matches integer;invoice_day date;policy text;begin
          if TG_OP='UPDATE' and (new.vat_treatment_id is distinct from old.vat_treatment_id or new.vat_treatment is distinct from old.vat_treatment or new.vat_rate is distinct from old.vat_rate or new.vat_amount is distinct from old.vat_amount or new.tax_point_date is distinct from old.tax_point_date) then raise exception 'VAT snapshots are immutable' using errcode='23514';end if;
          if TG_OP<>'INSERT' or new.billable_event_id is null then return new;end if;
          select invoice_date,coalesce(settings_snapshot->>'vatRoundingPolicy','Early') into invoice_day,policy from finance_invoice_details where invoice_id=new.invoice_id and organization_id=new.organization_id;
          select count(*) into matches from finance_resolve_vat(new.billable_event_id,invoice_day);if matches<>1 then raise exception 'Exactly one approved VAT treatment must cover this supply' using errcode='23514';end if;
          select * into resolved from finance_resolve_vat(new.billable_event_id,invoice_day);
          new.vat_treatment_id:=resolved.treatment_id;new.vat_treatment:=resolved.treatment;new.vat_rate:=resolved.rate;new.tax_point_date:=resolved.tax_day;
          if policy<>'Late' or new.vat_amount is null then new.vat_amount:=finance_policy_round(new.amount*resolved.rate/100,2,resolved.rounding_mode);end if;return new;end $$;

        create or replace function govern_recharge_invoice_line() returns trigger language plpgsql as $$declare resolved record;matches integer;invoice_day date;policy text;begin
          if new.recharge_id is null or TG_OP<>'INSERT' then return new;end if;
          if new.billable_event_id is not null or new.event_share_id is not null or new.visit_id is not null then raise exception 'Expense recharge is a separate source, not another attendance charge' using errcode='23514';end if;
          perform 1 from finance_expense_recharges r join "Invoices" i on i."Id"=new.invoice_id join finance_invoice_details d on d.invoice_id=i."Id" and d.organization_id=i."OrganizationId" where r.id=new.recharge_id and r.organization_id=new.organization_id and r.branch_id=new.branch_id and r.status='Approved' and r.service_user_id=new.service_user_id and r.net_amount=new.amount and new.quantity=1 and new.unit_rate=r.net_amount and i."OrganizationId"=r.organization_id and i."BranchId"=r.branch_id and d.currency=r.currency and d.payer_id=r.funder_id for update of r;
          if not found then raise exception 'Invoice must match the approved expense recharge' using errcode='23514';end if;
          select invoice_date,coalesce(settings_snapshot->>'vatRoundingPolicy','Early') into invoice_day,policy from finance_invoice_details where invoice_id=new.invoice_id;
          select count(*) into matches from finance_resolve_recharge_vat(new.recharge_id,invoice_day);if matches<>1 then raise exception 'Exactly one adviser-approved expense VAT treatment required' using errcode='23514';end if;
          select * into resolved from finance_resolve_recharge_vat(new.recharge_id,invoice_day);
          new.vat_treatment_id:=resolved.treatment_id;new.vat_treatment:=resolved.treatment;new.vat_rate:=resolved.rate;new.tax_point_date:=resolved.tax_day;
          if policy<>'Late' or new.vat_amount is null then new.vat_amount:=finance_policy_round(new.amount*resolved.rate/100,2,resolved.rounding_mode);end if;return new;end $$;

        create or replace function verify_invoice_vat_totals() returns trigger language plpgsql as $$declare details finance_invoice_details%rowtype;line_net numeric;line_tax numeric;expected_tax numeric;begin
          if new."Status" in ('Reviewed','Approved','Issued') and exists(select 1 from finance_invoice_lines where invoice_id=new."Id" and (billable_event_id is not null or recharge_id is not null)) then
            if exists(select 1 from finance_invoice_lines where invoice_id=new."Id" and (billable_event_id is not null or recharge_id is not null) and vat_treatment_id is null) then raise exception 'Legacy unissued supplies require reviewed VAT configuration and controlled regeneration' using errcode='23514';end if;
            select * into details from finance_invoice_details where invoice_id=new."Id" and organization_id=new."OrganizationId";
            select sum(amount),sum(vat_amount) into line_net,line_tax from finance_invoice_lines where invoice_id=new."Id";
            if coalesce(details.settings_snapshot->>'vatRoundingPolicy','Early')='Late' then
              select coalesce(sum(finance_policy_round(group_net*rate/100,2,rounding_mode)),0) into expected_tax from (
                select sum(l.amount) group_net,l.vat_rate rate,t.rounding_mode from finance_invoice_lines l join finance_vat_treatments t on t.id=l.vat_treatment_id where l.invoice_id=new."Id" group by l.vat_treatment_id,l.vat_rate,t.rounding_mode
              ) grouped;
              if line_tax<>expected_tax then raise exception 'Late VAT total does not match grouped treatment rounding' using errcode='23514';end if;
            end if;
            if details.net_amount<>line_net or details.vat_amount<>line_tax or details.gross_amount<>line_net+line_tax or new."Amount"<>line_net+line_tax then raise exception 'Invoice totals must reconcile to immutable line VAT snapshots' using errcode='23514';end if;
          end if;return new;end $$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Preserve versioned invoice tax policy; recover forwards.");
}
