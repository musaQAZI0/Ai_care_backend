using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261025140000_AddInvoiceCareRecipientSnapshot")]
public sealed class AddInvoiceCareRecipientSnapshot : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table finance_invoice_details add column care_recipient_id uuid null;
        alter table finance_invoice_details add column payer_id uuid null;
        alter table finance_invoice_details add column care_recipient_name text null;
        comment on column finance_invoice_details.care_recipient_name is
          'Care recipient snapshot for new invoices; customer_name/address identify the liable payer. Legacy snapshots are intentionally unchanged.';
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table finance_invoice_details drop column care_recipient_name;
        alter table finance_invoice_details drop column care_recipient_id;
        alter table finance_invoice_details drop column payer_id;
        """);
}
