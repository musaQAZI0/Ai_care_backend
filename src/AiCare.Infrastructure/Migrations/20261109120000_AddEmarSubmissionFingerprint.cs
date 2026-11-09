using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261109120000_AddEmarSubmissionFingerprint")]
public sealed class AddEmarSubmissionFingerprint : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table emar_ledger add column submission_fingerprint text not null default '';
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Preserve medication submission evidence; recover forwards.");
}
