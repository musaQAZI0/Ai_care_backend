using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20261107140000_AddIncidentInvestigationRevision")]
public sealed class AddIncidentInvestigationRevision:Migration
{
 protected override void Up(MigrationBuilder m)=>m.Sql("alter table incident_investigations add column revision integer not null default 1 check(revision>0);");
 protected override void Down(MigrationBuilder m)=>m.Sql("alter table incident_investigations drop column revision;");
}
