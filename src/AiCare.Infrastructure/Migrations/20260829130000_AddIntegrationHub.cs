using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260829130000_AddIntegrationHub")]
public sealed class AddIntegrationHub : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table if not exists integration_connectors (
            id uuid primary key, organization_id uuid not null, branch_id uuid not null,
            name text not null, connector_type text not null, endpoint_url text not null default '',
            status text not null default 'Active', configuration_json jsonb not null default '{}'::jsonb,
            created_by text not null, created_at timestamptz not null default now(), updated_at timestamptz not null default now()
        );
        create index if not exists ix_integration_connectors_tenant on integration_connectors(organization_id, branch_id, created_at desc);

        create table if not exists integration_jobs (
            id uuid primary key, connector_id uuid not null references integration_connectors(id) on delete cascade,
            organization_id uuid not null, branch_id uuid not null, direction text not null,
            status text not null default 'Pending', resource_type text not null, requested_by text not null,
            record_count integer not null default 0, error_message text null,
            created_at timestamptz not null default now(), completed_at timestamptz null
        );
        create index if not exists ix_integration_jobs_tenant on integration_jobs(organization_id, branch_id, created_at desc);

        create table if not exists integration_webhook_events (
            id uuid primary key, connector_id uuid null references integration_connectors(id) on delete set null,
            organization_id uuid not null, branch_id uuid not null, event_type text not null,
            external_event_id text null, status text not null default 'Received', payload_json jsonb not null default '{}'::jsonb,
            received_at timestamptz not null default now(), processed_at timestamptz null
        );
        create unique index if not exists ux_integration_webhook_external on integration_webhook_events(connector_id, external_event_id) where external_event_id is not null;

        create table if not exists integration_sync_failures (
            id uuid primary key, connector_id uuid null references integration_connectors(id) on delete set null,
            job_id uuid null references integration_jobs(id) on delete set null,
            webhook_event_id uuid null references integration_webhook_events(id) on delete set null,
            organization_id uuid not null, branch_id uuid not null, operation text not null,
            error_message text not null, payload_json jsonb not null default '{}'::jsonb,
            status text not null default 'Pending', retry_count integer not null default 0,
            next_retry_at timestamptz null, last_retried_at timestamptz null, resolved_at timestamptz null,
            created_at timestamptz not null default now()
        );
        create index if not exists ix_integration_failures_queue on integration_sync_failures(organization_id, branch_id, status, next_retry_at);

        create table if not exists integration_audit_history (
            id uuid primary key, organization_id uuid not null, branch_id uuid not null,
            action text not null, actor text not null, entity_type text not null, entity_id uuid null,
            detail_json jsonb not null default '{}'::jsonb, created_at timestamptz not null default now()
        );
        create index if not exists ix_integration_audit_tenant on integration_audit_history(organization_id, branch_id, created_at desc);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop table if exists integration_audit_history;
        drop table if exists integration_sync_failures;
        drop table if exists integration_webhook_events;
        drop table if exists integration_jobs;
        drop table if exists integration_connectors;
        """);
}
