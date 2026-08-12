using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantAndBranchModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "Visits",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("22222222-2222-2222-2222-222222222222"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "Visits",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("11111111-1111-1111-1111-111111111111"));

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "UatChecklist",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "UatChecklist",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "ServiceUsers",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("22222222-2222-2222-2222-222222222222"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "ServiceUsers",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("11111111-1111-1111-1111-111111111111"));

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "RiskAssessments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "RiskAssessments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "Reports",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "Reports",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "PayrollRuns",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "PayrollRuns",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "Notifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "Notifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "MessageThreads",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "MessageThreads",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "Medications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "Medications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "MedicationAdministrationRecords",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "MedicationAdministrationRecords",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "Invoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "Invoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "Incidents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "Incidents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "HealthObservations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "HealthObservations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "FamilyMembers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "FamilyMembers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "Documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "Documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "ComplianceItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "ComplianceItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "CareWorkers",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("22222222-2222-2222-2222-222222222222"));

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "CareWorkers",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("11111111-1111-1111-1111-111111111111"));

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "CarePlans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "CarePlans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "CareNotes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "CareNotes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "AuditEvents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "AuditEvents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "AppUsers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "AppUsers",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("11111111-1111-1111-1111-111111111111"));

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "AiRiskAlerts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "AiRiskAlerts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "AdminUsers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "AdminUsers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Branches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Region = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Branches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Organizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Plan = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Organizations", x => x.Id);
                });

            migrationBuilder.UpdateData(
                table: "AppUsers",
                keyColumn: "Id",
                keyValue: new Guid("99999999-9999-9999-9999-999999999999"),
                columns: new[] { "BranchId", "OrganizationId" },
                values: new object[] { null, new Guid("11111111-1111-1111-1111-111111111111") });

            migrationBuilder.InsertData(
                table: "Branches",
                columns: new[] { "Id", "Name", "OrganizationId", "Region", "Status" },
                values: new object[] { new Guid("22222222-2222-2222-2222-222222222222"), "Main Branch", new Guid("11111111-1111-1111-1111-111111111111"), "Primary", "Active" });

            migrationBuilder.InsertData(
                table: "Organizations",
                columns: new[] { "Id", "Name", "Plan", "Status" },
                values: new object[] { new Guid("11111111-1111-1111-1111-111111111111"), "AiCare Default Organization", "Enterprise", "Active" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Branches");

            migrationBuilder.DropTable(
                name: "Organizations");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "Visits");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "Visits");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "UatChecklist");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "UatChecklist");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "ServiceUsers");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "ServiceUsers");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "RiskAssessments");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "RiskAssessments");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "PayrollRuns");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "PayrollRuns");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "MessageThreads");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "MessageThreads");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "Medications");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "Medications");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "MedicationAdministrationRecords");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "MedicationAdministrationRecords");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "Incidents");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "Incidents");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "HealthObservations");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "HealthObservations");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "FamilyMembers");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "FamilyMembers");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "ComplianceItems");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "ComplianceItems");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "CareWorkers");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "CareWorkers");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "CarePlans");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "CarePlans");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "CareNotes");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "CareNotes");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "AppUsers");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "AppUsers");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "AiRiskAlerts");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "AiRiskAlerts");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "AdminUsers");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "AdminUsers");
        }
    }
}
