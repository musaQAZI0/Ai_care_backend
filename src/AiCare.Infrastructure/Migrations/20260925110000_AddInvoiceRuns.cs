using Microsoft.EntityFrameworkCore.Infrastructure;using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))][Migration("20260925110000_AddInvoiceRuns")]
public sealed class AddInvoiceRuns:Migration
{
 protected override void Up(MigrationBuilder m)=>m.Sql("""
 create table finance_invoice_runs(id uuid primary key,organization_id uuid not null,branch_id uuid null,period_start date not null,period_end date not null,funder_id uuid null references finance_funders(id),status text not null,requested_by text not null,created_at timestamptz not null default now(),completed_at timestamptz null,event_count integer not null default 0,invoice_count integer not null default 0,total_amount numeric(12,2) not null default 0,idempotency_key text not null,check(period_end>=period_start),check(status in ('Processing','Completed','Failed')),unique(organization_id,idempotency_key));
 alter table finance_invoice_lines add column billable_event_id uuid null references finance_billable_events(id);
 alter table finance_invoice_lines add column invoice_run_id uuid null references finance_invoice_runs(id);
 create unique index ux_invoice_line_billable_event on finance_invoice_lines(organization_id,billable_event_id) where billable_event_id is not null;
 create index ix_invoice_runs_scope on finance_invoice_runs(organization_id,branch_id,created_at desc);
 """);
 protected override void Down(MigrationBuilder m)=>m.Sql("""drop index if exists ux_invoice_line_billable_event;alter table finance_invoice_lines drop column if exists invoice_run_id;alter table finance_invoice_lines drop column if exists billable_event_id;drop table if exists finance_invoice_runs;""");
}
