using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261014110000_AdminPermissionDenials")]
public sealed class AdminPermissionDenials : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table if not exists admin_permission_denials (
          organization_id uuid not null references "Organizations"("Id") on delete cascade,
          user_id uuid not null references "AppUsers"("Id") on delete cascade,
          permission_key text not null,
          changed_by text not null,
          reason text not null,
          changed_at timestamptz not null default now(),
          primary key (organization_id,user_id,permission_key)
        );
        create index if not exists ix_admin_permission_denials_user on admin_permission_denials(user_id,organization_id);
        create table if not exists admin_permission_change_events (
          id uuid primary key,
          organization_id uuid not null references "Organizations"("Id") on delete cascade,
          user_id uuid not null references "AppUsers"("Id") on delete cascade,
          previous_disabled_keys text[] not null,
          disabled_keys text[] not null,
          reason text not null,
          changed_by text not null,
          changed_at timestamptz not null default now()
        );
        create index if not exists ix_admin_permission_change_events_user on admin_permission_change_events(organization_id,user_id,changed_at desc);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop table if exists admin_permission_change_events;
        drop table if exists admin_permission_denials;
        """);
}
