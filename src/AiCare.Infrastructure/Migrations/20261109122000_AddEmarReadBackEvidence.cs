using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261109122000_AddEmarReadBackEvidence")]
public sealed class AddEmarReadBackEvidence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("alter table emar_ledger add column read_back_evidence jsonb null;");
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Preserve administration evidence; recover forwards.");
}
