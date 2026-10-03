using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261003110000_AddEmarMonitoringAlerts")]
public sealed class AddEmarMonitoringAlerts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table emar_escalations alter column ledger_id drop not null;
        alter table emar_escalations add column due_at timestamptz null;
        create unique index ux_emar_overdue_dose_alert on emar_escalations(mar_record_id)
          where escalation_type='OverdueDose';
        create table medication_monitoring_alerts(
          id uuid primary key,
          medication_id uuid not null references "Medications"("Id") on delete restrict,
          organization_id uuid not null, branch_id uuid not null,
          alert_type text not null check(alert_type in ('ReviewDue','EndDateApproaching')),
          due_at timestamptz not null, severity text not null,
          status text not null default 'Open' check(status in ('Open','Acknowledged','InProgress','Resolved')),
          message text not null, owner text not null default 'Medication lead',
          acknowledged_at timestamptz null, acknowledged_by text not null default '',
          resolved_at timestamptz null, resolution text not null default '',
          created_at timestamptz not null default now(),
          unique(medication_id,alert_type,due_at));
        create index ix_medication_monitoring_queue
          on medication_monitoring_alerts(organization_id,branch_id,status,due_at);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        do $$ begin
          if exists(select 1 from emar_escalations where ledger_id is null)
             or exists(select 1 from medication_monitoring_alerts) then
            raise exception 'Cannot roll back eMAR monitoring while unrecorded-dose alerts exist';
          end if;
        end $$;
        drop table medication_monitoring_alerts;
        drop index ux_emar_overdue_dose_alert;
        alter table emar_escalations drop column due_at;
        alter table emar_escalations alter column ledger_id set not null;
        """);
}
