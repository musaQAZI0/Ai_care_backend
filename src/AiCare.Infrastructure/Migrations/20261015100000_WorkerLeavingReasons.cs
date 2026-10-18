using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261015100000_WorkerLeavingReasons")]
public sealed class WorkerLeavingReasons : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table care_operation_options drop constraint ck_care_operation_option_kind;
        alter table care_operation_options add constraint ck_care_operation_option_kind
          check(kind in ('DiaryCategory','CareProvided','DurationReason','CancellationReason','JobTitle','WorkerLeavingReason'));
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        delete from care_operation_options where kind='WorkerLeavingReason';
        alter table care_operation_options drop constraint ck_care_operation_option_kind;
        alter table care_operation_options add constraint ck_care_operation_option_kind
          check(kind in ('DiaryCategory','CareProvided','DurationReason','CancellationReason','JobTitle'));
        """);
}
