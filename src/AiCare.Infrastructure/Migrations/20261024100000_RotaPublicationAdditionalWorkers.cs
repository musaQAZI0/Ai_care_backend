using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261024100000_RotaPublicationAdditionalWorkers")]
public sealed class RotaPublicationAdditionalWorkers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table rota_publication_additional_workers (
          publication_id uuid not null references rota_publications(id) on delete cascade,
          visit_id uuid not null references "Visits"("Id") on delete cascade,
          care_worker_id uuid not null references "CareWorkers"("Id"),
          primary key(publication_id,visit_id,care_worker_id)
        );
        create index ix_rota_publication_additional_worker on rota_publication_additional_workers(care_worker_id,publication_id);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("drop table if exists rota_publication_additional_workers;");
}
