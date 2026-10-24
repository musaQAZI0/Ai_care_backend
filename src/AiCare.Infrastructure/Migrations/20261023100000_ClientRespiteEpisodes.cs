using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261023100000_ClientRespiteEpisodes")]
public sealed class ClientRespiteEpisodes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table client_respite_episodes (
            id uuid primary key,
            organization_id uuid not null references "Organizations"("Id") on delete cascade,
            branch_id uuid not null references "Branches"("Id"),
            service_user_id uuid not null references "ServiceUsers"("Id") on delete cascade,
            reason text not null check (length(trim(reason)) between 1 and 120),
            starts_at timestamptz not null,
            ends_at timestamptz not null,
            notes text not null default '',
            status text not null default 'Scheduled' check (status in ('Scheduled','Cancelled')),
            created_by text not null,
            created_at timestamptz not null default now(),
            cancelled_by text null,
            cancelled_at timestamptz null,
            cancellation_reason text null,
            constraint ck_client_respite_period check (ends_at > starts_at)
        );
        create index ix_client_respite_person_period on client_respite_episodes(organization_id,service_user_id,starts_at desc);
        create index ix_client_respite_active_period on client_respite_episodes(organization_id,service_user_id,starts_at,ends_at) where status='Scheduled';
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("drop table if exists client_respite_episodes;");
}
