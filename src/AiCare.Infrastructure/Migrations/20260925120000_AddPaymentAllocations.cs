using Microsoft.EntityFrameworkCore.Infrastructure;using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))][Migration("20260925120000_AddPaymentAllocations")]
public sealed class AddPaymentAllocations:Migration{protected override void Up(MigrationBuilder m)=>m.Sql("""
create table finance_receipts(id uuid primary key,organization_id uuid not null,branch_id uuid not null,amount numeric(12,2) not null check(amount>0),currency char(3) not null,reference text not null,received_at timestamptz not null,received_by text not null,notes text not null default '',created_at timestamptz not null default now(),unique(organization_id,reference));
create table finance_payment_allocations(id uuid primary key,receipt_id uuid not null references finance_receipts(id),invoice_id uuid not null references "Invoices"("Id"),organization_id uuid not null,branch_id uuid not null,amount numeric(12,2) not null check(amount>0),allocated_at timestamptz not null default now(),allocated_by text not null,reversed_at timestamptz null,reversed_by text null,reversal_reason text not null default '');
create index ix_payment_allocations_invoice on finance_payment_allocations(organization_id,invoice_id) where reversed_at is null;
""");protected override void Down(MigrationBuilder m)=>m.Sql("drop table if exists finance_payment_allocations;drop table if exists finance_receipts;");}
