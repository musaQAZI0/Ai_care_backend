using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260920120000_HardenPrivacyGovernance")]
public sealed class HardenPrivacyGovernance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create unique index if not exists ux_privacy_request_record
            on privacy_request_records(request_id, record_type, record_id)
            where record_id is not null;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop index if exists ux_privacy_request_record;
        """);
}
