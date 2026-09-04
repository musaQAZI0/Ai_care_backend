using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260901003000_AddPrivacyRightsGovernance")]
public sealed class AddPrivacyRightsGovernance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table if not exists privacy_requests (
            id uuid primary key, organization_id uuid not null, branch_id uuid not null,
            service_user_id uuid not null, request_type text not null, requester_name text not null,
            requester_relationship text not null, request_channel text not null, reason text not null,
            status text not null default 'Received', identity_status text not null default 'Pending',
            identity_evidence text not null default '', authority_evidence text not null default '',
            owner text not null default '', received_at timestamptz not null, due_at timestamptz not null,
            completed_at timestamptz null, decision text not null default '', created_by text not null,
            constraint ck_privacy_request_type check (request_type in ('SubjectAccess','Rectification','Restriction','Erasure','Portability')),
            constraint ck_privacy_status check (status in ('Received','Verified','InProgress','PackReady','Released','Closed','Reopened','Refused'))
        );
        create index if not exists ix_privacy_requests_scope on privacy_requests(organization_id, branch_id, status, due_at);

        create table if not exists privacy_request_records (
            id uuid primary key, request_id uuid not null references privacy_requests(id) on delete restrict,
            record_type text not null, record_id uuid null, field_name text not null default '',
            discovery_reference text not null, review_action text not null default 'Pending',
            review_reason text not null default '', reviewed_by text not null default '', reviewed_at timestamptz null,
            constraint ck_privacy_review_action check (review_action in ('Pending','Disclose','Redact','Exempt'))
        );
        create index if not exists ix_privacy_records_request on privacy_request_records(request_id, review_action);

        create table if not exists privacy_disclosures (
            id uuid primary key, request_id uuid not null references privacy_requests(id) on delete restrict,
            version integer not null, manifest_json jsonb not null, generated_by text not null,
            generated_at timestamptz not null default now(), released_by text null, released_at timestamptz null,
            release_evidence text not null default '', constraint uq_privacy_disclosure_version unique(request_id, version)
        );

        create table if not exists processing_restrictions (
            id uuid primary key, organization_id uuid not null, branch_id uuid not null, service_user_id uuid not null,
            privacy_request_id uuid null references privacy_requests(id) on delete restrict, scope text not null,
            reason text not null, status text not null default 'Active', applied_by text not null,
            applied_at timestamptz not null default now(), lifted_by text null, lifted_at timestamptz null,
            lift_evidence text not null default '', constraint ck_processing_restriction_status check (status in ('Active','Lifted'))
        );
        create index if not exists ix_processing_restriction_active on processing_restrictions(organization_id, branch_id, service_user_id, status);

        create table if not exists legal_holds (
            id uuid primary key, organization_id uuid not null, branch_id uuid null, service_user_id uuid null,
            scope text not null, reason text not null, authority text not null, status text not null default 'Active',
            review_due_at timestamptz not null, created_by text not null, created_at timestamptz not null default now(),
            release_requested_by text null, release_requested_at timestamptz null, released_by text null,
            released_at timestamptz null, release_evidence text not null default '',
            constraint ck_legal_hold_status check (status in ('Active','ReleaseRequested','Released'))
        );
        create index if not exists ix_legal_hold_scope on legal_holds(organization_id, status, review_due_at);

        create table if not exists privacy_case_events (
            id uuid primary key, request_id uuid not null references privacy_requests(id) on delete restrict,
            organization_id uuid not null, event_type text not null, detail text not null, actor text not null,
            occurred_at timestamptz not null default now()
        );
        create or replace function reject_privacy_event_mutation() returns trigger language plpgsql as $$
        begin raise exception 'Privacy case history is immutable'; end $$;
        drop trigger if exists trg_privacy_events_immutable on privacy_case_events;
        create trigger trg_privacy_events_immutable before update or delete on privacy_case_events
            for each row execute function reject_privacy_event_mutation();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop trigger if exists trg_privacy_events_immutable on privacy_case_events;
        drop function if exists reject_privacy_event_mutation();
        drop table if exists privacy_case_events;
        drop table if exists legal_holds;
        drop table if exists processing_restrictions;
        drop table if exists privacy_disclosures;
        drop table if exists privacy_request_records;
        drop table if exists privacy_requests;
        """);
}
