using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261028100000_AddApprovedExpenseRecharges")]
public sealed class AddApprovedExpenseRecharges : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table finance_recharge_policies (
          id uuid primary key,organization_id uuid not null,branch_id uuid not null,
          contract_version_id uuid not null references finance_contract_versions(id),
          claim_policy_id uuid not null references finance_claim_policies(id),
          service_type text not null check(length(trim(service_type))>0),
          reference text not null check(length(trim(reference))>0),
          valid_from date not null,valid_to date not null check(valid_to>=valid_from),
          charge_basis text not null check(charge_basis='ClaimAmountAsNet'),
          multiplier numeric(10,4) not null check(multiplier>0 and multiplier<=10),
          maximum_net numeric(12,2) not null check(maximum_net>0),
          authorised_net_limit numeric(12,2) not null check(authorised_net_limit>0),
          created_by uuid not null,approved_by uuid null,approved_at timestamptz null,
          request_snapshot jsonb not null,created_at timestamptz not null default now(),
          check(approved_by is null or (approved_by<>created_by and approved_at is not null)));
        create table finance_expense_recharges (
          id uuid primary key,organization_id uuid not null,branch_id uuid not null,
          claim_id uuid not null references finance_workforce_claims(id),
          policy_id uuid not null references finance_recharge_policies(id),
          service_user_id uuid not null references "ServiceUsers"("Id"),funder_id uuid not null references finance_funders(id),
          service_date date not null,currency char(3) not null,net_amount numeric(12,2) not null check(net_amount>0),
          reason text not null check(length(trim(reason))>0),request_snapshot jsonb not null,
          status text not null default 'Submitted' check(status in ('Submitted','Approved','Rejected')),
          created_by uuid not null,approved_by uuid null,reviewed_at timestamptz null,review_reason text not null default '',
          created_at timestamptz not null default now(),
          check(status='Submitted' or (approved_by is not null and approved_by<>created_by and reviewed_at is not null and length(trim(review_reason))>0)));
        create unique index ux_active_claim_recharge on finance_expense_recharges(claim_id) where status<>'Rejected';
        create function govern_recharge_policy() returns trigger language plpgsql as $$
        declare contract finance_contract_versions%rowtype;claim_policy finance_claim_policies%rowtype;
        begin
          if TG_OP='DELETE' then raise exception 'Recharge policy history is immutable' using errcode='23514'; end if;
          if TG_OP='UPDATE' and (old.approved_by is not null or (to_jsonb(new)-'approved_by'-'approved_at') is distinct from (to_jsonb(old)-'approved_by'-'approved_at'))
            then raise exception 'Create a new recharge policy version' using errcode='23514'; end if;
          select * into contract from finance_contract_versions where id=new.contract_version_id for update;
          if not found or contract.status<>'Approved' or contract.organization_id<>new.organization_id or contract.branch_id<>new.branch_id
            or new.valid_from<contract.authorised_from or new.valid_to>contract.authorised_to then
            raise exception 'Recharge requires a covering approved contract' using errcode='23514'; end if;
          select * into claim_policy from finance_claim_policies where id=new.claim_policy_id;
          if not found or claim_policy.approved_by is null or claim_policy.claim_type<>'Expense' or claim_policy.organization_id<>new.organization_id
            or claim_policy.branch_id<>new.branch_id or claim_policy.currency<>contract.currency
            or new.valid_from<claim_policy.valid_from or new.valid_to>claim_policy.valid_to then
            raise exception 'Recharge requires an approved expense policy in the same branch and currency' using errcode='23514'; end if;
          if new.approved_by is not null and exists(select 1 from finance_recharge_policies where id<>new.id
            and contract_version_id=new.contract_version_id and claim_policy_id=new.claim_policy_id and approved_by is not null
            and daterange(valid_from,valid_to,'[]')&&daterange(new.valid_from,new.valid_to,'[]')) then
            raise exception 'Recharge policy periods overlap' using errcode='23514'; end if;
          return new;
        end $$;
        create trigger recharge_policy_guard before insert or update or delete on finance_recharge_policies for each row execute function govern_recharge_policy();
        create function govern_expense_recharge() returns trigger language plpgsql as $$
        declare policy finance_recharge_policies%rowtype;claim finance_workforce_claims%rowtype;contract finance_contract_versions%rowtype;used numeric;
        begin
          if TG_OP='DELETE' then raise exception 'Recharge history is immutable' using errcode='23514'; end if;
          if TG_OP='UPDATE' and (old.status<>'Submitted' or new.status not in ('Approved','Rejected') or
            (to_jsonb(new)-'status'-'approved_by'-'reviewed_at'-'review_reason') is distinct from (to_jsonb(old)-'status'-'approved_by'-'reviewed_at'-'review_reason')) then
            raise exception 'Only independent recharge review is permitted' using errcode='23514'; end if;
          if TG_OP='INSERT' and new.status<>'Submitted' then raise exception 'Submit recharge for review first' using errcode='23514'; end if;
          select * into policy from finance_recharge_policies where id=new.policy_id for update;
          if not found or policy.approved_by is null or policy.organization_id<>new.organization_id or policy.branch_id<>new.branch_id
            or new.service_date not between policy.valid_from and policy.valid_to then
            raise exception 'Approved covering recharge policy required' using errcode='23514'; end if;
          select * into contract from finance_contract_versions where id=policy.contract_version_id;
          select * into claim from finance_workforce_claims where id=new.claim_id for update;
          if not found or claim.status not in ('Approved','Paid') or claim.claim_type<>'Expense' or claim.policy_id<>policy.claim_policy_id
            or claim.organization_id<>new.organization_id or claim.branch_id<>new.branch_id or claim.currency<>new.currency
            or claim.currency<>contract.currency or claim.claim_date<>new.service_date or claim.evidence_id is null
            or new.service_user_id<>contract.person_id or new.funder_id<>contract.funder_id
            or not exists(select 1 from "Visits" v where v."Id"=claim.visit_id and v."OrganizationId"=new.organization_id
              and v."BranchId"=new.branch_id and v."ServiceUserId"=new.service_user_id)
            or new.net_amount<>finance_policy_round(claim.amount*policy.multiplier,2,contract.rounding_mode)
            or new.net_amount>policy.maximum_net then
            raise exception 'Recharge claim, evidence, recipient, payer, currency or calculation mismatch' using errcode='23514'; end if;
          if new.status<>'Submitted' and (new.approved_by=claim.submitted_by_user_id or exists(select 1 from "AppUsers"
            where "Id"=new.approved_by and "CareWorkerId"=claim.care_worker_id)) then
            raise exception 'Recharge review must be independent of claimant and submitter' using errcode='23514'; end if;
          if new.status<>'Rejected' then
            select coalesce(sum(net_amount),0) into used from finance_expense_recharges where policy_id=new.policy_id and id<>new.id and status<>'Rejected';
            if used+new.net_amount>policy.authorised_net_limit then raise exception 'Recharge authorisation limit exceeded' using errcode='23514'; end if;
          end if;
          return new;
        end $$;
        create trigger expense_recharge_guard before insert or update or delete on finance_expense_recharges for each row execute function govern_expense_recharge();
        alter table finance_invoice_lines add column recharge_id uuid null unique references finance_expense_recharges(id);
        create function finance_resolve_recharge_vat(source uuid,invoice_day date)
          returns table(treatment_id uuid,treatment text,rate numeric,tax_day date,rounding_mode text) language sql stable as $$
          select t.id,t.treatment,t.rate,case t.tax_point when 'ServiceDate' then r.service_date else invoice_day end,t.rounding_mode
          from finance_expense_recharges r join finance_recharge_policies p on p.id=r.policy_id
          join finance_vat_treatments t on t.organization_id=r.organization_id and t.branch_id=r.branch_id
            and lower(t.service_type)=lower(p.service_type) and t.approved_by is not null
          where r.id=source and r.status='Approved' and
            (case t.tax_point when 'ServiceDate' then r.service_date else invoice_day end) between t.valid_from and t.valid_to;
        $$;
        create function govern_recharge_invoice_line() returns trigger language plpgsql as $$
        declare invoice_day date;resolved record;matches integer;
        begin
          if TG_OP='UPDATE' and new.recharge_id is distinct from old.recharge_id then raise exception 'Recharge source is immutable' using errcode='23514'; end if;
          if new.recharge_id is null or TG_OP<>'INSERT' then return new; end if;
          if new.billable_event_id is not null or new.event_share_id is not null or new.visit_id is not null then
            raise exception 'Expense recharge is a separate source, not another attendance charge' using errcode='23514'; end if;
          perform 1 from finance_expense_recharges r join "Invoices" i on i."Id"=new.invoice_id
            join finance_invoice_details d on d.invoice_id=i."Id" and d.organization_id=i."OrganizationId"
            where r.id=new.recharge_id and r.organization_id=new.organization_id and r.branch_id=new.branch_id
              and r.status='Approved' and r.service_user_id=new.service_user_id and r.net_amount=new.amount
              and new.quantity=1 and new.unit_rate=r.net_amount and i."OrganizationId"=r.organization_id
              and i."BranchId"=r.branch_id and d.currency=r.currency and d.payer_id=r.funder_id for update of r;
          if not found then raise exception 'Invoice must match the approved expense recharge' using errcode='23514'; end if;
          select invoice_date into invoice_day from finance_invoice_details where invoice_id=new.invoice_id;
          select count(*) into matches from finance_resolve_recharge_vat(new.recharge_id,invoice_day);
          if matches<>1 then raise exception 'Exactly one adviser-approved expense VAT treatment required' using errcode='23514'; end if;
          select * into resolved from finance_resolve_recharge_vat(new.recharge_id,invoice_day);
          new.vat_treatment_id:=resolved.treatment_id;new.vat_treatment:=resolved.treatment;new.vat_rate:=resolved.rate;
          new.tax_point_date:=resolved.tax_day;new.vat_amount:=finance_policy_round(new.amount*resolved.rate/100,2,resolved.rounding_mode);
          return new;
        end $$;
        create trigger a_recharge_invoice_line before insert or update on finance_invoice_lines for each row execute function govern_recharge_invoice_line();
        -- Extend the existing total guard to expense supplies without weakening attendance lineage.
        do $$declare definition text;begin
          select pg_get_functiondef('verify_invoice_vat_totals()'::regprocedure) into definition;
          if position('billable_event_id is not null' in definition)=0 then raise exception 'Review VAT guard before upgrading'; end if;
          definition:=replace(definition,'billable_event_id is not null','(billable_event_id is not null or recharge_id is not null)');execute definition;
        end $$;
        """);

    protected override void Down(MigrationBuilder m) => throw new NotSupportedException("Preserve recharge and invoice history; recover forwards.");
}
