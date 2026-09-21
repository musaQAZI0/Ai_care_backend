using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations;

public partial class AddMedicationDmdIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "DmdCode", table: "Medications", type: "character varying(64)", maxLength: 64, nullable: true);
        migrationBuilder.AddColumn<string>(name: "DmdDisplay", table: "Medications", type: "character varying(500)", maxLength: 500, nullable: true);
        migrationBuilder.AddColumn<string>(name: "DmdSystem", table: "Medications", type: "character varying(200)", maxLength: 200, nullable: true);
        migrationBuilder.CreateIndex(
            name: "IX_Medications_OrganizationId_BranchId_DmdCode",
            table: "Medications",
            columns: new[] { "OrganizationId", "BranchId", "DmdCode" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_Medications_OrganizationId_BranchId_DmdCode", table: "Medications");
        migrationBuilder.DropColumn(name: "DmdCode", table: "Medications");
        migrationBuilder.DropColumn(name: "DmdDisplay", table: "Medications");
        migrationBuilder.DropColumn(name: "DmdSystem", table: "Medications");
    }
}
