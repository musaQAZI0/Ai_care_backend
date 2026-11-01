using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20261026110000_AddClaimPolicyIntegrity")]
public sealed class AddClaimPolicyIntegrity:Migration
{
 protected override void Up(MigrationBuilder m)=>m.Sql("""
  create table finance_claim_policies(
   id uuid primary key,organization_id uuid not null,branch_id uuid not null,reference text not null,
   claim_type text not null check(claim_type in ('Mileage','Expense')),category text not null,
   valid_from date not null,valid_to date not null check(valid_to>=valid_from),currency char(3) not null check(currency ~ '^[A-Z]{3}$'),
   unit_rate numeric(12,4) not null check(unit_rate>0),maximum_amount numeric(12,2) not null check(maximum_amount>0),
   evidence_required boolean not null,visit_required boolean not null,rounding_mode text not null check(rounding_mode in ('AwayFromZero','ToEven')),
   created_by uuid not null,approved_by uuid null,created_at timestamptz not null default now(),approved_at timestamptz null,
   check(approved_by is null or approved_by<>created_by));
  alter table finance_workforce_claims add column policy_id uuid null references finance_claim_policies(id),
   add column source_reference text null,add column currency char(3) null,
   add column submitted_by_user_id uuid null,add column reviewed_by_user_id uuid null;
  create unique index ux_claim_source_reference on finance_workforce_claims(organization_id,care_worker_id,claim_type,source_reference) where source_reference is not null;
  create function govern_claim_policy() returns trigger language plpgsql as $$ begin
   if TG_OP='DELETE' then raise exception 'Claim policy history cannot be deleted' using errcode='23514'; end if;
   if TG_OP='UPDATE' and (old.approved_by is not null or (to_jsonb(new)-'approved_by'-'approved_at') is distinct from (to_jsonb(old)-'approved_by'-'approved_at')) then
     raise exception 'Create a new claim policy version' using errcode='23514'; end if;
   if not exists(select 1 from "Branches" where "Id"=new.branch_id and "OrganizationId"=new.organization_id) then raise exception 'Policy branch mismatch' using errcode='23514'; end if;
   return new;
  end $$;
  create trigger finance_claim_policy_guard before insert or update or delete on finance_claim_policies for each row execute function govern_claim_policy();
  create function govern_policy_claim() returns trigger language plpgsql as $$
  declare policy finance_claim_policies%rowtype;
  begin
   if TG_OP='DELETE' then raise exception 'Claims cannot be deleted' using errcode='23514'; end if;
   if TG_OP='UPDATE' and old.status in ('Approved','Paid','Rejected') then raise exception 'Reviewed claims require controlled corrections' using errcode='23514'; end if;
   if TG_OP='UPDATE' and (to_jsonb(new)-'status'-'reviewed_by'-'reviewed_at'-'review_reason'-'reviewed_by_user_id') is distinct from (to_jsonb(old)-'status'-'reviewed_by'-'reviewed_at'-'review_reason'-'reviewed_by_user_id') then raise exception 'Submitted claim details are immutable' using errcode='23514'; end if;
   if new.policy_id is null then
     if TG_OP='INSERT' or new.status='Approved' then raise exception 'An approved claim policy is required' using errcode='23514'; end if;
     return new;
   end if;
   select * into policy from finance_claim_policies where id=new.policy_id;
   if not found or policy.approved_by is null or policy.organization_id<>new.organization_id or policy.branch_id<>new.branch_id
     or policy.claim_type<>new.claim_type or policy.category<>new.category or new.claim_date not between policy.valid_from and policy.valid_to
     or policy.currency<>new.currency or new.unit_rate<>policy.unit_rate or new.amount<>finance_policy_round(new.quantity*policy.unit_rate,2,policy.rounding_mode)
     or new.amount>policy.maximum_amount or new.amount<=0 or length(trim(coalesce(new.source_reference,'')))=0
     or (policy.evidence_required and length(trim(new.evidence_reference))=0) or (policy.visit_required and new.visit_id is null)
     then raise exception 'Claim does not match the approved policy' using errcode='23514'; end if;
   perform 1 from "CareWorkers" where "Id"=new.care_worker_id and "OrganizationId"=new.organization_id and "BranchId"=new.branch_id for update;
   if not found then raise exception 'Claim worker scope mismatch' using errcode='23514'; end if;
   if new.visit_id is not null and not exists(select 1 from "Visits" where "Id"=new.visit_id and "OrganizationId"=new.organization_id and "BranchId"=new.branch_id and "CareWorkerId"=new.care_worker_id and ("StartsAt" at time zone 'Europe/London')::date=new.claim_date) then raise exception 'Claim visit scope/date mismatch' using errcode='23514'; end if;
   if new.claim_type='Mileage' and new.visit_id is not null and exists(select 1 from timesheet_entries where organization_id=new.organization_id and visit_id=new.visit_id and mileage>0) then raise exception 'Mileage already exists on the timesheet' using errcode='23514'; end if;
   if new.status='Approved' and (new.reviewed_by_user_id is null or new.submitted_by_user_id is null or new.reviewed_by_user_id=new.submitted_by_user_id
     or exists(select 1 from "AppUsers" where "Id"=new.reviewed_by_user_id and "CareWorkerId"=new.care_worker_id)) then raise exception 'Independent claim approval is required' using errcode='23514'; end if;
   return new;
  end $$;
  create trigger finance_policy_claim_guard before insert or update or delete on finance_workforce_claims for each row execute function govern_policy_claim();
  create function prevent_duplicate_timesheet_mileage() returns trigger language plpgsql as $$ begin
   if new.mileage>0 then
     perform 1 from "CareWorkers" where "Id"=new.care_worker_id for update;
     if exists(select 1 from finance_workforce_claims where organization_id=new.organization_id and visit_id=new.visit_id and claim_type='Mileage' and status<>'Rejected') then raise exception 'Mileage already exists in a claim' using errcode='23514'; end if;
   end if;return new;
  end $$;
  create trigger finance_mileage_source_guard before insert or update on timesheet_entries for each row execute function prevent_duplicate_timesheet_mileage();
  """);
 protected override void Down(MigrationBuilder m)=>throw new NotSupportedException("Claim policy history requires forward recovery.");
}
