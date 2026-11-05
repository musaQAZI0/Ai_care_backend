using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20261105120000_AddVisitTaskReconciliations")]
public sealed class AddVisitTaskReconciliations : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table visit_task_reconciliations (
          id uuid primary key,visit_id uuid not null references "Visits"("Id"),
          care_plan_id uuid not null references "CarePlans"("Id"),organization_id uuid not null,branch_id uuid not null,
          reason text not null check(length(trim(reason)) between 10 and 2000),retired_task_ids uuid[] not null,
          performed_by text not null,performed_at timestamptz not null default now()
        );
        create index ix_visit_task_reconciliations_visit on visit_task_reconciliations(organization_id,branch_id,visit_id);
        create function guard_visit_task_reconciliation_history() returns trigger language plpgsql as $$
        begin raise exception 'Visit task reconciliation history is append-only' using errcode='23514'; end $$;
        create trigger visit_task_reconciliation_history_guard before update or delete on visit_task_reconciliations
          for each row execute function guard_visit_task_reconciliation_history();
        """);
    protected override void Down(MigrationBuilder m) => throw new NotSupportedException("Retain reconciliation history; recover forwards.");
}
