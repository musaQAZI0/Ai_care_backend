using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20261107120000_AddMobileBrowserSettings")]
public sealed class AddMobileBrowserSettings : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table mobile_browser_settings (
          organization_id uuid primary key references "Organizations"("Id"),
          visit_alerts_enabled boolean not null default true,
          alert_lead_minutes integer not null default 30 check(alert_lead_minutes between 0 and 240),
          show_completed_visits boolean not null default true,
          show_visit_type boolean not null default true,
          updated_by text not null,updated_at timestamptz not null default now()
        );
        """);
    protected override void Down(MigrationBuilder m) => m.DropTable("mobile_browser_settings");
}
