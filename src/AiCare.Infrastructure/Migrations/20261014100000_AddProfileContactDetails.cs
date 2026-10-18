using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261014100000_AddProfileContactDetails")]
public sealed class AddProfileContactDetails : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table if not exists "ProfileContactDetails" (
          "Id" uuid primary key,
          "SubjectType" text not null,
          "SubjectId" uuid not null,
          "OrganizationId" uuid not null references "Organizations"("Id") on delete restrict,
          "BranchId" uuid not null references "Branches"("Id") on delete restrict,
          "Email" varchar(254) not null default '',
          "MobilePhone" varchar(40) not null default '',
          "Address" varchar(500) not null default '',
          "Postcode" varchar(16) not null default '',
          "EmergencyContactName" varchar(200) not null default '',
          "EmergencyContactRelationship" varchar(100) not null default '',
          "EmergencyContactPhone" varchar(40) not null default '',
          "PreferredContactMethod" varchar(20) not null default '',
          constraint "CK_ProfileContactDetails_SubjectType" check ("SubjectType" in ('CareWorker','ServiceUser')),
          constraint "CK_ProfileContactDetails_ContactMethod" check ("PreferredContactMethod" in ('','Phone','Email','Post'))
        );
        create unique index if not exists "IX_ProfileContactDetails_OrganizationId_SubjectType_SubjectId"
          on "ProfileContactDetails"("OrganizationId","SubjectType","SubjectId");
        create index if not exists "IX_ProfileContactDetails_OrganizationId_BranchId"
          on "ProfileContactDetails"("OrganizationId","BranchId");
        create unique index if not exists "IX_ProfileContactDetails_OrganizationId_Email"
          on "ProfileContactDetails"("OrganizationId", lower("Email")) where "Email" <> '';
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("drop table if exists \"ProfileContactDetails\";");
}
