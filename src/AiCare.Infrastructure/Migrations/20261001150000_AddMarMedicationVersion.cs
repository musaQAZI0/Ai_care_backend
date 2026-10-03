using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261001150000_AddMarMedicationVersion")]
public sealed class AddMarMedicationVersion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<int>(name:"MedicationProfileVersion",table:"MedicationAdministrationRecords",type:"integer",nullable:true);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(name:"MedicationProfileVersion",table:"MedicationAdministrationRecords");
}
