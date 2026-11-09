using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261108190000_AddCustomInvoiceNumbering")]
public sealed class AddCustomInvoiceNumbering : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table finance_invoice_profiles
          add column custom_numbering_enabled boolean not null default false,
          add column invoice_number_prefix text not null default 'INV-' check(length(invoice_number_prefix) between 1 and 20 and invoice_number_prefix ~ '^[A-Z0-9/_-]+$'),
          add column invoice_number_start bigint not null default 1 check(invoice_number_start between 1 and 999999999999),
          add column invoice_number_padding int not null default 6 check(invoice_number_padding between 1 and 12);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Preserve consumed invoice sequences; recover forwards.");
}
