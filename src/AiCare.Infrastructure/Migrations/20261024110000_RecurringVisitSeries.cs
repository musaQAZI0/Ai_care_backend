using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261024110000_RecurringVisitSeries")]
public sealed class RecurringVisitSeries : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table recurring_visit_series (
          id uuid primary key,
          organization_id uuid not null references "Organizations"("Id") on delete cascade,
          branch_id uuid not null references "Branches"("Id"),
          service_user_id uuid not null references "ServiceUsers"("Id"),
          frequency text not null check (frequency in ('Daily','Weekly','Fortnightly')),
          created_at timestamptz not null default now(),
          created_by text not null,
          updated_at timestamptz null,
          updated_by text null
        );
        create table recurring_visit_members (
          series_id uuid not null references recurring_visit_series(id) on delete cascade,
          visit_id uuid not null unique references "Visits"("Id") on delete cascade,
          ordinal integer not null,
          primary key(series_id,visit_id),
          unique(series_id,ordinal)
        );
        create index ix_recurring_visit_series_queue on recurring_visit_series(organization_id,branch_id,created_at desc);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop table if exists recurring_visit_members;
        drop table if exists recurring_visit_series;
        """);
}
