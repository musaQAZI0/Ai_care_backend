using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20261115100000_AddPayrollCsvManifests")]
public sealed class AddPayrollCsvManifests:Migration
{
    protected override void Up(MigrationBuilder m)=>m.Sql("""
        create table finance_payroll_csv_templates(
          id uuid primary key,organization_id uuid not null,branch_id uuid not null,reference text not null,evidence_reference text not null,
          definition jsonb not null,request_snapshot jsonb not null,created_by uuid not null,created_at timestamptz not null default now(),
          approved_by uuid null,approved_at timestamptz null,
          check(length(trim(reference)) between 1 and 120),check(approved_by is null or (approved_by<>created_by and approved_at is not null and length(trim(evidence_reference))>0)));
        create table finance_payroll_csv_exports(
          id uuid primary key,organization_id uuid not null,branch_id uuid not null,batch_id uuid not null references finance_payroll_batches(id),
          template_id uuid not null references finance_payroll_csv_templates(id),revision integer not null check(revision>0),
          supersedes_id uuid null references finance_payroll_csv_exports(id),reason text not null,request_snapshot jsonb not null,
          file_name text not null,sha256 char(64) not null,source_sha256 char(64) not null,key_id text not null,protected_content bytea not null,
          row_count integer not null check(row_count>0),currency char(3) not null,total numeric(18,2) not null,worker_totals jsonb not null,
          line_snapshot jsonb not null,created_by uuid not null,created_at timestamptz not null default now(),
          unique(batch_id,revision),unique(supersedes_id),check((revision=1 and supersedes_id is null) or (revision>1 and supersedes_id is not null)));
        create table finance_payroll_csv_receipts(
          id uuid primary key,export_id uuid not null unique references finance_payroll_csv_exports(id),organization_id uuid not null,branch_id uuid not null,
          outcome text not null check(outcome in ('Imported','Rejected','PartiallyImported')),reference text not null,
          imported_rows integer not null check(imported_rows>=0),imported_total numeric(18,2) not null,worker_totals jsonb not null,
          request_snapshot jsonb not null,acknowledged_by uuid not null,acknowledged_at timestamptz not null default now(),
          check(length(trim(reference)) between 1 and 200));
        create index ix_payroll_csv_exports_scope on finance_payroll_csv_exports(organization_id,branch_id,batch_id,revision desc);
        create function guard_payroll_csv_history() returns trigger language plpgsql as $$begin
          if TG_OP='DELETE' then raise exception 'Payroll CSV evidence cannot be deleted' using errcode='23514';end if;
          if TG_TABLE_NAME='finance_payroll_csv_templates' and TG_OP='UPDATE' then
            if old.approved_by is not null or new.approved_by is null or new.approved_by=new.created_by or
               (to_jsonb(new)-'approved_by'-'approved_at') is distinct from (to_jsonb(old)-'approved_by'-'approved_at') then
              raise exception 'Create a new independently approved CSV template' using errcode='23514';end if;
          elsif TG_OP='UPDATE' then raise exception 'Payroll CSV evidence is immutable' using errcode='23514';end if;
          return new;
        end $$;
        create trigger payroll_csv_template_history before update or delete on finance_payroll_csv_templates for each row execute function guard_payroll_csv_history();
        create trigger payroll_csv_export_history before update or delete on finance_payroll_csv_exports for each row execute function guard_payroll_csv_history();
        create trigger payroll_csv_receipt_history before update or delete on finance_payroll_csv_receipts for each row execute function guard_payroll_csv_history();
        """);
    protected override void Down(MigrationBuilder m)=>throw new NotSupportedException("Preserve payroll export and import evidence; roll application code back without removing history.");
}
