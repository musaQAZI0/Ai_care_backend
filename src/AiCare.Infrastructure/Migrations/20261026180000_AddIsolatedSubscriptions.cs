using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20261026180000_AddIsolatedSubscriptions")]
public sealed class AddIsolatedSubscriptions:Migration
{
 protected override void Up(MigrationBuilder m)=>m.Sql("""
 create schema saas;
 create table saas.subscription_versions(
  id uuid primary key,organization_id uuid not null references "Organizations"("Id"),version integer not null check(version>0),
  plan_reference text not null check(length(trim(plan_reference))>0),billing_contact text not null check(length(trim(billing_contact))>0),
  subscription_status text not null check(subscription_status in ('Trial','Active','Suspended','Cancelled')),
  invoice_status text not null check(invoice_status in ('NotInvoiced','Open','Paid','Overdue','Void')),
  subscription_invoice_reference text not null,decision_reference text not null check(length(trim(decision_reference))>0),
  effective_from timestamptz not null,valid_until timestamptz not null check(valid_until>effective_from),
  stored_workers_limit integer null check(stored_workers_limit>=0),stored_branches_limit integer null check(stored_branches_limit>=0),
  entitlements jsonb not null check(jsonb_typeof(entitlements)='object'),request_snapshot jsonb not null,
  created_by uuid not null,approved_by uuid null,approved_at timestamptz null,created_at timestamptz not null default now(),
  unique(organization_id,version),check(approved_by is null or (approved_by<>created_by and approved_at is not null)));
 create function saas.govern_subscription() returns trigger language plpgsql as $$declare previous timestamptz;item record;begin
  if TG_OP='DELETE' then raise exception 'Subscription history is immutable' using errcode='23514';end if;
  if TG_OP='UPDATE' and (old.approved_by is not null or (to_jsonb(new)-'approved_by'-'approved_at') is distinct from (to_jsonb(old)-'approved_by'-'approved_at')) then raise exception 'Create a new subscription version' using errcode='23514';end if;
  perform pg_advisory_xact_lock(hashtextextended('saas-subscription:'||new.organization_id::text,0));
  for item in select * from jsonb_each(new.entitlements) loop if jsonb_typeof(item.value)<>'boolean' or item.key !~ '^[A-Za-z][A-Za-z0-9._-]*$' then raise exception 'Explicit boolean entitlement codes required' using errcode='23514';end if;end loop;
  if new.approved_by is not null then
   select max(effective_from) into previous from saas.subscription_versions where organization_id=new.organization_id and approved_by is not null and id<>new.id;
   if previous is not null and new.effective_from<=previous then raise exception 'Subscription versions must advance their effective time' using errcode='23514';end if;
   if new.stored_workers_limit is not null and new.stored_workers_limit<(select count(*) from "CareWorkers" where "OrganizationId"=new.organization_id) then raise exception 'Worker limit is below stored usage' using errcode='23514';end if;
   if new.stored_branches_limit is not null and new.stored_branches_limit<(select count(*) from "Branches" where "OrganizationId"=new.organization_id) then raise exception 'Branch limit is below stored usage' using errcode='23514';end if;
  end if;return new;end $$;
 create trigger subscription_version_guard before insert or update or delete on saas.subscription_versions for each row execute function saas.govern_subscription();
 create function saas.enforce_stored_usage() returns trigger language plpgsql as $$declare cap integer;usage_count bigint;begin
  if TG_OP='UPDATE' and new."OrganizationId" is not distinct from old."OrganizationId" then return new;end if;
  perform pg_advisory_xact_lock(hashtextextended('saas-subscription:'||new."OrganizationId"::text,0));
  if TG_TABLE_NAME='CareWorkers' then
   select stored_workers_limit into cap from saas.subscription_versions where organization_id=new."OrganizationId" and approved_by is not null and effective_from<=now() order by effective_from desc,version desc limit 1;
   select count(*) into usage_count from "CareWorkers" where "OrganizationId"=new."OrganizationId";
  else
   select stored_branches_limit into cap from saas.subscription_versions where organization_id=new."OrganizationId" and approved_by is not null and effective_from<=now() order by effective_from desc,version desc limit 1;
   select count(*) into usage_count from "Branches" where "OrganizationId"=new."OrganizationId";
  end if;
  if cap is not null and usage_count>=cap then raise exception 'Approved subscription stored-record limit reached' using errcode='23514';end if;return new;end $$;
 create trigger subscription_worker_usage before insert or update on "CareWorkers" for each row execute function saas.enforce_stored_usage();
 create trigger subscription_branch_usage before insert or update on "Branches" for each row execute function saas.enforce_stored_usage();
 """);
 protected override void Down(MigrationBuilder m)=>throw new NotSupportedException("Subscription history is retained independently; recover forwards.");
}
