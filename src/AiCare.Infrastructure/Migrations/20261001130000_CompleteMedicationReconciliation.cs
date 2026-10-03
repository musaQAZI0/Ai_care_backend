using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261001130000_CompleteMedicationReconciliation")]
public sealed class CompleteMedicationReconciliation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table medication_safety_profiles add column dose_unit text not null default '';
        alter table medication_safety_profiles add column frequency text not null default '';
        alter table medication_safety_profiles add column administration_instructions text not null default '';
        alter table medication_safety_profiles add column review_due_at timestamptz null;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table medication_safety_profiles drop column review_due_at;
        alter table medication_safety_profiles drop column administration_instructions;
        alter table medication_safety_profiles drop column frequency;
        alter table medication_safety_profiles drop column dose_unit;
        """);
}
