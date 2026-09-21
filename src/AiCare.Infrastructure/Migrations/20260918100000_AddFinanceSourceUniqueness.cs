using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20260918100000_AddFinanceSourceUniqueness")]
public sealed class AddFinanceSourceUniqueness : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$
        BEGIN
            IF EXISTS (SELECT 1 FROM finance_invoice_lines WHERE visit_id IS NOT NULL
                       GROUP BY organization_id, visit_id HAVING count(*) > 1)
               OR EXISTS (SELECT 1 FROM finance_payroll_lines WHERE visit_id IS NOT NULL
                          GROUP BY organization_id, visit_id HAVING count(*) > 1) THEN
                RAISE EXCEPTION 'Finance source uniqueness requires reconciliation of existing duplicate visit claims.'
                    USING ERRCODE = '23505',
                          HINT = 'Group finance_invoice_lines and finance_payroll_lines by organization_id, visit_id. Reconcile financial records before retrying; do not automatically delete lines. Use isolated databases for integration tests.';
            END IF;
        END $$;
        create unique index ux_finance_invoice_visit on finance_invoice_lines(organization_id,visit_id) where visit_id is not null;
        create unique index ux_finance_payroll_visit on finance_payroll_lines(organization_id,visit_id) where visit_id is not null;
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop index ux_finance_invoice_visit;
        drop index ux_finance_payroll_visit;
        """);
}
