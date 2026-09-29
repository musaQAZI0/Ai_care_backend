using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260924100000_AddFinanceDomainFoundation")]
public sealed class AddFinanceDomainFoundation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            create table finance_funders (
                id uuid primary key,
                organization_id uuid not null,
                branch_id uuid null,
                name text not null,
                normalized_name text not null,
                funder_type text not null,
                billing_address text not null default '',
                billing_email text not null default '',
                billing_phone text not null default '',
                payment_terms_days integer not null default 30 check(payment_terms_days between 0 and 365),
                default_invoice_frequency text not null default 'Monthly',
                currency char(3) not null default 'GBP',
                external_reference text not null default '',
                notes text not null default '',
                active boolean not null default true,
                created_at timestamptz not null default now(),
                created_by text not null,
                updated_at timestamptz not null default now(),
                updated_by text not null,
                check(funder_type in ('LocalAuthority','Private','Family','NHS','Organisation','Other')),
                check(default_invoice_frequency in ('Weekly','FourWeekly','Monthly','AdHoc')),
                check(currency ~ '^[A-Z]{3}$')
            );
            create unique index ux_finance_funders_org_name_active on finance_funders(organization_id, normalized_name) where active;
            create index ix_finance_funders_scope on finance_funders(organization_id, branch_id, active, name);

            create table finance_rate_cards (
                id uuid primary key,
                organization_id uuid not null,
                branch_id uuid null,
                name text not null,
                service_type text not null,
                currency char(3) not null default 'GBP',
                active boolean not null default true,
                created_at timestamptz not null default now(),
                created_by text not null,
                updated_at timestamptz not null default now(),
                updated_by text not null,
                check(currency ~ '^[A-Z]{3}$')
            );
            create unique index ux_finance_rate_cards_org_name on finance_rate_cards(organization_id, lower(name));
            create index ix_finance_rate_cards_scope on finance_rate_cards(organization_id, branch_id, active);

            create table finance_rate_versions (
                id uuid primary key,
                rate_card_id uuid not null references finance_rate_cards(id),
                organization_id uuid not null,
                branch_id uuid null,
                version integer not null check(version > 0),
                effective_from date not null,
                effective_to date null,
                status text not null default 'Draft',
                created_at timestamptz not null default now(),
                created_by text not null,
                check(effective_to is null or effective_to >= effective_from),
                check(status in ('Draft','Active','Retired')),
                unique(rate_card_id, version)
            );
            create index ix_finance_rate_versions_effective on finance_rate_versions(organization_id, rate_card_id, effective_from, effective_to);

            create table finance_rate_rules (
                id uuid primary key,
                rate_version_id uuid not null references finance_rate_versions(id) on delete cascade,
                organization_id uuid not null,
                rule_type text not null,
                day_type text not null default 'Any',
                time_from time null,
                time_to time null,
                unit text not null,
                unit_rate numeric(12,4) not null check(unit_rate >= 0),
                minimum_quantity numeric(10,2) not null default 0 check(minimum_quantity >= 0),
                priority integer not null default 0,
                created_at timestamptz not null default now(),
                created_by text not null,
                check(rule_type in ('Standard','Weekend','BankHoliday','Night','Cancellation','Travel','Mileage')),
                check(day_type in ('Any','Weekday','Weekend','BankHoliday')),
                check(unit in ('Hour','Visit','Mile','Item'))
            );
            create index ix_finance_rate_rules_version on finance_rate_rules(organization_id, rate_version_id, priority desc);

            alter table funding_arrangements add column if not exists funder_id uuid null references finance_funders(id);
            alter table funding_arrangements add column if not exists rate_card_id uuid null references finance_rate_cards(id);
            alter table funding_arrangements add column if not exists invoice_frequency text not null default 'Monthly';
            alter table funding_arrangements add column if not exists allocation_rule text not null default 'Primary';
            alter table funding_arrangements add column if not exists purchase_order_reference text not null default '';
            alter table funding_arrangements add column if not exists created_by text not null default 'migration';
            create index ix_funding_arrangements_funder on funding_arrangements(organization_id, funder_id, valid_from, valid_to);
            create index ix_funding_arrangements_effective on funding_arrangements(organization_id, service_user_id, valid_from, valid_to, status);
        """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            alter table funding_arrangements drop column if exists created_by;
            alter table funding_arrangements drop column if exists purchase_order_reference;
            alter table funding_arrangements drop column if exists allocation_rule;
            alter table funding_arrangements drop column if exists invoice_frequency;
            alter table funding_arrangements drop column if exists rate_card_id;
            alter table funding_arrangements drop column if exists funder_id;
            drop table if exists finance_rate_rules;
            drop table if exists finance_rate_versions;
            drop table if exists finance_rate_cards;
            drop table if exists finance_funders;
        """);
    }
}
