using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261006130000_CompleteCareOperations")]
public sealed class CompleteCareOperations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
      create table if not exists subject_geocoded_locations (
        id uuid primary key, organization_id uuid not null, branch_id uuid not null,
        subject_type text not null, subject_id uuid not null, address_text text not null,
        postcode text not null default '', latitude numeric(9,6) not null, longitude numeric(9,6) not null,
        provider text not null, confidence numeric(5,4) null, geocoded_at timestamptz not null,
        verified_by text not null default '', verified_at timestamptz null,
        constraint ck_geocoded_subject check(subject_type in ('CareWorker','ServiceUser')),
        constraint ck_geocoded_lat check(latitude between -90 and 90),
        constraint ck_geocoded_lng check(longitude between -180 and 180),
        unique(organization_id,subject_type,subject_id)
      );
      create index if not exists ix_geocoded_location_scope on subject_geocoded_locations(organization_id,branch_id,subject_type);

      alter table operational_diary_entries add column if not exists owner_id uuid null;
      alter table operational_diary_entries add column if not exists due_at timestamptz null;
      create table if not exists operational_diary_recipients (
        diary_entry_id uuid not null references operational_diary_entries(id) on delete cascade,
        recipient_user_id uuid not null, organization_id uuid not null,
        acknowledged_at timestamptz null, primary key(diary_entry_id,recipient_user_id)
      );
      create table if not exists operational_diary_comments (
        id uuid primary key, diary_entry_id uuid not null references operational_diary_entries(id) on delete cascade,
        organization_id uuid not null, body text not null, created_by text not null, created_at timestamptz not null default now()
      );
      create table if not exists operational_diary_history (
        id uuid primary key, diary_entry_id uuid not null references operational_diary_entries(id) on delete cascade,
        organization_id uuid not null, action text not null, from_status text null, to_status text null,
        reason text not null default '', actor text not null, created_at timestamptz not null default now()
      );
      create index if not exists ix_diary_owner_due on operational_diary_entries(organization_id,branch_id,owner_id,due_at,status);
      create index if not exists ix_diary_comments_entry on operational_diary_comments(diary_entry_id,created_at);
      create index if not exists ix_diary_history_entry on operational_diary_history(diary_entry_id,created_at);

      alter table worker_training_bookings add column if not exists confirmation_status text not null default 'Pending';
      alter table worker_training_bookings add column if not exists confirmed_at timestamptz null;
      alter table worker_training_bookings add column if not exists attendance_notes text not null default '';
      create index if not exists ix_training_calendar_scope on worker_training_bookings(organization_id,branch_id,scheduled_at,status);
      create index if not exists ix_visits_operations_page on "Visits"("OrganizationId","BranchId","StartsAt","Id");
      create index if not exists ix_live_location_freshness on visit_location_events(organization_id,branch_id,care_worker_id,captured_at desc);
    """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
      drop table if exists operational_diary_history;
      drop table if exists operational_diary_comments;
      drop table if exists operational_diary_recipients;
      drop table if exists subject_geocoded_locations;
      alter table operational_diary_entries drop column if exists owner_id;
      alter table operational_diary_entries drop column if exists due_at;
      alter table worker_training_bookings drop column if exists confirmation_status;
      alter table worker_training_bookings drop column if exists confirmed_at;
      alter table worker_training_bookings drop column if exists attendance_notes;
    """);
}
