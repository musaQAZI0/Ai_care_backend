using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20260827170000_AddCareDocumentationGovernance")]
public sealed class AddCareDocumentationGovernance:Migration
{
 protected override void Up(MigrationBuilder m)=>m.Sql("""
 create table if not exists care_note_amendments(id uuid primary key,care_note_id uuid not null references "CareNotes"("Id") on delete restrict,organization_id uuid not null,reason text not null,old_values_json text not null,new_values_json text not null,amended_by text not null,amended_at timestamptz not null default now());
 create table if not exists care_note_reviews(id uuid primary key,care_note_id uuid not null references "CareNotes"("Id") on delete restrict,organization_id uuid not null,status text not null,comment text not null,reviewed_by text not null,reviewed_at timestamptz not null default now());
 create table if not exists observation_thresholds(id uuid primary key,organization_id uuid not null,branch_id uuid not null,observation_type text not null,minimum_value numeric null,maximum_value numeric null,severity text not null default 'High',instructions text not null,active boolean not null default true,updated_at timestamptz not null default now(),unique(organization_id,branch_id,observation_type));
 create table if not exists deterioration_alerts(id uuid primary key,observation_id uuid null references "HealthObservations"("Id") on delete restrict,visit_id uuid not null references "Visits"("Id") on delete cascade,service_user_id uuid not null,organization_id uuid not null,branch_id uuid not null,alert_type text not null,severity text not null,detail text not null,immediate_action text not null default '',external_contact text not null default '',owner text not null default '',status text not null default 'Open',acknowledged_at timestamptz null,resolved_at timestamptz null,created_at timestamptz not null default now(),updated_at timestamptz not null default now());
 create index if not exists ix_deterioration_queue on deterioration_alerts(organization_id,status,severity,created_at desc);
 """);
 protected override void Down(MigrationBuilder m)=>m.Sql("drop table if exists deterioration_alerts;drop table if exists observation_thresholds;drop table if exists care_note_reviews;drop table if exists care_note_amendments;");
}
