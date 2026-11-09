using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261109123000_AddMedicationAdministrationSafety")]
public sealed class AddMedicationAdministrationSafety : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table medication_safety_profiles add column administration_safety jsonb null;
        alter table emar_ledger add column administration_safety_snapshot jsonb null, add column administration_observations jsonb null;
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Preserve medication safety evidence; recover forwards.");
}
