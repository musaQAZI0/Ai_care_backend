using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261122090000_AddAdminBranchChangeEvents")]
public sealed class AddAdminBranchChangeEvents : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table admin_branch_change_events (
          id uuid primary key,
          organization_id uuid not null,
          user_id uuid not null,
          new_branch_id uuid not null,
          care_worker_id uuid not null,
          reason text not null,
          changed_by text not null,
          changed_at timestamptz not null default now()
        );
        create index ix_admin_branch_change_events_account on admin_branch_change_events(organization_id,user_id,changed_at desc);
        create function reject_admin_branch_change_event_mutation() returns trigger language plpgsql as $$
        begin
          raise exception 'Admin branch change history is immutable';
        end $$;
        create trigger trg_admin_branch_change_events_immutable before update or delete on admin_branch_change_events
          for each row execute function reject_admin_branch_change_event_mutation();
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop table if exists admin_branch_change_events;
        drop function if exists reject_admin_branch_change_event_mutation();
        """);
}
