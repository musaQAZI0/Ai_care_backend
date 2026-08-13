using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CompletePersonRecordAssessmentsCarePlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CareAssessments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessmentType = table.Column<string>(type: "text", nullable: false),
                    TemplateVersion = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    AnswersJson = table.Column<string>(type: "jsonb", nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: false),
                    Risk = table.Column<string>(type: "text", nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: false),
                    RecommendedActions = table.Column<string>(type: "text", nullable: false),
                    CompletedBy = table.Column<string>(type: "text", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReviewDueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CareAssessments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CarePlanOutcomes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CarePlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Goal = table.Column<string>(type: "text", nullable: false),
                    DesiredOutcome = table.Column<string>(type: "text", nullable: false),
                    Interventions = table.Column<string>(type: "text", nullable: false),
                    ResponsiblePerson = table.Column<string>(type: "text", nullable: false),
                    Measure = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    TargetDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CarePlanOutcomes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PersonRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreferredName = table.Column<string>(type: "text", nullable: false),
                    Pronouns = table.Column<string>(type: "text", nullable: false),
                    HealthIdentifier = table.Column<string>(type: "text", nullable: false),
                    GpDetails = table.Column<string>(type: "text", nullable: false),
                    PharmacyDetails = table.Column<string>(type: "text", nullable: false),
                    LegalRepresentative = table.Column<string>(type: "text", nullable: false),
                    ConsentStatus = table.Column<string>(type: "text", nullable: false),
                    MentalCapacityStatus = table.Column<string>(type: "text", nullable: false),
                    CommunicationPassport = table.Column<string>(type: "text", nullable: false),
                    PersonalHistory = table.Column<string>(type: "text", nullable: false),
                    WhatMattersToMe = table.Column<string>(type: "text", nullable: true),
                    DesiredOutcomes = table.Column<string>(type: "text", nullable: true),
                    AdvanceCareWishes = table.Column<string>(type: "text", nullable: false),
                    AdmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DischargedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CareAssessments_OrganizationId_BranchId_ServiceUserId",
                table: "CareAssessments",
                columns: new[] { "OrganizationId", "BranchId", "ServiceUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_CarePlanOutcomes_OrganizationId_BranchId_ServiceUserId",
                table: "CarePlanOutcomes",
                columns: new[] { "OrganizationId", "BranchId", "ServiceUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_PersonRecords_OrganizationId_BranchId_ServiceUserId",
                table: "PersonRecords",
                columns: new[] { "OrganizationId", "BranchId", "ServiceUserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CareAssessments");

            migrationBuilder.DropTable(
                name: "CarePlanOutcomes");

            migrationBuilder.DropTable(
                name: "PersonRecords");

        }
    }
}
