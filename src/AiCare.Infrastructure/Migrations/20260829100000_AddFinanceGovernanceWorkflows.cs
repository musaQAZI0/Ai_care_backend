using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260829100000_AddFinanceGovernanceWorkflows")]
public sealed class AddFinanceGovernanceWorkflows : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            create table if not exists finance_invoice_lines (
                id uuid primary key,
                invoice_id uuid not null,
                visit_id uuid null,
                service_user_id uuid not null,
                organization_id uuid not null,
                branch_id uuid not null,
                description text not null,
                quantity numeric(10,2) not null,
                unit_rate numeric(12,2) not null,
                amount numeric(12,2) not null,
                funding_source text not null default '',
                created_at timestamptz not null default now(),
                constraint fk_finance_invoice_lines_invoice foreign key (invoice_id) references "Invoices"("Id") on delete cascade,
                constraint fk_finance_invoice_lines_person foreign key (service_user_id) references "ServiceUsers"("Id") on delete cascade
            );
            create index if not exists ix_finance_invoice_lines_invoice on finance_invoice_lines(organization_id, invoice_id);
            create index if not exists ix_finance_invoice_lines_person on finance_invoice_lines(organization_id, service_user_id, created_at desc);

            create table if not exists finance_payments (
                id uuid primary key,
                invoice_id uuid not null,
                organization_id uuid not null,
                branch_id uuid not null,
                amount numeric(12,2) not null,
                reference text not null,
                received_at timestamptz not null default now(),
                received_by text not null default '',
                constraint fk_finance_payments_invoice foreign key (invoice_id) references "Invoices"("Id") on delete cascade
            );
            create index if not exists ix_finance_payments_invoice on finance_payments(organization_id, invoice_id, received_at desc);

            create table if not exists finance_payroll_lines (
                id uuid primary key,
                payroll_run_id uuid not null,
                visit_id uuid null,
                care_worker_id uuid not null,
                organization_id uuid not null,
                branch_id uuid not null,
                description text not null,
                payable_hours numeric(10,2) not null,
                hourly_rate numeric(12,2) not null,
                mileage_amount numeric(12,2) not null default 0,
                gross_pay numeric(12,2) not null,
                created_at timestamptz not null default now(),
                constraint fk_finance_payroll_lines_run foreign key (payroll_run_id) references "PayrollRuns"("Id") on delete cascade,
                constraint fk_finance_payroll_lines_worker foreign key (care_worker_id) references "CareWorkers"("Id") on delete cascade
            );
            create index if not exists ix_finance_payroll_lines_run on finance_payroll_lines(organization_id, payroll_run_id);
            create index if not exists ix_finance_payroll_lines_worker on finance_payroll_lines(organization_id, care_worker_id, created_at desc);

            create table if not exists finance_funding_reconciliations (
                id uuid primary key,
                service_user_id uuid not null,
                organization_id uuid not null,
                branch_id uuid not null,
                period_start timestamptz not null,
                period_end timestamptz not null,
                authorized_hours numeric(10,2) not null default 0,
                delivered_hours numeric(10,2) not null default 0,
                invoiced_amount numeric(12,2) not null default 0,
                variance_hours numeric(10,2) not null default 0,
                status text not null default 'Open',
                notes text not null default '',
                created_by text not null default '',
                created_at timestamptz not null default now(),
                constraint fk_finance_funding_reconciliations_person foreign key (service_user_id) references "ServiceUsers"("Id") on delete cascade
            );
            create index if not exists ix_finance_reconciliations_queue on finance_funding_reconciliations(organization_id, branch_id, status, period_end desc);
        """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            drop table if exists finance_funding_reconciliations;
            drop table if exists finance_payroll_lines;
            drop table if exists finance_payments;
            drop table if exists finance_invoice_lines;
        """);
    }
}
