using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261026100000_AddFinanceLedgerProjection")]
public sealed class AddFinanceLedgerProjection : Migration
{
    protected override void Up(MigrationBuilder m)=>m.Sql("""
        create view finance_invoice_ledger as
        select x.*,x.gross_amount-x.credits-x.net_cash balance,
          greatest(x.gross_amount-x.credits-x.net_cash,0) outstanding,
          greatest(-(x.gross_amount-x.credits-x.net_cash),0) customer_credit
        from (
          select i."Id" invoice_id,i."OrganizationId" organization_id,i."BranchId" branch_id,i."ServiceUserId" person_id,i."Status" status,
            d.invoice_number,d.invoice_date,d.due_date,trim(d.currency) currency,d.net_amount,d.vat_amount,d.gross_amount,
            coalesce(p.direct,0) direct_payments,coalesce(a.receipts,0) receipt_allocations,coalesce(r.refunds,0) refunds,coalesce(c.credits,0) credits,
            coalesce(p.direct,0)+coalesce(a.receipts,0)-coalesce(r.refunds,0) net_cash,
            i."Status" in ('Issued','Part paid','Paid','Overdue','Credited') is_issued
          from "Invoices" i join finance_invoice_details d on d.invoice_id=i."Id" and d.organization_id=i."OrganizationId" and d.branch_id=i."BranchId"
          left join lateral(select sum(amount) direct from finance_payments where invoice_id=i."Id" and organization_id=i."OrganizationId") p on true
          left join lateral(select sum(amount) receipts from finance_payment_allocations where invoice_id=i."Id" and organization_id=i."OrganizationId" and reversed_at is null) a on true
          left join lateral(select sum(amount) refunds from finance_refunds where invoice_id=i."Id" and organization_id=i."OrganizationId") r on true
          left join lateral(select sum(amount) credits from finance_credit_notes where invoice_id=i."Id" and organization_id=i."OrganizationId" and status='Issued') c on true
        ) x;
        """);
    protected override void Down(MigrationBuilder m)=>m.Sql("drop view finance_invoice_ledger;");
}
