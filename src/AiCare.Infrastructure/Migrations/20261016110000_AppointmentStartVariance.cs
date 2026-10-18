using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261016110000_AppointmentStartVariance")]
public sealed class AppointmentStartVariance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table appointment_start_variance_settings (
          organization_id uuid primary key references "Organizations"("Id") on delete cascade,
          max_change_minutes integer null check(max_change_minutes is null or max_change_minutes between 1 and 10080),
          updated_by text not null,
          updated_at timestamptz not null default now()
        );
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("drop table if exists appointment_start_variance_settings;");
}
