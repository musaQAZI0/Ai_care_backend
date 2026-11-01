using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20261026160000_AddPayrollReadyBatches")]
public sealed class AddPayrollReadyBatches:Migration
{
 protected override void Up(MigrationBuilder m)=>m.Sql("""
 create table finance_payroll_contracts(
  id uuid primary key,organization_id uuid not null,branch_id uuid not null,reference text not null check(length(trim(reference))>0),
  currency char(3) not null check(currency ~ '^[A-Z]{3}$'),valid_from date not null,valid_to date not null check(valid_to>=valid_from),
  worker_mappings jsonb not null check(jsonb_typeof(worker_mappings)='object'),pay_codes jsonb not null check(jsonb_typeof(pay_codes)='object'),request_snapshot jsonb not null,
  created_by uuid not null,approved_by uuid null,approved_at timestamptz null,created_at timestamptz not null default now(),
  check(approved_by is null or (approved_by<>created_by and approved_at is not null)));
 create table finance_payroll_extras(
  id uuid primary key,organization_id uuid not null,branch_id uuid not null,care_worker_id uuid not null references "CareWorkers"("Id"),
  kind text not null check(kind in ('Leave','Adjustment')),service_date date not null,currency char(3) not null check(currency ~ '^[A-Z]{3}$'),
  quantity numeric(12,4) not null,unit_rate numeric(12,4) not null,amount numeric(12,2) not null check(amount<>0),rounding_mode text not null check(rounding_mode in ('AwayFromZero','ToEven')),check(amount=finance_policy_round(quantity*unit_rate,2,rounding_mode)),
  absence_id uuid null references worker_absences(id),correction_line_id uuid null,reference text not null check(length(trim(reference))>0),reason text not null check(length(trim(reason))>0),request_snapshot jsonb not null,
  created_by uuid not null,approved_by uuid null,approved_at timestamptz null,created_at timestamptz not null default now(),
  check(kind<>'Leave' or (absence_id is not null and quantity>0 and unit_rate>=0 and amount>0)),
  check(kind<>'Adjustment' or correction_line_id is not null),check(approved_by is null or (approved_by<>created_by and approved_at is not null)),unique(organization_id,care_worker_id,reference));
 create table finance_payroll_batches(
  id uuid primary key,organization_id uuid not null,branch_id uuid not null,contract_id uuid not null references finance_payroll_contracts(id),
  period_from date not null,period_to date not null check(period_to>=period_from),reference text not null check(length(trim(reference))>0),
  request_snapshot jsonb not null,status text not null default 'Submitted' check(status in ('Submitted','Approved','Rejected','Paid')),
  created_by uuid not null,created_at timestamptz not null default now(),reviewed_by uuid null,reviewed_at timestamptz null,review_reason text null,
  paid_by uuid null,paid_at timestamptz null,payment_reference text null,
  unique(organization_id,reference),unique(organization_id,payment_reference),check(reviewed_by is null or reviewed_by<>created_by));
 create table finance_payroll_batch_lines(
  id uuid primary key,batch_id uuid not null references finance_payroll_batches(id),organization_id uuid not null,branch_id uuid not null,
  source_type text not null check(source_type in ('Worked','Travel','TimesheetMileage','ClaimMileage','ClaimExpense','Leave','Adjustment')),
  source_id uuid not null,care_worker_id uuid not null references "CareWorkers"("Id"),worker_reference text not null,pay_code text not null,
  service_date date not null,currency char(3) not null,quantity numeric(16,8) not null,unit_rate numeric(16,8) not null,amount numeric(12,2) not null,
  released boolean not null default false);
 create unique index ux_finance_payroll_source on finance_payroll_batch_lines(source_type,source_id) where not released;
 alter table finance_payroll_extras add constraint fk_payroll_correction_source foreign key(correction_line_id) references finance_payroll_batch_lines(id);
 create function govern_payroll_configuration() returns trigger language plpgsql as $$ declare item record;begin
  if TG_OP='DELETE' then raise exception 'Payroll configuration history is immutable' using errcode='23514';end if;
  if TG_OP='UPDATE' and (old.approved_by is not null or (to_jsonb(new)-'approved_by'-'approved_at') is distinct from (to_jsonb(old)-'approved_by'-'approved_at')) then raise exception 'Create a replacement payroll configuration' using errcode='23514';end if;
  perform 1 from "Branches" where "Id"=new.branch_id and "OrganizationId"=new.organization_id for update;if not found then raise exception 'Payroll branch mismatch' using errcode='23514';end if;
  if TG_TABLE_NAME='finance_payroll_contracts' then
   for item in select * from jsonb_each_text(new.worker_mappings) loop
    if length(trim(item.value))=0 or not exists(select 1 from "CareWorkers" where "Id"::text=item.key and "OrganizationId"=new.organization_id and "BranchId"=new.branch_id) then raise exception 'Worker payroll mapping mismatch' using errcode='23514';end if;
   end loop;
   if (select count(*) from jsonb_each_text(new.worker_mappings))<>(select count(distinct value) from jsonb_each_text(new.worker_mappings)) then raise exception 'Duplicate worker payroll reference' using errcode='23514';end if;
   for item in select unnest(array['Worked','Travel','TimesheetMileage','ClaimMileage','ClaimExpense','Leave','Adjustment']) key loop
    if length(trim(coalesce(new.pay_codes->>item.key,'')))=0 then raise exception 'Every payroll component requires an explicit pay code' using errcode='23514';end if;
   end loop;
   if new.approved_by is not null and exists(select 1 from finance_payroll_contracts where id<>new.id and organization_id=new.organization_id and branch_id=new.branch_id and approved_by is not null and daterange(valid_from,valid_to,'[]')&&daterange(new.valid_from,new.valid_to,'[]')) then raise exception 'Approved payroll contracts overlap' using errcode='23514';end if;
  else
   if not exists(select 1 from "CareWorkers" where "Id"=new.care_worker_id and "OrganizationId"=new.organization_id and "BranchId"=new.branch_id) then raise exception 'Payroll extra worker mismatch' using errcode='23514';end if;
   if new.kind='Leave' and (not exists(select 1 from worker_absences where id=new.absence_id and organization_id=new.organization_id and branch_id=new.branch_id and care_worker_id=new.care_worker_id and status='Approved' and new.service_date between (starts_at at time zone 'Europe/London')::date and (ends_at at time zone 'Europe/London')::date) or exists(select 1 from finance_payroll_extras where id<>new.id and absence_id=new.absence_id and service_date=new.service_date)) then raise exception 'Leave source is missing, unapproved or already mapped for this day' using errcode='23514';end if;
   if new.kind='Adjustment' and not exists(select 1 from finance_payroll_batch_lines l join finance_payroll_batches b on b.id=l.batch_id where l.id=new.correction_line_id and l.organization_id=new.organization_id and l.branch_id=new.branch_id and l.care_worker_id=new.care_worker_id and l.currency=new.currency and b.status='Paid') then raise exception 'Adjustment requires an original confirmed payroll source' using errcode='23514';end if;
   if new.approved_by is not null and exists(select 1 from "AppUsers" where "Id"=new.approved_by and "CareWorkerId"=new.care_worker_id) then raise exception 'Payroll beneficiary cannot approve their own extra' using errcode='23514';end if;
  end if;return new;end $$;
 create trigger payroll_contract_guard before insert or update or delete on finance_payroll_contracts for each row execute function govern_payroll_configuration();
 create trigger payroll_extra_guard before insert or update or delete on finance_payroll_extras for each row execute function govern_payroll_configuration();
 create function govern_payroll_batch() returns trigger language plpgsql as $$ declare contract finance_payroll_contracts%rowtype;begin
  if TG_OP='DELETE' then raise exception 'Payroll batches cannot be deleted' using errcode='23514';end if;
  select * into contract from finance_payroll_contracts where id=new.contract_id;
  if not found or contract.approved_by is null or contract.organization_id<>new.organization_id or contract.branch_id<>new.branch_id or new.period_from<contract.valid_from or new.period_to>contract.valid_to then raise exception 'Payroll batch requires a covering approved contract' using errcode='23514';end if;
  if TG_OP='INSERT' then if new.status<>'Submitted' then raise exception 'Payroll must be submitted first' using errcode='23514';end if;return new;end if;
  if (to_jsonb(new)-'status'-'reviewed_by'-'reviewed_at'-'review_reason'-'paid_by'-'paid_at'-'payment_reference') is distinct from (to_jsonb(old)-'status'-'reviewed_by'-'reviewed_at'-'review_reason'-'paid_by'-'paid_at'-'payment_reference') then raise exception 'Payroll batch source is immutable' using errcode='23514';end if;
  if old.status='Submitted' and new.status in ('Approved','Rejected') then
   if new.reviewed_by is null or new.reviewed_at is null or length(trim(coalesce(new.review_reason,'')))=0 or new.reviewed_by=new.created_by or exists(select 1 from finance_payroll_batch_lines l join "AppUsers" u on u."CareWorkerId"=l.care_worker_id where l.batch_id=new.id and u."Id"=new.reviewed_by) then raise exception 'Independent payroll approval required' using errcode='23514';end if;
   if new.paid_by is not null or new.payment_reference is not null then raise exception 'Approval is not payment' using errcode='23514';end if;
   if new.status='Approved' and not exists(select 1 from finance_payroll_batch_lines where batch_id=new.id) then raise exception 'Empty payroll batch' using errcode='23514';end if;
  elsif old.status='Approved' and new.status='Paid' then
   if new.paid_by is null or new.paid_at is null or length(trim(coalesce(new.payment_reference,'')))=0 or (to_jsonb(new)-'status'-'paid_by'-'paid_at'-'payment_reference') is distinct from (to_jsonb(old)-'status'-'paid_by'-'paid_at'-'payment_reference') then raise exception 'Confirmation must preserve approved payroll' using errcode='23514';end if;
  else raise exception 'Payroll transition is not permitted' using errcode='23514';end if;return new;end $$;
 create trigger payroll_batch_guard before insert or update or delete on finance_payroll_batches for each row execute function govern_payroll_batch();
 create function govern_payroll_batch_line() returns trigger language plpgsql as $$ declare b finance_payroll_batches%rowtype;c finance_payroll_contracts%rowtype;expected numeric;worker uuid;day date;begin
  if TG_OP='DELETE' then raise exception 'Payroll lines cannot be deleted' using errcode='23514';end if;
  select * into b from finance_payroll_batches where id=new.batch_id for update;
  if TG_OP='UPDATE' then if b.status<>'Rejected' or old.released or not new.released or (to_jsonb(new)-'released') is distinct from (to_jsonb(old)-'released') then raise exception 'Payroll lines are immutable' using errcode='23514';end if;return new;end if;
  select * into c from finance_payroll_contracts where id=b.contract_id;
  if b.status<>'Submitted' or new.released or new.organization_id<>b.organization_id or new.branch_id<>b.branch_id or new.currency<>c.currency or new.worker_reference<>coalesce(c.worker_mappings->>new.care_worker_id::text,'') or length(trim(new.worker_reference))=0 or new.pay_code<>coalesce(c.pay_codes->>new.source_type,'') or length(trim(new.pay_code))=0 or new.service_date not between b.period_from and b.period_to then raise exception 'Payroll line contract mismatch' using errcode='23514';end if;
  if new.source_type in ('ClaimMileage','ClaimExpense') then
   select amount,care_worker_id,claim_date into expected,worker,day from finance_workforce_claims where id=new.source_id and organization_id=b.organization_id and branch_id=b.branch_id and status='Approved' and currency=c.currency and claim_type=case when new.source_type='ClaimMileage' then 'Mileage' else 'Expense' end and quantity=new.quantity and unit_rate=new.unit_rate for update;
  elsif new.source_type in ('Leave','Adjustment') then
   select amount,care_worker_id,service_date into expected,worker,day from finance_payroll_extras where id=new.source_id and organization_id=b.organization_id and branch_id=b.branch_id and kind=new.source_type and approved_by is not null and currency=c.currency and quantity=new.quantity and unit_rate=new.unit_rate for update;
  else
   select case new.source_type when 'Worked' then l.gross_pay-l.travel_amount-l.mileage_amount when 'Travel' then l.travel_amount else l.mileage_amount end,l.care_worker_id,(v."StartsAt" at time zone 'Europe/London')::date into expected,worker,day
    from finance_payroll_lines l join timesheet_entries e on e.id=l.timesheet_entry_id join "Visits" v on v."Id"=l.visit_id join timesheet_periods p on p.id=e.period_id join "PayrollRuns" r on r."Id"=l.payroll_run_id
    where l.id=new.source_id and l.organization_id=b.organization_id and l.branch_id=b.branch_id and p.status='Locked' and e.status='Approved' and r."Status"='Approved'
     and length(trim(coalesce(b.request_snapshot->>'PayrollCurrencyReference','')))>0
     and new.quantity=round(case new.source_type when 'Worked' then e.payable_minutes/60 when 'Travel' then e.travel_minutes/60 else e.mileage end,8)
     and new.unit_rate=case new.source_type when 'Worked' then e.hourly_rate when 'Travel' then p.travel_rate else p.mileage_rate end for update of l;
  end if;
  if expected is null or new.amount<>expected or new.care_worker_id<>worker or new.service_date<>day then raise exception 'Payroll source is missing, unapproved or mismatched' using errcode='23514';end if;return new;end $$;
 create trigger payroll_batch_line_guard before insert or update or delete on finance_payroll_batch_lines for each row execute function govern_payroll_batch_line();
 create function post_payroll_batch() returns trigger language plpgsql as $$begin
  if new.status='Rejected' then update finance_payroll_batch_lines set released=true where batch_id=new.id;
  elsif new.status='Paid' then update finance_workforce_claims set status='Paid' where id in (select source_id from finance_payroll_batch_lines where batch_id=new.id and source_type in ('ClaimMileage','ClaimExpense')) and status='Approved';end if;return new;end $$;
 create trigger payroll_batch_posted after update on finance_payroll_batches for each row execute function post_payroll_batch();
 do $$declare definition text;needle text:='if TG_OP=''DELETE'' then raise exception ''Claims cannot be deleted'' using errcode=''23514''; end if;';begin
  select pg_get_functiondef('govern_policy_claim()'::regprocedure) into definition;
  if position(needle in definition)=0 then raise exception 'Claim guard changed; explicit migration review required';end if;
  definition:=replace(definition,needle,needle||$guard$
   if TG_OP='INSERT' and new.status<>'Submitted' then raise exception 'Claims must start submitted' using errcode='23514';end if;
   if TG_OP='UPDATE' and new.status='Paid' and old.status<>'Approved' then raise exception 'Only reserved approved claims can be paid' using errcode='23514';end if;
   if TG_OP='UPDATE' and old.status='Approved' and new.status='Paid' and (to_jsonb(new)-'status')=(to_jsonb(old)-'status') and exists(select 1 from finance_payroll_batch_lines l join finance_payroll_batches b on b.id=l.batch_id where l.source_id=new.id and l.source_type in ('ClaimMileage','ClaimExpense') and not l.released and b.status='Paid' and b.organization_id=new.organization_id and b.branch_id=new.branch_id) then return new;end if;
  $guard$);execute definition;end $$;
 create function protect_exported_payroll_source() returns trigger language plpgsql as $$ begin
  if TG_TABLE_NAME='finance_payroll_lines' then
   if exists(select 1 from finance_payroll_batch_lines where source_id=old.id and source_type in ('Worked','Travel','TimesheetMileage') and not released) then raise exception 'Payroll source is reserved; create a controlled correction' using errcode='23514';end if;
  else
   if exists(select 1 from finance_payroll_lines l join finance_payroll_batch_lines b on b.source_id=l.id and b.source_type in ('Worked','Travel','TimesheetMileage') and not b.released where l.payroll_run_id=old."Id") then raise exception 'Payroll run is reserved; create a controlled correction' using errcode='23514';end if;
  end if;if TG_OP='DELETE' then return old;end if;return new;end $$;
 create trigger exported_payroll_source_guard before update or delete on finance_payroll_lines for each row execute function protect_exported_payroll_source();
 create trigger exported_payroll_run_guard before update or delete on "PayrollRuns" for each row execute function protect_exported_payroll_source();
 """);
 protected override void Down(MigrationBuilder m)=>throw new NotSupportedException("Preserve payroll and reimbursement history; recover forwards.");
}
