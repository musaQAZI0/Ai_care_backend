using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCareWorkerUserLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CareWorkerId",
                table: "AppUsers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppUsers_CareWorkerId",
                table: "AppUsers",
                column: "CareWorkerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AppUsers_CareWorkerId",
                table: "AppUsers");

            migrationBuilder.DropColumn(
                name: "CareWorkerId",
                table: "AppUsers");
        }
    }
}
