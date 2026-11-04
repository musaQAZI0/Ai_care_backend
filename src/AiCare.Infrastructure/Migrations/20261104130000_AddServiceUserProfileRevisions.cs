using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261104130000_AddServiceUserProfileRevisions")]
public sealed class AddServiceUserProfileRevisions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("ServiceUserProfileRevisions", table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            ServiceUserId = table.Column<Guid>(type: "uuid", nullable: false),
            Actor = table.Column<string>(type: "text", nullable: false),
            RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            BeforeJson = table.Column<string>(type: "jsonb", nullable: true),
            AfterJson = table.Column<string>(type: "jsonb", nullable: false),
            OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
            BranchId = table.Column<Guid>(type: "uuid", nullable: true)
        }, constraints: table => table.PrimaryKey("PK_ServiceUserProfileRevisions", item => item.Id));
        migrationBuilder.CreateIndex("IX_ServiceUserProfileRevisions_PersonTime",
            "ServiceUserProfileRevisions", new[] { "OrganizationId", "ServiceUserId", "RecordedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("ServiceUserProfileRevisions");
}
