using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261009130000_AddOperationalReportAuditFilters")]
public sealed class AddOperationalReportAuditFilters : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table operational_report_presets add column actor_filter text not null default '';
        alter table operational_report_presets add column action_filter text not null default '';
        alter table operational_report_presets add column entity_type_filter text not null default '';
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table operational_report_presets drop column entity_type_filter;
        alter table operational_report_presets drop column action_filter;
        alter table operational_report_presets drop column actor_filter;
        """);
}
