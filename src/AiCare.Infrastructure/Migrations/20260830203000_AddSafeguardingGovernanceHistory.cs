using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20260830203000_AddSafeguardingGovernanceHistory")]
public sealed class AddSafeguardingGovernanceHistory : Migration
{
 protected override void Up(MigrationBuilder migrationBuilder)=>migrationBuilder.Sql("""
 alter table safeguarding_case_actions add column if not exists completion_evidence text not null default '';
 alter table safeguarding_case_actions add column if not exists completed_by text not null default '';
 create table if not exists safeguarding_case_events(id uuid primary key,case_id uuid not null references safeguarding_cases(id) on delete restrict,organization_id uuid not null,branch_id uuid not null,event_type text not null,detail text not null,actor text not null,occurred_at timestamptz not null default now());
 create index if not exists ix_safeguarding_events_case on safeguarding_case_events(organization_id,case_id,occurred_at);
 create or replace function prevent_safeguarding_event_mutation() returns trigger language plpgsql as $$ begin raise exception 'Safeguarding history is immutable'; end $$;
 drop trigger if exists trg_safeguarding_event_immutable on safeguarding_case_events;
 create trigger trg_safeguarding_event_immutable before update or delete on safeguarding_case_events for each row execute function prevent_safeguarding_event_mutation();
 """);
 protected override void Down(MigrationBuilder migrationBuilder)=>migrationBuilder.Sql("""
 drop trigger if exists trg_safeguarding_event_immutable on safeguarding_case_events; drop function if exists prevent_safeguarding_event_mutation(); drop table if exists safeguarding_case_events;
 alter table safeguarding_case_actions drop column if exists completed_by; alter table safeguarding_case_actions drop column if exists completion_evidence;
 """);
}
