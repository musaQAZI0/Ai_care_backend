using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261104120000_AddPersonRecordRevisions")]
public sealed class AddPersonRecordRevisions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PersonRecordRevisions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ServiceUserId = table.Column<Guid>(type: "uuid", nullable: false),
                PersonRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                Actor = table.Column<string>(type: "text", nullable: false),
                RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                BeforeJson = table.Column<string>(type: "jsonb", nullable: true),
                AfterJson = table.Column<string>(type: "jsonb", nullable: false),
                OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                BranchId = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_PersonRecordRevisions", item => item.Id));
        migrationBuilder.CreateIndex(
            name: "IX_PersonRecordRevisions_OrganizationId_ServiceUserId_RecordedAt",
            table: "PersonRecordRevisions", columns: new[] { "OrganizationId", "ServiceUserId", "RecordedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("PersonRecordRevisions");
}
