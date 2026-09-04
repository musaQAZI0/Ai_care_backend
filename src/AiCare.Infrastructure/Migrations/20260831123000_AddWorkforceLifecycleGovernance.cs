using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260831123000_AddWorkforceLifecycleGovernance")]
public sealed class AddWorkforceLifecycleGovernance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table if not exists worker_employment_profiles (
            care_worker_id uuid primary key references "CareWorkers"("Id") on delete cascade,
            organization_id uuid not null, branch_id uuid not null,
            recruitment_status text not null default 'Applicant', employment_status text not null default 'PreEmployment',
            contract_type text not null default '', job_title text not null default '', contracted_weekly_minutes integer not null default 0,
            start_date date null, end_date date null, line_manager text not null default '', status_reason text not null default '',
            approved_by text not null default '', approved_at timestamptz null, updated_at timestamptz not null default now(),
            constraint ck_worker_employment_minutes check (contracted_weekly_minutes >= 0),
            constraint ck_worker_employment_dates check (end_date is null or start_date is null or end_date >= start_date)
        );
        create index if not exists ix_worker_employment_scope on worker_employment_profiles(organization_id,branch_id,employment_status);

        create table if not exists worker_supervision_records (
            id uuid primary key, care_worker_id uuid not null references "CareWorkers"("Id") on delete cascade,
            organization_id uuid not null, branch_id uuid not null, record_type text not null,
            scheduled_at timestamptz not null, completed_at timestamptz null, supervisor text not null default '',
            discussion text not null default '', outcome text not null default '', actions text not null default '', evidence_reference text not null default '',
            review_due date null, status text not null default 'Scheduled', created_at timestamptz not null default now(),
            constraint ck_worker_supervision_type check (record_type in ('Supervision','Appraisal')),
            constraint ck_worker_supervision_status check (status in ('Scheduled','Completed','Cancelled'))
        );
        create index if not exists ix_worker_supervision_due on worker_supervision_records(organization_id,care_worker_id,status,review_due);

        alter table worker_absences add column if not exists cover_status text not null default 'Unassigned';
        alter table worker_absences add column if not exists covered_by uuid null references "CareWorkers"("Id") on delete set null;
        alter table worker_absences add column if not exists cover_notes text not null default '';

        create table if not exists worker_return_to_work_reviews (
            id uuid primary key, absence_id uuid not null unique references worker_absences(id) on delete restrict,
            care_worker_id uuid not null references "CareWorkers"("Id") on delete cascade,
            organization_id uuid not null, branch_id uuid not null, meeting_at timestamptz not null,
            fit_to_return boolean not null, adjustments text not null default '', restrictions text not null default '',
            evidence_reference text not null default '', review_due date null, manager text not null default '',
            status text not null default 'Completed', completed_at timestamptz not null default now()
        );
        create index if not exists ix_worker_rtw_worker on worker_return_to_work_reviews(organization_id,care_worker_id,completed_at desc);

        create table if not exists workforce_lifecycle_events (
            id uuid primary key, care_worker_id uuid not null references "CareWorkers"("Id") on delete cascade,
            organization_id uuid not null, branch_id uuid not null, event_type text not null, entity_type text not null,
            entity_id uuid null, actor text not null, detail text not null default '', occurred_at timestamptz not null default now()
        );
        create index if not exists ix_workforce_lifecycle_events_worker on workforce_lifecycle_events(organization_id,care_worker_id,occurred_at desc);
        create or replace function reject_workforce_lifecycle_event_mutation() returns trigger language plpgsql as $$
        begin raise exception 'workforce lifecycle history is immutable'; end $$;
        drop trigger if exists trg_workforce_lifecycle_events_immutable on workforce_lifecycle_events;
        create trigger trg_workforce_lifecycle_events_immutable before update or delete on workforce_lifecycle_events
        for each row execute function reject_workforce_lifecycle_event_mutation();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop trigger if exists trg_workforce_lifecycle_events_immutable on workforce_lifecycle_events;
        drop function if exists reject_workforce_lifecycle_event_mutation();
        drop table if exists workforce_lifecycle_events;
        drop table if exists worker_return_to_work_reviews;
        alter table worker_absences drop column if exists cover_notes;
        alter table worker_absences drop column if exists covered_by;
        alter table worker_absences drop column if exists cover_status;
        drop table if exists worker_supervision_records;
        drop table if exists worker_employment_profiles;
        """);
}
