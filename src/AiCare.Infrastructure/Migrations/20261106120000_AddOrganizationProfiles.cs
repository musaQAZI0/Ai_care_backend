using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261106120000_AddOrganizationProfiles")]
public sealed class AddOrganizationProfiles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table organization_profiles (
          organization_id uuid primary key references "Organizations"("Id"),
          legal_name text not null check(length(trim(legal_name)) between 1 and 200),
          display_name text not null check(length(trim(display_name)) between 1 and 200),
          address text not null default '' check(length(address) <= 1000),
          business_phone text not null default '' check(length(business_phone) <= 80),
          contact_email text not null default '' check(length(contact_email) <= 254),
          logo_content_type text null check(logo_content_type in ('image/png','image/jpeg')),
          logo_bytes bytea null check(logo_bytes is null or octet_length(logo_bytes) <= 1048576),
          logo_sha256 text null,
          updated_by text not null,
          updated_at timestamptz not null default now(),
          check((logo_content_type is null and logo_bytes is null and logo_sha256 is null)
             or (logo_content_type is not null and logo_bytes is not null and logo_sha256 is not null))
        );
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("organization_profiles");
}
