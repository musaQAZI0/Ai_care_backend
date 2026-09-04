using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260902090000_BindGovernedReportExports")]
public sealed class BindGovernedReportExports : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            alter table report_runs
                add column if not exists report_catalogue_id uuid null;

            do $$ begin
                alter table report_runs
                    add constraint fk_report_runs_catalogue
                    foreign key (report_catalogue_id) references governed_report_catalogue(id) on delete restrict;
            exception when duplicate_object then null;
            end $$;

            create index if not exists ix_report_runs_catalogue
                on report_runs(organization_id, branch_id, report_catalogue_id);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            alter table report_runs drop constraint if exists fk_report_runs_catalogue;
            drop index if exists ix_report_runs_catalogue;
            alter table report_runs drop column if exists report_catalogue_id;
            """);
    }
}
