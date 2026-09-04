using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260830193000_AddSchedulingBreakRules")]
public sealed class AddSchedulingBreakRules : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table scheduling_policies add column if not exists maximum_continuous_minutes integer not null default 360;
        alter table scheduling_policies add column if not exists required_break_minutes integer not null default 20;
        alter table scheduling_policies drop constraint if exists ck_scheduling_policy_break_values;
        alter table scheduling_policies add constraint ck_scheduling_policy_break_values
            check (maximum_continuous_minutes > 0 and required_break_minutes >= 0);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table scheduling_policies drop constraint if exists ck_scheduling_policy_break_values;
        alter table scheduling_policies drop column if exists required_break_minutes;
        alter table scheduling_policies drop column if exists maximum_continuous_minutes;
        """);
}
