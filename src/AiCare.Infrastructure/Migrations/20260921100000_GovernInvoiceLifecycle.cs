using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260921100000_GovernInvoiceLifecycle")]
public sealed class GovernInvoiceLifecycle : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create unique index ux_finance_payment_reference
            on finance_payments(organization_id,invoice_id,reference);

        alter table "Invoices" add constraint ck_invoice_governed_status
            check ("Status" in ('Generated','Approved','Issued','Part paid','Paid','Overdue','Void')) not valid;

        create or replace function prevent_issued_invoice_line_mutation() returns trigger as $$
        declare invoice_status text;
        begin
            select "Status" into invoice_status from "Invoices" where "Id"=coalesce(new.invoice_id,old.invoice_id);
            if invoice_status is not null and invoice_status <> 'Generated' then
                raise exception 'Invoice lines are immutable after approval.' using errcode='23514';
            end if;
            return case when tg_op='DELETE' then old else new end;
        end;
        $$ language plpgsql;

        create trigger trg_finance_invoice_lines_immutable
            before insert or update or delete on finance_invoice_lines
            for each row execute function prevent_issued_invoice_line_mutation();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop trigger if exists trg_finance_invoice_lines_immutable on finance_invoice_lines;
        drop function if exists prevent_issued_invoice_line_mutation();
        alter table "Invoices" drop constraint if exists ck_invoice_governed_status;
        drop index if exists ux_finance_payment_reference;
        """);
}
