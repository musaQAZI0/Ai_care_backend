using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260901123000_AddMfaLifecycleGovernance")]
public sealed class AddMfaLifecycleGovernance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table auth_user_security add column if not exists mfa_status text not null default 'NotEnrolled';
        alter table auth_user_security add column if not exists mfa_failed_attempts integer not null default 0;
        alter table auth_user_security add column if not exists mfa_locked_until timestamptz null;
        alter table auth_user_security add column if not exists mfa_verified_at timestamptz null;
        alter table auth_user_security add column if not exists mfa_disabled_at timestamptz null;
        alter table auth_user_security add column if not exists mfa_reset_required boolean not null default false;

        create table if not exists auth_mfa_recovery_codes (
            id uuid primary key, user_id uuid not null references "AppUsers"("Id") on delete cascade,
            code_hash text not null, created_at timestamptz not null default now(), used_at timestamptz null,
            invalidated_at timestamptz null, unique(user_id, code_hash)
        );
        create index if not exists ix_mfa_recovery_user on auth_mfa_recovery_codes(user_id, used_at, invalidated_at);

        create table if not exists auth_mfa_policies (
            id uuid primary key, organization_id uuid not null, branch_id uuid null,
            required_roles text[] not null, grace_period_days integer not null default 7,
            enabled boolean not null default true, effective_at timestamptz not null,
            updated_by text not null, updated_at timestamptz not null default now(),
            constraint ck_mfa_grace check(grace_period_days between 0 and 90)
        );
        create unique index if not exists uq_mfa_policy_scope on auth_mfa_policies(organization_id, coalesce(branch_id,'00000000-0000-0000-0000-000000000000'::uuid));

        create table if not exists auth_mfa_events (
            id uuid primary key, user_id uuid not null, organization_id uuid not null,
            branch_id uuid null, event_type text not null, detail text not null,
            actor text not null, occurred_at timestamptz not null default now()
        );
        create index if not exists ix_mfa_events_user on auth_mfa_events(organization_id, user_id, occurred_at desc);
        create or replace function reject_mfa_event_mutation() returns trigger language plpgsql as $$
        begin raise exception 'MFA security history is immutable'; end $$;
        drop trigger if exists trg_mfa_events_immutable on auth_mfa_events;
        create trigger trg_mfa_events_immutable before update or delete on auth_mfa_events
            for each row execute function reject_mfa_event_mutation();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop trigger if exists trg_mfa_events_immutable on auth_mfa_events;
        drop function if exists reject_mfa_event_mutation();
        drop table if exists auth_mfa_events;
        drop table if exists auth_mfa_policies;
        drop table if exists auth_mfa_recovery_codes;
        alter table auth_user_security drop column if exists mfa_reset_required;
        alter table auth_user_security drop column if exists mfa_disabled_at;
        alter table auth_user_security drop column if exists mfa_verified_at;
        alter table auth_user_security drop column if exists mfa_locked_until;
        alter table auth_user_security drop column if exists mfa_failed_attempts;
        alter table auth_user_security drop column if exists mfa_status;
        """);
}
