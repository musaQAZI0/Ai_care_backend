using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260827133000_AddAdvancedSchedulingSafety")]
public sealed class AddAdvancedSchedulingSafety : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table if not exists worker_absences (
            id uuid primary key,
            care_worker_id uuid not null references "CareWorkers"("Id") on delete cascade,
            organization_id uuid not null,
            branch_id uuid not null,
            absence_type text not null,
            starts_at timestamptz not null,
            ends_at timestamptz not null,
            status text not null default 'Approved',
            notes text not null default '',
            created_at timestamptz not null default now(),
            constraint ck_worker_absence_range check (ends_at > starts_at)
        );
        create index if not exists ix_worker_absences_worker_range on worker_absences(organization_id,care_worker_id,starts_at,ends_at);

        create table if not exists scheduling_policies (
            organization_id uuid not null,
            branch_id uuid not null,
            minimum_rest_minutes integer not null default 660,
            maximum_daily_minutes integer not null default 720,
            maximum_weekly_minutes integer not null default 2880,
            travel_buffer_minutes integer not null default 15,
            updated_at timestamptz not null default now(),
            primary key (organization_id,branch_id),
            constraint ck_scheduling_policy_values check (minimum_rest_minutes >= 0 and maximum_daily_minutes > 0 and maximum_weekly_minutes > 0 and travel_buffer_minutes >= 0)
        );

        create table if not exists visit_care_worker_assignments (
            visit_id uuid not null references "Visits"("Id") on delete cascade,
            care_worker_id uuid not null references "CareWorkers"("Id") on delete restrict,
            organization_id uuid not null,
            branch_id uuid not null,
            assignment_role text not null default 'Additional',
            created_at timestamptz not null default now(),
            primary key (visit_id,care_worker_id)
        );
        create index if not exists ix_visit_worker_assignments_worker on visit_care_worker_assignments(organization_id,care_worker_id,visit_id);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop table if exists visit_care_worker_assignments;
        drop table if exists scheduling_policies;
        drop table if exists worker_absences;
        """);
}
