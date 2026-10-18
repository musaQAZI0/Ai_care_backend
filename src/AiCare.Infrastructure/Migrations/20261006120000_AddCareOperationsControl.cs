using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261006120000_AddCareOperationsControl")]
public sealed class AddCareOperationsControl : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table if not exists operational_diary_entries (
          id uuid primary key, organization_id uuid not null, branch_id uuid not null,
          service_user_id uuid null references "ServiceUsers"("Id") on delete restrict,
          care_worker_id uuid null references "CareWorkers"("Id") on delete restrict,
          category text not null, priority text not null, status text not null default 'Open',
          subject text not null, detail text not null, event_at timestamptz not null,
          created_by text not null, created_at timestamptz not null default now(),
          updated_by text not null default '', updated_at timestamptz null,
          constraint ck_diary_priority check(priority in ('Routine','Medium','High','Critical')),
          constraint ck_diary_status check(status in ('Open','InProgress','Resolved','Cancelled'))
        );
        create index if not exists ix_operational_diary_scope on operational_diary_entries(organization_id,branch_id,event_at desc,status);

        create table if not exists location_sharing_consents (
          id uuid primary key, organization_id uuid not null, branch_id uuid not null,
          subject_type text not null, subject_id uuid not null, sharing_mode text not null,
          purpose text not null, starts_at timestamptz not null, ends_at timestamptz null,
          active boolean not null default true, approved_by text not null, created_at timestamptz not null default now(),
          constraint ck_location_subject check(subject_type in ('CareWorker','ServiceUser')),
          constraint ck_location_mode check(sharing_mode in ('Exact','Approximate','Hidden'))
        );
        create unique index if not exists uq_location_consent_active on location_sharing_consents(organization_id,subject_type,subject_id) where active=true;

        create table if not exists visit_monitoring_outcomes (
          visit_id uuid primary key references "Visits"("Id") on delete cascade,
          organization_id uuid not null, branch_id uuid not null,
          care_provided text not null default '', duration_reason text not null default '',
          exception_reason text not null default '', recorded_by text not null,
          recorded_at timestamptz not null default now(), updated_at timestamptz null
        );
        create index if not exists ix_visit_monitoring_scope on visit_monitoring_outcomes(organization_id,branch_id,recorded_at desc);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop table if exists visit_monitoring_outcomes;
        drop table if exists location_sharing_consents;
        drop table if exists operational_diary_entries;
        """);
}
