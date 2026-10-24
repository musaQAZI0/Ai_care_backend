using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261023130000_RotaPublications")]
public sealed class RotaPublications : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table rota_publications (
          id uuid primary key,
          organization_id uuid not null references "Organizations"("Id") on delete cascade,
          branch_id uuid not null references "Branches"("Id"),
          week_start date not null,
          revision integer not null check (revision > 0),
          published_at timestamptz not null default now(),
          published_by text not null,
          unique(organization_id,branch_id,week_start,revision)
        );
        create index ix_rota_publications_week on rota_publications(organization_id,branch_id,week_start,revision desc);
        create table rota_publication_visits (
          publication_id uuid not null references rota_publications(id) on delete cascade,
          visit_id uuid not null references "Visits"("Id") on delete cascade,
          care_worker_id uuid not null references "CareWorkers"("Id"),
          starts_at timestamptz not null,
          duration_minutes integer not null,
          visit_type text not null,
          primary key(publication_id,visit_id)
        );
        create index ix_rota_publication_worker on rota_publication_visits(care_worker_id,publication_id);
        create table rota_publication_acknowledgements (
          publication_id uuid not null references rota_publications(id) on delete cascade,
          care_worker_id uuid not null references "CareWorkers"("Id"),
          acknowledged_by text not null,
          acknowledged_at timestamptz not null default now(),
          primary key(publication_id,care_worker_id)
        );
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop table if exists rota_publication_acknowledgements;
        drop table if exists rota_publication_visits;
        drop table if exists rota_publications;
        """);
}
