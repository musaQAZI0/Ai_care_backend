using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SeedDesktopAdminUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "AppUsers",
                columns: new[] { "Id", "Email", "IsActive", "PasswordHash", "Role", "UserName" },
                values: new object[] { new Guid("99999999-9999-9999-9999-999999999999"), "admin@aicare.local", true, "pbkdf2$210000$16$QWlDYXJlU2VlZFNhbHQwMQ==$b+nMBoR9kN4AUCjzZxYCfMBhIRK5uSsdpEgykuK9AWs=", "Administrator", "admin" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "AppUsers",
                keyColumn: "Id",
                keyValue: new Guid("99999999-9999-9999-9999-999999999999"));
        }
    }
}
