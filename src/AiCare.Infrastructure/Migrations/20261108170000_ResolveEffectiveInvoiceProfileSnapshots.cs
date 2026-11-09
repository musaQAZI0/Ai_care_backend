using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261108170000_ResolveEffectiveInvoiceProfileSnapshots")]
public sealed class ResolveEffectiveInvoiceProfileSnapshots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create or replace function populate_invoice_snapshot() returns trigger language plpgsql as $$
        begin
            if new.settings_snapshot is null then
                new.settings_snapshot := jsonb_build_object(
                    'providerName', new.provider_name, 'providerAddress', new.provider_address,
                    'providerEmail', new.provider_email, 'providerPhone', new.provider_phone,
                    'companyNumber', new.company_number, 'vatNumber', new.vat_number,
                    'remittanceDetails', new.remittance_details, 'paymentTerms', new.payment_terms,
                    'vatRate', new.vat_rate, 'vatExemptionReason', new.vat_exemption_reason,
                    'currency', new.currency, 'logoSha256', new.logo_sha256, 'profileVersion',
                    (select version from finance_invoice_profile_versions
                     where organization_id = new.organization_id and branch_id = new.branch_id
                       and effective_from <= now()
                     order by effective_from desc, version desc limit 1));
            end if;
            if new.calculation_snapshot is null then
                new.calculation_snapshot := jsonb_build_object(
                    'net', new.net_amount, 'vatRate', new.vat_rate,
                    'vat', new.vat_amount, 'gross', new.gross_amount,
                    'currency', new.currency);
            end if;
            return new;
        end $$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create or replace function populate_invoice_snapshot() returns trigger language plpgsql as $$
        begin
            if new.settings_snapshot is null then
                new.settings_snapshot := jsonb_build_object(
                    'providerName', new.provider_name, 'providerAddress', new.provider_address,
                    'providerEmail', new.provider_email, 'providerPhone', new.provider_phone,
                    'companyNumber', new.company_number, 'vatNumber', new.vat_number,
                    'remittanceDetails', new.remittance_details, 'paymentTerms', new.payment_terms,
                    'vatRate', new.vat_rate, 'vatExemptionReason', new.vat_exemption_reason,
                    'currency', new.currency, 'logoSha256', new.logo_sha256, 'profileVersion',
                    (select max(version) from finance_invoice_profile_versions
                     where organization_id = new.organization_id and branch_id = new.branch_id));
            end if;
            if new.calculation_snapshot is null then
                new.calculation_snapshot := jsonb_build_object(
                    'net', new.net_amount, 'vatRate', new.vat_rate,
                    'vat', new.vat_amount, 'gross', new.gross_amount,
                    'currency', new.currency);
            end if;
            return new;
        end $$;
        """);
}
