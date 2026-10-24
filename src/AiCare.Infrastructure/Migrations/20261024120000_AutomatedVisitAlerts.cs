using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261024120000_AutomatedVisitAlerts")]
public sealed class AutomatedVisitAlerts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create unique index ux_visit_exception_automatic on visit_exceptions(visit_id,exception_type)
          where created_by='System monitoring';
        create unique index ux_visit_escalation_overdue on visit_escalation_events(visit_exception_id,action)
          where action='Overdue';
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop index if exists ux_visit_escalation_overdue;
        drop index if exists ux_visit_exception_automatic;
        """);
}
