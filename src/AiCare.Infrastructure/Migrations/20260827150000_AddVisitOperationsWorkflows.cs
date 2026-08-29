using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260827150000_AddVisitOperationsWorkflows")]
public sealed class AddVisitOperationsWorkflows : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table if not exists visit_exceptions (
          id uuid primary key, visit_id uuid not null references "Visits"("Id") on delete cascade, service_user_id uuid not null,
          organization_id uuid not null, branch_id uuid not null, exception_type text not null, severity text not null,
          reason text not null, immediate_action text not null, notify_manager boolean not null, follow_up_owner text not null,
          escalation_due_at timestamptz null, status text not null default 'Open', acknowledged_at timestamptz null,
          resolved_at timestamptz null, closed_at timestamptz null, created_by text not null, created_at timestamptz not null default now(), updated_at timestamptz not null default now()
        );
        create index if not exists ix_visit_exceptions_queue on visit_exceptions(organization_id,status,escalation_due_at);
        create table if not exists visit_escalation_events (
          id uuid primary key, visit_exception_id uuid not null references visit_exceptions(id) on delete cascade,
          organization_id uuid not null, action text not null, detail text not null, actor text not null, created_at timestamptz not null default now()
        );
        create table if not exists schedule_change_history (
          id uuid primary key, visit_id uuid not null, organization_id uuid not null, branch_id uuid not null,
          change_type text not null, old_values_json text not null, new_values_json text not null, reason text not null,
          changed_by text not null, changed_at timestamptz not null default now()
        );
        create index if not exists ix_schedule_history_visit on schedule_change_history(organization_id,visit_id,changed_at desc);
        create table if not exists visit_handovers (
          id uuid primary key, visit_id uuid not null references "Visits"("Id") on delete cascade, service_user_id uuid not null,
          organization_id uuid not null, branch_id uuid not null, summary text not null, outstanding_actions text not null,
          urgent boolean not null default false, attachment_reference text not null default '', created_by text not null,
          created_at timestamptz not null default now()
        );
        create index if not exists ix_visit_handovers_queue on visit_handovers(organization_id,urgent,created_at desc);
        create table if not exists handover_acknowledgements (
          handover_id uuid not null references visit_handovers(id) on delete cascade, user_id uuid not null,
          organization_id uuid not null, acknowledged_at timestamptz not null default now(), primary key(handover_id,user_id)
        );
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop table if exists handover_acknowledgements; drop table if exists visit_handovers;
        drop table if exists schedule_change_history; drop table if exists visit_escalation_events; drop table if exists visit_exceptions;
        """);
}
