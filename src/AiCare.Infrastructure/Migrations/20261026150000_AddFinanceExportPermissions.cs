using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20261026150000_AddFinanceExportPermissions")]
public sealed class AddFinanceExportPermissions:Migration
{
 protected override void Up(MigrationBuilder m)=>m.Sql("""
 create table finance_export_permissions(
  id uuid primary key,organization_id uuid not null,branch_id uuid not null,user_id uuid not null references "AppUsers"("Id"),
  export_kind text not null check(export_kind in ('Ledger','Claims','Payroll','InvoiceDocuments')),
  allow_identifiers boolean not null,allow_rates boolean not null,valid_from date not null,valid_to date not null check(valid_to>=valid_from),
  reference text not null check(length(trim(reference))>0),created_by uuid not null,approved_by uuid null,approved_at timestamptz null,
  created_at timestamptz not null default now(),check(approved_by is null or (approved_by<>created_by and approved_by<>user_id and approved_at is not null)));
 create function govern_finance_export_permission() returns trigger language plpgsql as $$ begin
  if TG_OP='DELETE' then raise exception 'Export authority history is immutable' using errcode='23514';end if;
  if TG_OP='UPDATE' and (old.approved_by is not null or (to_jsonb(new)-'approved_by'-'approved_at') is distinct from (to_jsonb(old)-'approved_by'-'approved_at')) then raise exception 'Create a new export authority' using errcode='23514';end if;
  if not exists(select 1 from "Branches" where "Id"=new.branch_id and "OrganizationId"=new.organization_id)
   or not exists(select 1 from "AppUsers" where "Id"=new.user_id and "OrganizationId"=new.organization_id and ("BranchId"=new.branch_id or "BranchId" is null))
   then raise exception 'Export authority scope mismatch' using errcode='23514';end if;
  return new;end $$;
 create trigger govern_export_authority before insert or update or delete on finance_export_permissions for each row execute function govern_finance_export_permission();
 """);
 protected override void Down(MigrationBuilder m)=>throw new NotSupportedException("Retain finance authority history; use a forward migration.");
}
