using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260901170000_AddSensitiveOperationStepUp")]
public sealed class AddSensitiveOperationStepUp : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table if not exists auth_step_up_grants(
          id uuid primary key,
          user_id uuid not null references "AppUsers"("Id") on delete cascade,
          session_id uuid null references auth_sessions(id) on delete cascade,
          organization_id uuid not null,
          branch_id uuid null,
          purpose text not null,
          operation_scope text not null,
          evidence text not null,
          created_at timestamptz not null default now(),
          expires_at timestamptz not null,
          consumed_at timestamptz null,
          revoked_at timestamptz null
        );
        create index if not exists ix_step_up_active on auth_step_up_grants(user_id,session_id,purpose,expires_at) where consumed_at is null and revoked_at is null;

        create table if not exists auth_step_up_events(
          id uuid primary key,
          grant_id uuid null references auth_step_up_grants(id) on delete restrict,
          user_id uuid not null,
          session_id uuid null,
          organization_id uuid not null,
          branch_id uuid null,
          event_type text not null,
          purpose text not null,
          operation_scope text not null,
          detail text not null,
          actor text not null,
          occurred_at timestamptz not null default now()
        );
        create index if not exists ix_step_up_events on auth_step_up_events(organization_id,user_id,occurred_at desc);
        create or replace function reject_step_up_event_mutation() returns trigger language plpgsql as $$begin raise exception 'Step-up security history is immutable';end$$;
        drop trigger if exists trg_step_up_events_immutable on auth_step_up_events;
        create trigger trg_step_up_events_immutable before update or delete on auth_step_up_events for each row execute function reject_step_up_event_mutation();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop trigger if exists trg_step_up_events_immutable on auth_step_up_events;
        drop function if exists reject_step_up_event_mutation();
        drop table if exists auth_step_up_events;
        drop table if exists auth_step_up_grants;
        """);
}
