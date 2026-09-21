using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20260918110000_AddComplaintAssignedStatus")]
public sealed class AddComplaintAssignedStatus : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE family_feedback_cases DROP CONSTRAINT family_feedback_cases_status_check;
        ALTER TABLE family_feedback_cases ADD CONSTRAINT family_feedback_cases_status_check
            CHECK (status IN ('Open','Submitted','Assigned','Acknowledged','InReview','Responded','Resolved','Closed'));
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM family_feedback_cases WHERE status IN ('Open','Assigned')) THEN
                RAISE EXCEPTION 'Cannot remove complaint statuses while Open or Assigned cases exist.';
            END IF;
        END $$;
        ALTER TABLE family_feedback_cases DROP CONSTRAINT family_feedback_cases_status_check;
        ALTER TABLE family_feedback_cases ADD CONSTRAINT family_feedback_cases_status_check
            CHECK (status IN ('Submitted','Acknowledged','InReview','Responded','Resolved','Closed'));
        """);
}
