using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261023120000_UnassignedVisits")]
public sealed class UnassignedVisits : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table unassigned_visits (
          id uuid primary key,
          service_user_id uuid not null references "ServiceUsers"("Id"),
          organization_id uuid not null references "Organizations"("Id") on delete cascade,
          branch_id uuid not null references "Branches"("Id"),
          starts_at timestamptz not null,
          visit_type text not null check (length(trim(visit_type)) between 1 and 200),
          duration_minutes integer not null check (duration_minutes between 1 and 1440),
          required_skills text not null default '',
          status text not null default 'Open' check (status in ('Open','Assigned','Cancelled')),
          assigned_visit_id uuid null references "Visits"("Id"),
          reason text not null default '',
          created_by text not null,
          created_at timestamptz not null default now(),
          resolved_by text null,
          resolved_at timestamptz null
        );
        create index ix_unassigned_visits_queue on unassigned_visits(organization_id,branch_id,status,starts_at);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("drop table if exists unassigned_visits;");
}
