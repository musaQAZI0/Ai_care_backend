using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261020110000_DashboardPreferences")]
public sealed class DashboardPreferences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table dashboard_preferences (
            organization_id uuid not null references "Organizations"("Id") on delete cascade,
            user_id uuid not null,
            widgets text not null,
            updated_at timestamptz not null default now(),
            primary key(organization_id, user_id)
        );
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("drop table if exists dashboard_preferences;");
}
