using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260905120000_AddIdempotencyKeys")]
public sealed class AddIdempotencyKeys : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table if not exists api_idempotency_keys (
            id uuid primary key,
            organization_id uuid not null,
            branch_id uuid null,
            actor_user_id uuid null,
            endpoint text not null,
            idempotency_key text not null,
            resource_type text not null,
            resource_id uuid not null,
            created_at timestamptz not null default now(),
            constraint uq_api_idempotency_key unique(organization_id, actor_user_id, endpoint, idempotency_key)
        );
        create index if not exists ix_api_idempotency_actor on api_idempotency_keys(organization_id, actor_user_id, created_at desc);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("drop table if exists api_idempotency_keys;");
}
