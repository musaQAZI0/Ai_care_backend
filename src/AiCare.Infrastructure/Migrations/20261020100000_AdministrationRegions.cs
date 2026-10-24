using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261020100000_AdministrationRegions")]
public sealed class AdministrationRegions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table admin_regions (
            id uuid primary key,
            organization_id uuid not null references "Organizations"("Id") on delete cascade,
            name text not null check (length(trim(name)) between 1 and 120),
            created_at timestamptz not null default now()
        );
        create unique index ux_admin_regions_name on admin_regions(organization_id, lower(name));
        insert into admin_regions(id, organization_id, name)
        select gen_random_uuid(), "OrganizationId", min("Region")
        from "Branches"
        group by "OrganizationId", lower("Region");
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("drop table if exists admin_regions;");
}
