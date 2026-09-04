using Microsoft.EntityFrameworkCore.Infrastructure;using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))][Migration("20260831103000_AddComplaintGovernance")]
public sealed class AddComplaintGovernance:Migration{
protected override void Up(MigrationBuilder m)=>m.Sql("""
alter table family_feedback_cases add column if not exists assigned_to text not null default '';
alter table family_feedback_cases add column if not exists acknowledged_at timestamptz null;
alter table family_feedback_cases add column if not exists investigation_summary text not null default '';
alter table family_feedback_cases add column if not exists response_text text not null default '';
alter table family_feedback_cases add column if not exists responded_at timestamptz null;
alter table family_feedback_cases add column if not exists closure_summary text not null default '';
alter table family_feedback_cases add column if not exists closed_at timestamptz null;
alter table family_feedback_cases add column if not exists resolution_approved_by text not null default '';
create table if not exists complaint_case_events(id uuid primary key,case_id uuid not null references family_feedback_cases(id) on delete restrict,organization_id uuid not null,branch_id uuid null,event_type text not null,detail text not null,visible_to_family boolean not null default false,actor text not null,occurred_at timestamptz not null default now());
create index if not exists ix_complaint_events_case on complaint_case_events(organization_id,case_id,occurred_at);
create or replace function prevent_complaint_event_mutation() returns trigger language plpgsql as $$ begin raise exception 'Complaint history is immutable'; end $$;
drop trigger if exists trg_complaint_event_immutable on complaint_case_events;create trigger trg_complaint_event_immutable before update or delete on complaint_case_events for each row execute function prevent_complaint_event_mutation();
""");protected override void Down(MigrationBuilder m)=>m.Sql("""drop trigger if exists trg_complaint_event_immutable on complaint_case_events;drop function if exists prevent_complaint_event_mutation();drop table if exists complaint_case_events;alter table family_feedback_cases drop column if exists resolution_approved_by;alter table family_feedback_cases drop column if exists closed_at;alter table family_feedback_cases drop column if exists closure_summary;alter table family_feedback_cases drop column if exists responded_at;alter table family_feedback_cases drop column if exists response_text;alter table family_feedback_cases drop column if exists investigation_summary;alter table family_feedback_cases drop column if exists acknowledged_at;alter table family_feedback_cases drop column if exists assigned_to;""");}
