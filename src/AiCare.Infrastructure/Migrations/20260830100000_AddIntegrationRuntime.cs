using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20260830100000_AddIntegrationRuntime")]
public sealed class AddIntegrationRuntime:Migration
{
 protected override void Up(MigrationBuilder m)=>m.Sql("""
 alter table integration_connectors add column if not exists schedule_minutes integer null;
 alter table integration_connectors add column if not exists last_scheduled_at timestamptz null;
 alter table integration_connectors add column if not exists webhook_secret_protected text null;
 alter table integration_jobs add column if not exists options_json jsonb not null default '{}'::jsonb;
 alter table integration_jobs add column if not exists attempt_count integer not null default 0;
 alter table integration_jobs add column if not exists max_attempts integer not null default 5;
 alter table integration_jobs add column if not exists next_attempt_at timestamptz null;
 alter table integration_jobs add column if not exists started_at timestamptz null;
 alter table integration_jobs add column if not exists output_json jsonb null;
 alter table integration_jobs add column if not exists output_content_type text null;
 alter table integration_sync_failures add column if not exists dead_lettered_at timestamptz null;
 create index if not exists ix_integration_jobs_worker on integration_jobs(status,next_attempt_at,created_at);
 """);
 protected override void Down(MigrationBuilder m)=>m.Sql("""
 drop index if exists ix_integration_jobs_worker;
 alter table integration_sync_failures drop column if exists dead_lettered_at;
 alter table integration_jobs drop column if exists output_content_type,drop column if exists output_json,drop column if exists started_at,drop column if exists next_attempt_at,drop column if exists max_attempts,drop column if exists attempt_count,drop column if exists options_json;
 alter table integration_connectors drop column if exists webhook_secret_protected,drop column if exists last_scheduled_at,drop column if exists schedule_minutes;
 """);
}
