using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261008100000_VersionInvoiceProfiles")]
public sealed class VersionInvoiceProfiles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table finance_invoice_profile_versions (
            id uuid primary key,
            organization_id uuid not null,
            branch_id uuid not null,
            version int not null check (version > 0),
            effective_from timestamptz not null,
            profile_snapshot jsonb not null,
            changed_by text not null,
            unique (organization_id, branch_id, version)
        );
        create index ix_finance_invoice_profile_versions_effective
            on finance_invoice_profile_versions (organization_id, branch_id, effective_from desc);
        alter table finance_invoice_profiles
            add column logo_content_type text,
            add column logo_bytes bytea,
            add column logo_sha256 char(64),
            add constraint ck_finance_invoice_profile_logo
                check ((logo_bytes is null and logo_content_type is null and logo_sha256 is null)
                    or (logo_bytes is not null and logo_content_type in ('image/png','image/jpeg') and logo_sha256 is not null));
        insert into finance_invoice_profile_versions
            (id, organization_id, branch_id, version, effective_from, profile_snapshot, changed_by)
        select gen_random_uuid(), organization_id, branch_id, 1, updated_at,
            jsonb_build_object(
                'providerName', provider_name, 'providerAddress', provider_address,
                'providerEmail', provider_email, 'providerPhone', provider_phone,
                'companyNumber', company_number, 'vatNumber', vat_number,
                'remittanceDetails', remittance_details,
                'paymentTermsDays', default_payment_terms_days,
                'vatRate', default_vat_rate, 'vatExemptionReason', vat_exemption_reason),
            'migration'
        from finance_invoice_profiles;
        alter table finance_invoice_details
            add column settings_snapshot jsonb,
            add column calculation_snapshot jsonb,
            add column logo_content_type text,
            add column logo_bytes bytea,
            add column logo_sha256 char(64),
            add constraint ck_finance_invoice_detail_logo
                check ((logo_bytes is null and logo_content_type is null and logo_sha256 is null)
                    or (logo_bytes is not null and logo_content_type in ('image/png','image/jpeg') and logo_sha256 is not null));
        create function populate_invoice_snapshot() returns trigger language plpgsql as $$
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
        create trigger invoice_snapshot_on_insert before insert on finance_invoice_details
            for each row execute function populate_invoice_snapshot();
        update finance_invoice_details
        set settings_snapshot = jsonb_build_object(
                'providerName', provider_name, 'providerAddress', provider_address,
                'providerEmail', provider_email, 'providerPhone', provider_phone,
                'companyNumber', company_number, 'vatNumber', vat_number,
                'remittanceDetails', remittance_details, 'paymentTerms', payment_terms,
                'vatRate', vat_rate, 'vatExemptionReason', vat_exemption_reason,
                'currency', currency),
            calculation_snapshot = jsonb_build_object(
                'source', 'legacy', 'net', net_amount, 'vat', vat_amount,
                'gross', gross_amount);
        alter table finance_invoice_details
            alter column settings_snapshot set not null,
            alter column calculation_snapshot set not null;
        create function prevent_invoice_snapshot_change() returns trigger language plpgsql as $$
        begin
            if old is distinct from new then
                raise exception 'Invoice details are immutable; create a replacement document';
            end if;
            return new;
        end $$;
        create trigger invoice_snapshot_immutable before update on finance_invoice_details
            for each row execute function prevent_invoice_snapshot_change();
        create function prevent_issued_invoice_line_change() returns trigger language plpgsql as $$
        begin
            if exists (select 1 from "Invoices" where "Id" = old.invoice_id and "Status" <> 'Generated') then
                raise exception 'Issued invoice lines are immutable';
            end if;
            return old;
        end $$;
        create trigger issued_invoice_line_immutable before update or delete on finance_invoice_lines
            for each row execute function prevent_issued_invoice_line_change();
        create table finance_invoice_document_versions (
            id uuid primary key,
            invoice_id uuid not null references "Invoices"("Id"),
            organization_id uuid not null,
            branch_id uuid not null,
            version int not null check (version > 0),
            pdf_bytes bytea not null,
            created_at timestamptz not null default now(),
            created_by text not null,
            unique (invoice_id, version)
        );
        create function prevent_immutable_finance_record_change() returns trigger language plpgsql as $$
        begin
            raise exception 'Invoice profile versions and issued documents are append-only';
        end $$;
        create trigger invoice_profile_version_immutable before update or delete on finance_invoice_profile_versions
            for each row execute function prevent_immutable_finance_record_change();
        create trigger invoice_document_immutable before update or delete on finance_invoice_document_versions
            for each row execute function prevent_immutable_finance_record_change();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop trigger if exists invoice_profile_version_immutable on finance_invoice_profile_versions;
        drop trigger if exists invoice_document_immutable on finance_invoice_document_versions;
        drop table if exists finance_invoice_document_versions;
        drop function if exists prevent_immutable_finance_record_change();
        drop trigger if exists invoice_snapshot_immutable on finance_invoice_details;
        drop function if exists prevent_invoice_snapshot_change();
        drop trigger if exists issued_invoice_line_immutable on finance_invoice_lines;
        drop function if exists prevent_issued_invoice_line_change();
        drop trigger if exists invoice_snapshot_on_insert on finance_invoice_details;
        drop function if exists populate_invoice_snapshot();
        alter table finance_invoice_details
            drop constraint if exists ck_finance_invoice_detail_logo,
            drop column if exists logo_sha256,
            drop column if exists logo_bytes,
            drop column if exists logo_content_type,
            drop column if exists calculation_snapshot,
            drop column if exists settings_snapshot;
        alter table finance_invoice_profiles
            drop constraint if exists ck_finance_invoice_profile_logo,
            drop column if exists logo_sha256,
            drop column if exists logo_bytes,
            drop column if exists logo_content_type;
        drop table if exists finance_invoice_profile_versions;
        """);
}
