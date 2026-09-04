using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260902110000_AddDelegationEmergencyAccess")]
public sealed class AddDelegationEmergencyAccess : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            create table if not exists access_delegations (
                id uuid primary key,
                organization_id uuid not null,
                branch_id uuid null,
                person_id uuid null,
                granted_to_user_id uuid not null references "AppUsers"("Id") on delete restrict,
                delegated_role text not null,
                action_scope text not null,
                route_prefix text not null,
                http_methods text[] not null,
                reason text not null,
                starts_at timestamptz not null,
                expires_at timestamptz not null,
                status text not null default 'Active',
                granted_by_user_id uuid not null references "AppUsers"("Id") on delete restrict,
                granted_by text not null,
                revoked_at timestamptz null,
                revoked_by text null,
                revocation_reason text null,
                reviewed_at timestamptz null,
                reviewed_by text null,
                review_decision text null,
                review_notes text null,
                created_at timestamptz not null default now(),
                constraint ck_access_delegation_period check (expires_at > starts_at),
                constraint ck_access_delegation_role check (delegated_role in ('CareCoordinator','CareManager'))
            );
            create index if not exists ix_access_delegations_active
                on access_delegations(organization_id, granted_to_user_id, status, starts_at, expires_at);

            create table if not exists emergency_access_grants (
                id uuid primary key,
                organization_id uuid not null,
                branch_id uuid not null,
                person_id uuid not null,
                user_id uuid not null references "AppUsers"("Id") on delete restrict,
                action_scope text not null,
                route_prefix text not null,
                http_methods text[] not null,
                justification text not null,
                status text not null default 'Active',
                activated_at timestamptz not null default now(),
                expires_at timestamptz not null,
                closed_at timestamptz null,
                reviewed_at timestamptz null,
                reviewed_by text null,
                review_decision text null,
                review_notes text null,
                constraint ck_emergency_access_expiry check (expires_at > activated_at)
            );
            create index if not exists ix_emergency_access_active
                on emergency_access_grants(organization_id, user_id, status, expires_at);

            create table if not exists privileged_access_events (
                id uuid primary key,
                organization_id uuid not null,
                branch_id uuid null,
                access_type text not null,
                access_id uuid not null,
                event_type text not null,
                actor_user_id uuid null,
                actor text not null,
                detail text not null,
                occurred_at timestamptz not null default now()
            );
            create index if not exists ix_privileged_access_history
                on privileged_access_events(organization_id, access_type, access_id, occurred_at);

            create or replace function reject_privileged_access_event_mutation() returns trigger language plpgsql as $$
            begin raise exception 'privileged access events are immutable'; end $$;
            drop trigger if exists trg_privileged_access_events_immutable on privileged_access_events;
            create trigger trg_privileged_access_events_immutable before update or delete on privileged_access_events
                for each row execute function reject_privileged_access_event_mutation();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            drop trigger if exists trg_privileged_access_events_immutable on privileged_access_events;
            drop function if exists reject_privileged_access_event_mutation();
            drop table if exists privileged_access_events;
            drop table if exists emergency_access_grants;
            drop table if exists access_delegations;
            """);
    }
}
