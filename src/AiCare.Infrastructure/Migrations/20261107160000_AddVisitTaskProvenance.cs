using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261107160000_AddVisitTaskProvenance")]
public sealed class AddVisitTaskProvenance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table visit_tasks add column source_type text not null default 'LegacyImported';
        alter table visit_tasks add column source_reference text not null default '';
        alter table visit_tasks add column source_created_by text not null default '';
        update visit_tasks set source_type='CarePlan',source_reference=care_plan_task_id::text,source_created_by='migration:20261107160000'
        where care_plan_task_id is not null;
        alter table visit_tasks add constraint ck_visit_tasks_source_type check(source_type in ('CarePlan','AuthorizedManual','LegacyImported'));
        alter table visit_tasks add constraint ck_visit_tasks_care_plan_source check(
          (care_plan_task_id is not null and source_type in ('CarePlan','LegacyImported')) or
          (care_plan_task_id is null and source_type in ('AuthorizedManual','LegacyImported')));
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table visit_tasks drop constraint ck_visit_tasks_care_plan_source;
        alter table visit_tasks drop constraint ck_visit_tasks_source_type;
        alter table visit_tasks drop column source_created_by;
        alter table visit_tasks drop column source_reference;
        alter table visit_tasks drop column source_type;
        """);
}
