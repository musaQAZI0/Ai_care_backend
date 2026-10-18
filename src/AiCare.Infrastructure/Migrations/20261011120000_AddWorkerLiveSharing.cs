using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261011120000_AddWorkerLiveSharing")]
public sealed class AddWorkerLiveSharing : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table worker_live_sharing (
          id uuid primary key,
          organization_id uuid not null,
          branch_id uuid not null,
          care_worker_id uuid not null references "CareWorkers"("Id") on delete cascade,
          visit_id uuid not null references "Visits"("Id") on delete cascade,
          started_at timestamptz not null default now(),
          ends_at timestamptz not null,
          stopped_at timestamptz null
        );
        create unique index uq_worker_live_sharing_active on worker_live_sharing(organization_id,care_worker_id) where stopped_at is null;
        create index ix_worker_live_sharing_scope on worker_live_sharing(organization_id,branch_id,visit_id,ends_at);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("drop table if exists worker_live_sharing;");
}
