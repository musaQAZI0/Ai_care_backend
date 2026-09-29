using Microsoft.EntityFrameworkCore.Infrastructure;using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))][Migration("20260925150000_GovernInvoiceRunsAndReview")]
public sealed class GovernInvoiceRunsAndReview:Migration{protected override void Up(MigrationBuilder m)=>m.Sql("""
alter table finance_invoice_runs add column failure_reason text not null default '';
alter table finance_invoice_runs add column cancelled_at timestamptz null;
alter table finance_invoice_runs add column cancelled_by text null;
alter table finance_invoice_runs drop constraint if exists finance_invoice_runs_status_check;
alter table finance_invoice_runs add constraint finance_invoice_runs_status_check check(status in ('Processing','Completed','Failed','Cancelled'));
alter table "Invoices" drop constraint if exists ck_invoice_governed_status;
alter table "Invoices" add constraint ck_invoice_governed_status check("Status" in ('Generated','Reviewed','Approved','Issued','Part paid','Paid','Overdue','Credited','Void'));
""");protected override void Down(MigrationBuilder m)=>m.Sql("""
update "Invoices" set "Status"='Generated' where "Status"='Reviewed';alter table "Invoices" drop constraint if exists ck_invoice_governed_status;alter table "Invoices" add constraint ck_invoice_governed_status check("Status" in ('Generated','Approved','Issued','Part paid','Paid','Overdue','Credited','Void'));alter table finance_invoice_runs drop constraint if exists finance_invoice_runs_status_check;alter table finance_invoice_runs add constraint finance_invoice_runs_status_check check(status in ('Processing','Completed','Failed'));alter table finance_invoice_runs drop column if exists cancelled_by;alter table finance_invoice_runs drop column if exists cancelled_at;alter table finance_invoice_runs drop column if exists failure_reason;
""");}
