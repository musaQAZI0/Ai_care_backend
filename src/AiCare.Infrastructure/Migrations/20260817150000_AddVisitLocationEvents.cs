using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260817150000_AddVisitLocationEvents")]
public sealed class AddVisitLocationEvents : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            create table if not exists visit_location_events (
                id uuid primary key,
                visit_id uuid not null,
                service_user_id uuid not null,
                care_worker_id uuid not null,
                organization_id uuid not null,
                branch_id uuid not null,
                event_type text not null,
                latitude numeric(10,7) not null,
                longitude numeric(10,7) not null,
                accuracy_meters numeric(10,2) null,
                captured_at timestamptz not null,
                source text not null default 'Browser geolocation',
                notes text not null default '',
                created_at timestamptz not null default now(),
                constraint fk_visit_location_visit foreign key (visit_id) references "Visits"("Id") on delete cascade
            );
            create index if not exists ix_visit_location_visit on visit_location_events(organization_id, branch_id, visit_id, captured_at);
        """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("drop table if exists visit_location_events;");
    }
}
