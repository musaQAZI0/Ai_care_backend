using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260817141000_AddWorkforceComplianceRecords")]
public sealed class AddWorkforceComplianceRecords : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            create table if not exists worker_compliance_records (
                id uuid primary key,
                care_worker_id uuid not null,
                organization_id uuid not null,
                branch_id uuid not null,
                compliance_type text not null,
                reference text not null default '',
                status text not null default 'Pending',
                issued_at timestamptz null,
                expires_at timestamptz null,
                verified_by text not null default '',
                notes text not null default '',
                created_at timestamptz not null default now(),
                updated_at timestamptz not null default now(),
                constraint fk_worker_compliance_care_worker foreign key (care_worker_id) references \"CareWorkers\"(\"Id\") on delete cascade
            );
            create index if not exists ix_worker_compliance_worker on worker_compliance_records(organization_id, branch_id, care_worker_id);
            create index if not exists ix_worker_compliance_expiry on worker_compliance_records(organization_id, status, expires_at);

            create table if not exists worker_training_records (
                id uuid primary key,
                care_worker_id uuid not null,
                organization_id uuid not null,
                branch_id uuid not null,
                course_name text not null,
                category text not null default 'Mandatory',
                provider text not null default '',
                certificate_reference text not null default '',
                completed_at timestamptz not null,
                expires_at timestamptz null,
                status text not null default 'Valid',
                created_at timestamptz not null default now(),
                constraint fk_worker_training_care_worker foreign key (care_worker_id) references \"CareWorkers\"(\"Id\") on delete cascade
            );
            create index if not exists ix_worker_training_worker on worker_training_records(organization_id, branch_id, care_worker_id);
            create index if not exists ix_worker_training_expiry on worker_training_records(organization_id, status, expires_at);

            create table if not exists worker_competency_records (
                id uuid primary key,
                care_worker_id uuid not null,
                organization_id uuid not null,
                branch_id uuid not null,
                competency text not null,
                level text not null default 'Competent',
                status text not null default 'Valid',
                assessed_by text not null default '',
                assessed_at timestamptz not null,
                expires_at timestamptz null,
                notes text not null default '',
                created_at timestamptz not null default now(),
                constraint fk_worker_competency_care_worker foreign key (care_worker_id) references \"CareWorkers\"(\"Id\") on delete cascade
            );
            create index if not exists ix_worker_competency_worker on worker_competency_records(organization_id, branch_id, care_worker_id);

            create table if not exists worker_availability_rules (
                id uuid primary key,
                care_worker_id uuid not null,
                organization_id uuid not null,
                branch_id uuid not null,
                day_of_week integer not null,
                start_time time not null,
                end_time time not null,
                is_available boolean not null default true,
                effective_from date not null,
                effective_to date null,
                notes text not null default '',
                created_at timestamptz not null default now(),
                constraint ck_worker_availability_day check (day_of_week between 0 and 6),
                constraint ck_worker_availability_time check (end_time > start_time),
                constraint fk_worker_availability_care_worker foreign key (care_worker_id) references \"CareWorkers\"(\"Id\") on delete cascade
            );
            create index if not exists ix_worker_availability_worker on worker_availability_rules(organization_id, branch_id, care_worker_id, day_of_week);
        """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            drop table if exists worker_availability_rules;
            drop table if exists worker_competency_records;
            drop table if exists worker_training_records;
            drop table if exists worker_compliance_records;
        """);
    }
}
