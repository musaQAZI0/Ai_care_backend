using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20260918100000_AddFinanceSourceUniqueness")]
public sealed class AddFinanceSourceUniqueness : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table finance_invoice_lines
            add column if not exists is_source_claim_anchor boolean not null default true;
        alter table finance_payroll_lines
            add column if not exists is_source_claim_anchor boolean not null default true;

        -- Preserve every historical financial line. For legacy duplicate visit claims,
        -- keep the earliest line as the uniqueness anchor and retain the other lines
        -- for explicit financial reconciliation. New lines use the true default and
        -- are therefore protected by the unique indexes below.
        with ranked as (
            select id,
                   row_number() over (
                       partition by organization_id, visit_id
                       order by created_at, id) as claim_rank
            from finance_invoice_lines
            where visit_id is not null
        )
        update finance_invoice_lines line
        set is_source_claim_anchor = ranked.claim_rank = 1
        from ranked
        where line.id = ranked.id;

        with ranked as (
            select id,
                   row_number() over (
                       partition by organization_id, visit_id
                       order by created_at, id) as claim_rank
            from finance_payroll_lines
            where visit_id is not null
        )
        update finance_payroll_lines line
        set is_source_claim_anchor = ranked.claim_rank = 1
        from ranked
        where line.id = ranked.id;

        create unique index ux_finance_invoice_visit on finance_invoice_lines(organization_id,visit_id)
            where visit_id is not null and is_source_claim_anchor;
        create unique index ux_finance_payroll_visit on finance_payroll_lines(organization_id,visit_id)
            where visit_id is not null and is_source_claim_anchor;
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop index if exists ux_finance_invoice_visit;
        drop index if exists ux_finance_payroll_visit;
        alter table finance_invoice_lines drop column if exists is_source_claim_anchor;
        alter table finance_payroll_lines drop column if exists is_source_claim_anchor;
        """);
}
