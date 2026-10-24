using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261023110000_VisitCoverWorkflows")]
public sealed class VisitCoverWorkflows : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table visit_cover_requests (
          id uuid primary key,
          visit_id uuid not null references "Visits"("Id") on delete cascade,
          organization_id uuid not null references "Organizations"("Id") on delete cascade,
          branch_id uuid not null references "Branches"("Id"),
          original_worker_id uuid not null references "CareWorkers"("Id"),
          reason text not null check (length(trim(reason)) between 1 and 500),
          status text not null default 'Open' check (status in ('Open','Covered','Closed')),
          created_by text not null,
          created_at timestamptz not null default now(),
          resolved_at timestamptz null,
          resolved_by text null,
          resolution_reason text not null default ''
        );
        create unique index ix_visit_cover_one_open on visit_cover_requests(organization_id,visit_id) where status='Open';
        create index ix_visit_cover_queue on visit_cover_requests(organization_id,branch_id,status,created_at desc);
        create table visit_cover_offers (
          id uuid primary key,
          cover_request_id uuid not null references visit_cover_requests(id) on delete cascade,
          organization_id uuid not null references "Organizations"("Id") on delete cascade,
          branch_id uuid not null references "Branches"("Id"),
          care_worker_id uuid not null references "CareWorkers"("Id"),
          status text not null default 'Pending' check (status in ('Pending','Accepted','Declined','Closed')),
          offered_by text not null,
          offered_at timestamptz not null default now(),
          responded_at timestamptz null,
          response_reason text not null default '',
          unique (cover_request_id,care_worker_id)
        );
        create index ix_visit_cover_worker_offers on visit_cover_offers(organization_id,care_worker_id,status,offered_at desc);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop table if exists visit_cover_offers;
        drop table if exists visit_cover_requests;
        """);
}
