using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20260830213000_AddIncidentInvestigationCapa")]
public sealed class AddIncidentInvestigationCapa:Migration
{
 protected override void Up(MigrationBuilder m)=>m.Sql("""
 create table if not exists incident_investigations(id uuid primary key,incident_id uuid not null unique references "Incidents"("Id") on delete restrict,organization_id uuid not null,branch_id uuid not null,status text not null check(status in ('InProgress','Completed')),chronology text not null,evidence_reviewed text not null,findings text not null,root_cause text not null,lessons_learned text not null,owner text not null,reviewed_by text not null default '',started_at timestamptz not null default now(),completed_at timestamptz null,updated_at timestamptz not null default now());
 create table if not exists incident_capa_actions(id uuid primary key,incident_id uuid not null references "Incidents"("Id") on delete restrict,organization_id uuid not null,branch_id uuid not null,action_type text not null check(action_type in ('Corrective','Preventive')),detail text not null,owner text not null,due_at timestamptz not null,status text not null default 'Open' check(status in ('Open','Completed')),completion_evidence text not null default '',completed_by text not null default '',completed_at timestamptz null,created_at timestamptz not null default now());
 create index if not exists ix_incident_capa_open on incident_capa_actions(organization_id,incident_id,status,due_at);
 create table if not exists incident_governance_events(id uuid primary key,incident_id uuid not null references "Incidents"("Id") on delete restrict,organization_id uuid not null,branch_id uuid not null,event_type text not null,detail text not null,actor text not null,occurred_at timestamptz not null default now());
 create or replace function prevent_incident_event_mutation() returns trigger language plpgsql as $$ begin raise exception 'Incident governance history is immutable'; end $$;
 drop trigger if exists trg_incident_event_immutable on incident_governance_events; create trigger trg_incident_event_immutable before update or delete on incident_governance_events for each row execute function prevent_incident_event_mutation();
 """);
 protected override void Down(MigrationBuilder m)=>m.Sql("""drop trigger if exists trg_incident_event_immutable on incident_governance_events;drop function if exists prevent_incident_event_mutation();drop table if exists incident_governance_events;drop table if exists incident_capa_actions;drop table if exists incident_investigations;""");
}
