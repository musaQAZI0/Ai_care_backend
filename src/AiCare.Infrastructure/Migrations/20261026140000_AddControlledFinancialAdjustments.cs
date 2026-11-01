using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20261026140000_AddControlledFinancialAdjustments")]
public sealed class AddControlledFinancialAdjustments:Migration
{
 protected override void Up(MigrationBuilder m)=>m.Sql("""
  alter table "Invoices" drop constraint ck_invoice_governed_status;
  alter table "Invoices" add constraint ck_invoice_governed_status check("Status" in ('Generated','Reviewed','Approved','Issued','Part paid','Paid','Overdue','Credited','Void','Written off'));
  create table finance_adjustment_authorities(
   id uuid primary key,organization_id uuid not null,branch_id uuid not null,reviewer_id uuid not null references "AppUsers"("Id"),
   currency char(3) not null check(currency ~ '^[A-Z]{3}$'),maximum_amount numeric(12,2) not null check(maximum_amount>0),
   valid_from date not null,valid_to date not null check(valid_to>=valid_from),reference text not null check(length(trim(reference))>0),
   created_by uuid not null,approved_by uuid null,approved_at timestamptz null,created_at timestamptz not null default now(),
   check(approved_by is null or (approved_by<>created_by and approved_by<>reviewer_id and approved_at is not null)));
  create table finance_adjustment_requests(
   id uuid primary key,invoice_id uuid not null references "Invoices"("Id"),organization_id uuid not null,branch_id uuid not null,
   kind text not null check(kind in ('Credit','Refund','WriteOff')),amount numeric(12,2) not null check(amount>0),currency char(3) not null,
   reason text not null check(length(trim(reason))>0),reference text not null check(length(trim(reference))>0),
   net_amount numeric(12,2) null check(net_amount>=0),tax_amount numeric(12,2) null check(tax_amount>=0),service_date date null,
   correction_reference text not null default '',request_snapshot jsonb not null,status text not null default 'Submitted' check(status in ('Submitted','Rejected','Posted')),
   submitted_by uuid not null,submitted_at timestamptz not null default now(),reviewed_by uuid null,reviewed_at timestamptz null,review_reason text not null default '',
   authority_id uuid null references finance_adjustment_authorities(id),posted_record_id uuid null,
   check(kind<>'Credit' or (net_amount is not null and tax_amount is not null and service_date is not null and amount=net_amount+tax_amount)),
   check(status='Submitted' or (reviewed_by is not null and reviewed_by<>submitted_by and reviewed_at is not null)),
   check(status<>'Posted' or (posted_record_id is not null and authority_id is not null)),unique(organization_id,invoice_id,kind,reference));
  alter table finance_credit_notes add column adjustment_request_id uuid null unique references finance_adjustment_requests(id),
   add column net_amount numeric(12,2) null,add column tax_amount numeric(12,2) null,add column service_date date null;
  alter table finance_refunds add column adjustment_request_id uuid null unique references finance_adjustment_requests(id);
  create table finance_write_offs(id uuid primary key,invoice_id uuid not null references "Invoices"("Id"),organization_id uuid not null,branch_id uuid not null,
   amount numeric(12,2) not null check(amount>0),adjustment_request_id uuid not null unique references finance_adjustment_requests(id),posted_at timestamptz not null default now());
  create table finance_invoice_disputes(id uuid primary key,invoice_id uuid not null references "Invoices"("Id"),organization_id uuid not null,branch_id uuid not null,
   amount numeric(12,2) not null check(amount>0),reason text not null,evidence_reference text not null,status text not null default 'Open' check(status in ('Open','Resolved')),
   created_by uuid not null,created_at timestamptz not null default now(),resolved_by uuid null,resolved_at timestamptz null,resolution text not null default '',
   check(status<>'Resolved' or (resolved_by is not null and resolved_at is not null and length(trim(resolution))>0)));
  create table finance_adjustment_history(id uuid primary key default gen_random_uuid(),entity_id uuid not null,entity_type text not null,snapshot jsonb not null,recorded_at timestamptz not null default now());
  create trigger finance_adjustment_history_immutable before update or delete on finance_adjustment_history for each row execute function protect_timesheet_history();
  create function govern_adjustment_authority() returns trigger language plpgsql as $$ begin
   if TG_OP='DELETE' then raise exception 'Authority history cannot be deleted' using errcode='23514'; end if;
   if TG_OP='UPDATE' and (old.approved_by is not null or (to_jsonb(new)-'approved_by'-'approved_at') is distinct from (to_jsonb(old)-'approved_by'-'approved_at')) then raise exception 'Authority content is immutable' using errcode='23514'; end if;
   if not exists(select 1 from "AppUsers" u join "Branches" b on b."Id"=new.branch_id and b."OrganizationId"=new.organization_id where u."Id"=new.reviewer_id and u."OrganizationId"=new.organization_id and u."Role" in ('Administrator','BackOffice') and u."IsActive") then raise exception 'Authority reviewer/branch mismatch' using errcode='23514'; end if;
   insert into finance_adjustment_history(entity_id,entity_type,snapshot) values(new.id,'Authority',to_jsonb(new));return new;
  end $$;
  create trigger finance_authority_guard before insert or update or delete on finance_adjustment_authorities for each row execute function govern_adjustment_authority();
  create function govern_adjustment_request() returns trigger language plpgsql as $$ begin
   if TG_OP='DELETE' then raise exception 'Adjustment history cannot be deleted' using errcode='23514'; end if;
   perform 1 from "Invoices" i join finance_invoice_details d on d.invoice_id=i."Id" and d.organization_id=i."OrganizationId" where i."Id"=new.invoice_id and i."OrganizationId"=new.organization_id and i."BranchId"=new.branch_id and d.currency=new.currency for update of i;
   if not found then raise exception 'Adjustment invoice scope/currency mismatch' using errcode='23514'; end if;
   if TG_OP='UPDATE' and (old.status<>'Submitted' or (to_jsonb(new)-'status'-'reviewed_by'-'reviewed_at'-'review_reason'-'authority_id'-'posted_record_id') is distinct from (to_jsonb(old)-'status'-'reviewed_by'-'reviewed_at'-'review_reason'-'authority_id'-'posted_record_id')) then raise exception 'Submitted adjustment content is immutable' using errcode='23514'; end if;
   if new.status='Posted' and not exists(select 1 from finance_adjustment_authorities a where a.id=new.authority_id and a.organization_id=new.organization_id and a.branch_id=new.branch_id and a.currency=new.currency and a.reviewer_id=new.reviewed_by and a.approved_by is not null and new.amount<=a.maximum_amount and (now() at time zone 'Europe/London')::date between a.valid_from and a.valid_to) then raise exception 'Approved adjustment authority is required' using errcode='23514'; end if;
   insert into finance_adjustment_history(entity_id,entity_type,snapshot) values(new.id,'Adjustment',to_jsonb(new));return new;
  end $$;
  create trigger finance_adjustment_request_guard before insert or update or delete on finance_adjustment_requests for each row execute function govern_adjustment_request();
  create function govern_controlled_posting() returns trigger language plpgsql as $$
  declare r finance_adjustment_requests%rowtype;credits numeric;refunds numeric;cash numeric;written numeric;gross numeric;net numeric;tax numeric;
  begin
   if TG_OP<>'INSERT' then raise exception 'Posted financial history is immutable' using errcode='23514'; end if;
   if new.adjustment_request_id is null then raise exception 'Use the controlled adjustment request workflow' using errcode='23514'; end if;
   select * into r from finance_adjustment_requests where id=new.adjustment_request_id;
   if not found or r.status<>'Posted' or r.posted_record_id<>new.id or r.invoice_id<>new.invoice_id or r.organization_id<>new.organization_id or r.branch_id<>new.branch_id or r.amount<>new.amount
    or (TG_TABLE_NAME='finance_credit_notes' and r.kind<>'Credit') or (TG_TABLE_NAME='finance_refunds' and r.kind<>'Refund') or (TG_TABLE_NAME='finance_write_offs' and r.kind<>'WriteOff') then raise exception 'Financial posting request mismatch' using errcode='23514'; end if;
   select i."Amount",d.net_amount,d.vat_amount into gross,net,tax from "Invoices" i join finance_invoice_details d on d.invoice_id=i."Id" where i."Id"=new.invoice_id and i."OrganizationId"=new.organization_id for update of i;
   select coalesce(sum(amount),0) into credits from finance_credit_notes where invoice_id=new.invoice_id and organization_id=new.organization_id and status='Issued';
   select coalesce(sum(amount),0) into refunds from finance_refunds where invoice_id=new.invoice_id and organization_id=new.organization_id;
   select coalesce(sum(amount),0) into written from finance_write_offs where invoice_id=new.invoice_id and organization_id=new.organization_id;
   select coalesce((select sum(amount) from finance_payments where invoice_id=new.invoice_id and organization_id=new.organization_id),0)+coalesce((select sum(amount) from finance_payment_allocations where invoice_id=new.invoice_id and organization_id=new.organization_id and reversed_at is null),0)-refunds into cash;
   if r.kind='Credit' then
    if credits+new.amount+written>gross or r.net_amount>net-coalesce((select sum(net_amount) from finance_credit_notes where invoice_id=new.invoice_id and organization_id=new.organization_id and status='Issued'),0) or r.tax_amount>tax-coalesce((select sum(tax_amount) from finance_credit_notes where invoice_id=new.invoice_id and organization_id=new.organization_id and status='Issued'),0) then raise exception 'Credit exceeds remaining invoice net/tax/gross' using errcode='23514'; end if;
    if new.net_amount is distinct from r.net_amount or new.tax_amount is distinct from r.tax_amount or new.service_date is distinct from r.service_date then raise exception 'Credit attribution differs from the approved request' using errcode='23514'; end if;
   elsif r.kind='Refund' and new.amount>cash then raise exception 'Refund exceeds net received cash' using errcode='23514';
   elsif r.kind='WriteOff' and new.amount>gross-credits-written-cash then raise exception 'Write-off exceeds outstanding debt' using errcode='23514'; end if;return new;
  end $$;
  create trigger finance_controlled_credit before insert or update or delete on finance_credit_notes for each row execute function govern_controlled_posting();
  create trigger finance_controlled_refund before insert or update or delete on finance_refunds for each row execute function govern_controlled_posting();
  create trigger finance_controlled_writeoff before insert or update or delete on finance_write_offs for each row execute function govern_controlled_posting();
  create function verify_adjustment_posted() returns trigger language plpgsql as $$ begin
   if new.status='Posted' and not (
    (new.kind='Credit' and exists(select 1 from finance_credit_notes where id=new.posted_record_id and adjustment_request_id=new.id and amount=new.amount))
    or (new.kind='Refund' and exists(select 1 from finance_refunds where id=new.posted_record_id and adjustment_request_id=new.id and amount=new.amount))
    or (new.kind='WriteOff' and exists(select 1 from finance_write_offs where id=new.posted_record_id and adjustment_request_id=new.id and amount=new.amount))) then raise exception 'Approved adjustment and immutable posting must commit together' using errcode='23514'; end if;return null;
  end $$;
  create constraint trigger finance_adjustment_posted after insert or update on finance_adjustment_requests deferrable initially deferred for each row execute function verify_adjustment_posted();
  create function govern_invoice_dispute() returns trigger language plpgsql as $$ begin
   if TG_OP='DELETE' then raise exception 'Dispute history cannot be deleted' using errcode='23514'; end if;
   perform 1 from "Invoices" where "Id"=new.invoice_id and "OrganizationId"=new.organization_id and "BranchId"=new.branch_id and "Status" in ('Issued','Part paid','Paid','Overdue','Credited','Written off') and "Amount">=new.amount for update;
   if not found then raise exception 'Dispute invoice scope/amount mismatch' using errcode='23514'; end if;
   if TG_OP='UPDATE' and (old.status<>'Open' or (to_jsonb(new)-'status'-'resolved_by'-'resolved_at'-'resolution') is distinct from (to_jsonb(old)-'status'-'resolved_by'-'resolved_at'-'resolution')) then raise exception 'Dispute source is immutable' using errcode='23514'; end if;
   insert into finance_adjustment_history(entity_id,entity_type,snapshot) values(new.id,'Dispute',to_jsonb(new));return new;
  end $$;
  create trigger finance_dispute_guard before insert or update or delete on finance_invoice_disputes for each row execute function govern_invoice_dispute();

  create or replace view finance_invoice_ledger as
  select x.invoice_id,x.organization_id,x.branch_id,x.person_id,x.status,x.invoice_number,x.invoice_date,x.due_date,x.currency,x.net_amount,x.vat_amount,x.gross_amount,x.direct_payments,x.receipt_allocations,x.refunds,x.credits,x.net_cash,x.is_issued,x.gross_amount-x.credits-x.write_offs-x.net_cash balance,greatest(x.gross_amount-x.credits-x.write_offs-x.net_cash,0) outstanding,greatest(-(x.gross_amount-x.credits-x.write_offs-x.net_cash),0) customer_credit,x.write_offs from (
   select i."Id" invoice_id,i."OrganizationId" organization_id,i."BranchId" branch_id,i."ServiceUserId" person_id,i."Status" status,
    d.invoice_number,d.invoice_date,d.due_date,trim(d.currency) currency,d.net_amount,d.vat_amount,d.gross_amount,
    coalesce(p.direct,0) direct_payments,coalesce(a.receipts,0) receipt_allocations,coalesce(r.refunds,0) refunds,coalesce(c.credits,0) credits,coalesce(w.written,0) write_offs,
    coalesce(p.direct,0)+coalesce(a.receipts,0)-coalesce(r.refunds,0) net_cash,i."Status" in ('Issued','Part paid','Paid','Overdue','Credited','Written off') is_issued
   from "Invoices" i join finance_invoice_details d on d.invoice_id=i."Id" and d.organization_id=i."OrganizationId" and d.branch_id=i."BranchId"
   left join lateral(select sum(amount) direct from finance_payments where invoice_id=i."Id" and organization_id=i."OrganizationId")p on true
   left join lateral(select sum(amount) receipts from finance_payment_allocations where invoice_id=i."Id" and organization_id=i."OrganizationId" and reversed_at is null)a on true
   left join lateral(select sum(amount) refunds from finance_refunds where invoice_id=i."Id" and organization_id=i."OrganizationId")r on true
   left join lateral(select sum(amount) credits from finance_credit_notes where invoice_id=i."Id" and organization_id=i."OrganizationId" and status='Issued')c on true
   left join lateral(select sum(amount) written from finance_write_offs where invoice_id=i."Id" and organization_id=i."OrganizationId")w on true
  )x;
  """);
 protected override void Down(MigrationBuilder m)=>throw new NotSupportedException("Adjustment history requires forward recovery.");
}
