using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261012100000_AddAdminRoleChangeEvents")]
public sealed class AddAdminRoleChangeEvents : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table admin_role_change_events (
          id uuid primary key,
          organization_id uuid not null,
          branch_id uuid null,
          user_id uuid not null,
          old_role text not null,
          new_role text not null,
          reason text not null,
          changed_by text not null,
          changed_at timestamptz not null default now()
        );
        create index ix_admin_role_change_events_account on admin_role_change_events(organization_id,user_id,changed_at desc);
        create or replace function reject_admin_role_change_event_mutation() returns trigger language plpgsql as $$
        begin
          raise exception 'Admin role change history is immutable';
        end $$;
        create trigger trg_admin_role_change_events_immutable before update or delete on admin_role_change_events
          for each row execute function reject_admin_role_change_event_mutation();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop trigger if exists trg_admin_role_change_events_immutable on admin_role_change_events;
        drop function if exists reject_admin_role_change_event_mutation();
        drop table if exists admin_role_change_events;
        """);
}
