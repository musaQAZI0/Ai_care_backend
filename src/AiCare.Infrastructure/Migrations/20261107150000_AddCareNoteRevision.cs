using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261107150000_AddCareNoteRevision")]
public sealed class AddCareNoteRevision : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "Revision",
            table: "CareNotes",
            type: "integer",
            nullable: false,
            defaultValue: 1);
        migrationBuilder.AddCheckConstraint(
            name: "CK_CareNotes_Revision",
            table: "CareNotes",
            sql: "\"Revision\" > 0");
        migrationBuilder.Sql("alter table care_note_reviews add column care_note_revision integer not null default 1 check(care_note_revision>0);");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("alter table care_note_reviews drop column care_note_revision;");
        migrationBuilder.DropCheckConstraint("CK_CareNotes_Revision", "CareNotes");
        migrationBuilder.DropColumn("Revision", "CareNotes");
    }
}
