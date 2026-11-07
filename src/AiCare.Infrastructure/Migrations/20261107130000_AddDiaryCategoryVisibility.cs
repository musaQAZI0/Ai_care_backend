using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20261107130000_AddDiaryCategoryVisibility")]
public sealed class AddDiaryCategoryVisibility:Migration
{
 protected override void Up(MigrationBuilder m)=>m.Sql("""
  alter table care_operation_options add column visibility text not null default 'AllStaff';
  alter table care_operation_options add constraint ck_care_operation_option_visibility
    check(visibility in ('AllStaff','ManagersOnly') and (kind='DiaryCategory' or visibility='AllStaff'));
  """);
 protected override void Down(MigrationBuilder m)=>m.Sql("alter table care_operation_options drop column visibility;");
}
