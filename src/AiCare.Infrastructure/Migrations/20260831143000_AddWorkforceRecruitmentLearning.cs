using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260831143000_AddWorkforceRecruitmentLearning")]
public sealed class AddWorkforceRecruitmentLearning : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table if not exists worker_recruitment_cases (
          id uuid primary key, care_worker_id uuid not null unique references "CareWorkers"("Id") on delete cascade,
          organization_id uuid not null, branch_id uuid not null, status text not null default 'Application',
          application_reference text not null, applied_at timestamptz not null, interview_at timestamptz null,
          interview_outcome text not null default '', identity_status text not null default 'Pending',
          right_to_work_status text not null default 'Pending', dbs_status text not null default 'Pending',
          health_declaration_status text not null default 'Pending', onboarding_status text not null default 'NotStarted',
          owner text not null default '', decision_reason text not null default '', decided_by text not null default '', decided_at timestamptz null,
          created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
          constraint ck_worker_recruitment_status check (status in ('Application','Interview','ConditionalOffer','Onboarding','Cleared','Rejected','Withdrawn'))
        );
        create table if not exists worker_recruitment_references (
          id uuid primary key, recruitment_case_id uuid not null references worker_recruitment_cases(id) on delete cascade,
          organization_id uuid not null, referee_name text not null, relationship text not null default '',
          requested_at timestamptz not null, received_at timestamptz null, outcome text not null default 'Pending', evidence_reference text not null default '', created_at timestamptz not null default now()
        );
        create index if not exists ix_worker_recruitment_scope on worker_recruitment_cases(organization_id,branch_id,status);

        create table if not exists training_catalogue_courses (
          id uuid primary key, organization_id uuid not null, branch_id uuid null, code text not null, title text not null,
          category text not null default 'Mandatory', provider text not null default '', validity_months integer null,
          is_mandatory boolean not null default false, active boolean not null default true, description text not null default '', created_at timestamptz not null default now(),
          constraint uq_training_catalogue_code unique(organization_id,code), constraint ck_training_validity check(validity_months is null or validity_months>0)
        );
        create table if not exists worker_training_bookings (
          id uuid primary key, course_id uuid not null references training_catalogue_courses(id) on delete restrict,
          care_worker_id uuid not null references "CareWorkers"("Id") on delete cascade, organization_id uuid not null, branch_id uuid not null,
          scheduled_at timestamptz not null, status text not null default 'Booked', completion_evidence text not null default '',
          completed_at timestamptz null, expires_at timestamptz null, booked_by text not null, created_at timestamptz not null default now(),
          constraint ck_training_booking_status check(status in ('Booked','Attended','Completed','Cancelled','NoShow'))
        );
        create index if not exists ix_training_bookings_worker on worker_training_bookings(organization_id,care_worker_id,status,scheduled_at);

        create table if not exists worker_probation_reviews (
          id uuid primary key, care_worker_id uuid not null references "CareWorkers"("Id") on delete cascade,
          organization_id uuid not null, branch_id uuid not null, review_at timestamptz not null, reviewer text not null,
          outcome text not null, objectives text not null default '', evidence_reference text not null,
          extension_until date null, completed_at timestamptz not null default now(), created_at timestamptz not null default now()
        );
        create index if not exists ix_worker_probation_worker on worker_probation_reviews(organization_id,care_worker_id,review_at desc);
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop table if exists worker_probation_reviews;
        drop table if exists worker_training_bookings;
        drop table if exists training_catalogue_courses;
        drop table if exists worker_recruitment_references;
        drop table if exists worker_recruitment_cases;
        """);
}
