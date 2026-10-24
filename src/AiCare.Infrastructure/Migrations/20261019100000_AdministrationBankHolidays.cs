using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261019100000_AdministrationBankHolidays")]
public sealed class AdministrationBankHolidays : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table admin_bank_holidays (
            id uuid primary key,
            organization_id uuid not null references "Organizations"("Id") on delete cascade,
            branch_id uuid null references "Branches"("Id") on delete cascade,
            holiday_date date not null,
            name text not null check (length(trim(name)) between 1 and 120),
            created_by text not null,
            created_at timestamptz not null default now(),
            updated_by text not null,
            updated_at timestamptz not null default now()
        );
        create unique index ux_admin_bank_holidays_scope_date
          on admin_bank_holidays(organization_id, branch_id, holiday_date) nulls not distinct;
        create index ix_admin_bank_holidays_lookup
          on admin_bank_holidays(organization_id, holiday_date, branch_id);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop table if exists admin_bank_holidays;
        """);
}
