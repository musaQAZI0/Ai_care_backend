using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261013100000_TrainingCourseJobTitles")]
public sealed class TrainingCourseJobTitles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table training_catalogue_courses
          add column if not exists required_job_titles text[] not null default '{}';
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table training_catalogue_courses drop column if exists required_job_titles;
        """);
}
