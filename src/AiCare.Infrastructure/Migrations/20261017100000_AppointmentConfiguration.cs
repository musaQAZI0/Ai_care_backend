using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261017100000_AppointmentConfiguration")]
public sealed class AppointmentConfiguration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table appointment_configuration (
          organization_id uuid not null references "Organizations"("Id") on delete cascade,
          branch_id uuid not null references "Branches"("Id") on delete cascade,
          configuration jsonb not null,
          updated_by text not null,
          updated_at timestamptz not null default now(),
          primary key(organization_id,branch_id)
        );
        create table appointment_presets (
          id uuid primary key, organization_id uuid not null, branch_id uuid not null,
          name text not null, visit_type text not null, duration_minutes integer not null check(duration_minutes between 1 and 1440),
          required_skills text not null default '', active boolean not null default true,
          updated_by text not null, updated_at timestamptz not null default now(),
          foreign key(organization_id,branch_id) references appointment_configuration(organization_id,branch_id)
        );
        create table appointment_runs (
          id uuid primary key, organization_id uuid not null, branch_id uuid not null,
          name text not null, active boolean not null default true, updated_by text not null, updated_at timestamptz not null default now(),
          foreign key(organization_id,branch_id) references appointment_configuration(organization_id,branch_id)
        );
        create table appointment_run_visits (
          visit_id uuid primary key references "Visits"("Id") on delete cascade,
          run_id uuid not null references appointment_runs(id), organization_id uuid not null, branch_id uuid not null
        );
        create table appointment_breaks (
          id uuid primary key, organization_id uuid not null, branch_id uuid not null,
          care_worker_id uuid not null references "CareWorkers"("Id"),
          starts_at timestamptz not null, ends_at timestamptz not null,
          note text not null default '', cancelled_at timestamptz null, created_by text not null,
          check(ends_at>starts_at)
        );
        create index ix_appointment_breaks_worker on appointment_breaks(organization_id,branch_id,care_worker_id,starts_at);
        create index ix_appointment_run_visits_run on appointment_run_visits(organization_id,branch_id,run_id);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop table if exists appointment_breaks;
        drop table if exists appointment_run_visits;
        drop table if exists appointment_runs;
        drop table if exists appointment_presets;
        drop table if exists appointment_configuration;
        """);
}
