using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(CareDbContext))]
    [Migration("20260808000100_AddTenantIndexes")]
    public partial class AddTenantIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex("IX_AppUsers_OrganizationId", "AppUsers", "OrganizationId");
            migrationBuilder.CreateIndex("IX_AppUsers_OrganizationId_BranchId", "AppUsers", new[] { "OrganizationId", "BranchId" });
            migrationBuilder.CreateIndex("IX_CareNotes_OrganizationId_BranchId", "CareNotes", new[] { "OrganizationId", "BranchId" });
            migrationBuilder.CreateIndex("IX_CareNotes_OrganizationId_ServiceUserId", "CareNotes", new[] { "OrganizationId", "ServiceUserId" });
            migrationBuilder.CreateIndex("IX_CarePlans_OrganizationId_BranchId", "CarePlans", new[] { "OrganizationId", "BranchId" });
            migrationBuilder.CreateIndex("IX_CareWorkers_OrganizationId", "CareWorkers", "OrganizationId");
            migrationBuilder.CreateIndex("IX_CareWorkers_OrganizationId_BranchId", "CareWorkers", new[] { "OrganizationId", "BranchId" });
            migrationBuilder.CreateIndex("IX_Documents_OrganizationId_BranchId", "Documents", new[] { "OrganizationId", "BranchId" });
            migrationBuilder.CreateIndex("IX_Incidents_OrganizationId_BranchId", "Incidents", new[] { "OrganizationId", "BranchId" });
            migrationBuilder.CreateIndex("IX_RiskAssessments_OrganizationId_BranchId", "RiskAssessments", new[] { "OrganizationId", "BranchId" });
            migrationBuilder.CreateIndex("IX_ServiceUsers_OrganizationId", "ServiceUsers", "OrganizationId");
            migrationBuilder.CreateIndex("IX_ServiceUsers_OrganizationId_BranchId", "ServiceUsers", new[] { "OrganizationId", "BranchId" });
            migrationBuilder.CreateIndex("IX_Visits_OrganizationId", "Visits", "OrganizationId");
            migrationBuilder.CreateIndex("IX_Visits_OrganizationId_BranchId", "Visits", new[] { "OrganizationId", "BranchId" });
            migrationBuilder.CreateIndex("IX_Visits_OrganizationId_ServiceUserId", "Visits", new[] { "OrganizationId", "ServiceUserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex("IX_AppUsers_OrganizationId", "AppUsers");
            migrationBuilder.DropIndex("IX_AppUsers_OrganizationId_BranchId", "AppUsers");
            migrationBuilder.DropIndex("IX_CareNotes_OrganizationId_BranchId", "CareNotes");
            migrationBuilder.DropIndex("IX_CareNotes_OrganizationId_ServiceUserId", "CareNotes");
            migrationBuilder.DropIndex("IX_CarePlans_OrganizationId_BranchId", "CarePlans");
            migrationBuilder.DropIndex("IX_CareWorkers_OrganizationId", "CareWorkers");
            migrationBuilder.DropIndex("IX_CareWorkers_OrganizationId_BranchId", "CareWorkers");
            migrationBuilder.DropIndex("IX_Documents_OrganizationId_BranchId", "Documents");
            migrationBuilder.DropIndex("IX_Incidents_OrganizationId_BranchId", "Incidents");
            migrationBuilder.DropIndex("IX_RiskAssessments_OrganizationId_BranchId", "RiskAssessments");
            migrationBuilder.DropIndex("IX_ServiceUsers_OrganizationId", "ServiceUsers");
            migrationBuilder.DropIndex("IX_ServiceUsers_OrganizationId_BranchId", "ServiceUsers");
            migrationBuilder.DropIndex("IX_Visits_OrganizationId", "Visits");
            migrationBuilder.DropIndex("IX_Visits_OrganizationId_BranchId", "Visits");
            migrationBuilder.DropIndex("IX_Visits_OrganizationId_ServiceUserId", "Visits");
        }
    }
}
