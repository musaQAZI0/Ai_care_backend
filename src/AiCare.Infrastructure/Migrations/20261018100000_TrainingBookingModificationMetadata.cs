using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261018100000_TrainingBookingModificationMetadata")]
public sealed class TrainingBookingModificationMetadata : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table worker_training_bookings
          add column if not exists updated_by text not null default '',
          add column if not exists updated_at timestamptz null;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table worker_training_bookings
          drop column if exists updated_by,
          drop column if exists updated_at;
        """);
}
