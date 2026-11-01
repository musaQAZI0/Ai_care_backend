using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261025130000_AddStructuredMedicationDoseTimes")]
public sealed class AddStructuredMedicationDoseTimes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table medication_safety_profiles
          add column scheduled_local_times jsonb not null default '[]'::jsonb;
        alter table medication_safety_profiles
          add constraint ck_medication_scheduled_local_times_array
          check (jsonb_typeof(scheduled_local_times) = 'array');
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table medication_safety_profiles drop constraint if exists ck_medication_scheduled_local_times_array;
        alter table medication_safety_profiles drop column if exists scheduled_local_times;
        """);
}
