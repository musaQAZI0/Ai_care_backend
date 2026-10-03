using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261001160000_AddMarOrderSnapshot")]
public sealed class AddMarOrderSnapshot : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name:"MedicationOrderSnapshotJson",table:"MedicationAdministrationRecords",type:"text",nullable:true);
        migrationBuilder.Sql("""
            create or replace function prevent_mar_order_snapshot_change() returns trigger language plpgsql as $$
            begin
              if old."MedicationOrderSnapshotJson" is distinct from new."MedicationOrderSnapshotJson"
                or old."MedicationProfileVersion" is distinct from new."MedicationProfileVersion" then
                raise exception 'MAR order snapshot and profile version are immutable after scheduling';
              end if;
              return new;
            end $$;
            create trigger mar_order_snapshot_immutable before update on "MedicationAdministrationRecords"
              for each row execute function prevent_mar_order_snapshot_change();
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            drop trigger if exists mar_order_snapshot_immutable on "MedicationAdministrationRecords";
            drop function if exists prevent_mar_order_snapshot_change();
            """);
        migrationBuilder.DropColumn(name:"MedicationOrderSnapshotJson",table:"MedicationAdministrationRecords");
    }
}
