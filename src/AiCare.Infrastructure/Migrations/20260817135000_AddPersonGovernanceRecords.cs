using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260817135000_AddPersonGovernanceRecords")]
public sealed class AddPersonGovernanceRecords : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            create table if not exists person_contacts (
                id uuid primary key,
                service_user_id uuid not null,
                organization_id uuid not null,
                branch_id uuid not null,
                contact_type text not null,
                full_name text not null,
                relationship text not null default '',
                phone_number text not null default '',
                email text not null default '',
                organization_name text not null default '',
                is_primary boolean not null default false,
                is_emergency boolean not null default false,
                created_at timestamptz not null default now(),
                updated_at timestamptz not null default now(),
                constraint fk_person_contacts_service_users foreign key (service_user_id) references "ServiceUsers"("Id") on delete cascade
            );
            create index if not exists ix_person_contacts_tenant_person on person_contacts(organization_id, branch_id, service_user_id);

            create table if not exists consent_records (
                id uuid primary key,
                service_user_id uuid not null,
                organization_id uuid not null,
                branch_id uuid not null,
                consent_type text not null,
                scope text not null,
                status text not null default 'Active',
                capacity_basis text not null default 'Not recorded',
                decision_maker text not null default '',
                evidence_reference text not null default '',
                effective_from timestamptz not null,
                expires_at timestamptz null,
                withdrawn_at timestamptz null,
                withdrawal_reason text not null default '',
                created_at timestamptz not null default now(),
                constraint fk_consent_records_service_users foreign key (service_user_id) references "ServiceUsers"("Id") on delete cascade
            );
            create index if not exists ix_consent_records_tenant_person on consent_records(organization_id, branch_id, service_user_id);
            create index if not exists ix_consent_records_status on consent_records(organization_id, status, expires_at);

            create table if not exists funding_arrangements (
                id uuid primary key,
                service_user_id uuid not null,
                organization_id uuid not null,
                branch_id uuid not null,
                funding_source text not null,
                funder_name text not null default '',
                contract_reference text not null default '',
                care_package_type text not null default '',
                authorized_hours_per_week numeric(10,2) not null default 0,
                hourly_rate numeric(12,2) not null default 0,
                valid_from timestamptz not null,
                valid_to timestamptz null,
                status text not null default 'Active',
                notes text not null default '',
                created_at timestamptz not null default now(),
                updated_at timestamptz not null default now(),
                constraint fk_funding_arrangements_service_users foreign key (service_user_id) references "ServiceUsers"("Id") on delete cascade
            );
            create index if not exists ix_funding_arrangements_tenant_person on funding_arrangements(organization_id, branch_id, service_user_id);
            create index if not exists ix_funding_arrangements_status on funding_arrangements(organization_id, status, valid_to);
        """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            drop table if exists funding_arrangements;
            drop table if exists consent_records;
            drop table if exists person_contacts;
        """);
    }
}
