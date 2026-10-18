using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261009120000_AddOperationalReportPresets")]
public sealed class AddOperationalReportPresets : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table operational_report_presets (
            id uuid primary key,
            organization_id uuid not null,
            branch_id uuid null,
            owner_user_id uuid not null references "AppUsers"("Id") on delete cascade,
            name text not null,
            report_key text not null,
            from_date date null,
            to_date date null,
            worker_id uuid null,
            client_id uuid null,
            created_at timestamptz not null default now(),
            unique (organization_id, owner_user_id, name),
            check (length(trim(name)) between 1 and 100),
            check (from_date is null or to_date is null or to_date >= from_date)
        );
        create index ix_operational_report_presets_owner on operational_report_presets(organization_id, owner_user_id, created_at desc);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("drop table if exists operational_report_presets;");
}
