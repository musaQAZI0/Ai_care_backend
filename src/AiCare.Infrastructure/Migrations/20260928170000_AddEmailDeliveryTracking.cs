using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260928170000_AddEmailDeliveryTracking")]
public sealed class AddEmailDeliveryTracking : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table if not exists email_deliveries (
            id uuid primary key,
            tenant_id uuid not null,
            job_id uuid not null unique,
            idempotency_key varchar(256) not null unique,
            email_type varchar(100) not null,
            recipient varchar(320) not null,
            provider_message_id varchar(200) null unique,
            status varchar(40) not null,
            attempt_count integer not null default 0,
            last_error varchar(500) not null default '',
            created_at timestamptz not null,
            updated_at timestamptz not null,
            accepted_at timestamptz null,
            delivered_at timestamptz null,
            bounced_at timestamptz null,
            failed_at timestamptz null
        );
        create index if not exists ix_email_deliveries_tenant_created
            on email_deliveries(tenant_id, created_at desc);

        create table if not exists email_webhook_events (
            id uuid primary key,
            provider_event_id varchar(200) not null unique,
            provider_message_id varchar(200) not null,
            event_type varchar(100) not null,
            occurred_at timestamptz not null,
            processed_at timestamptz not null
        );
        create index if not exists ix_email_webhook_events_provider_message
            on email_webhook_events(provider_message_id);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop table if exists email_webhook_events;
        drop table if exists email_deliveries;
        """);
}
