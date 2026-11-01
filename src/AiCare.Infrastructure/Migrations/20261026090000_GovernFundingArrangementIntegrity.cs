using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261026090000_GovernFundingArrangementIntegrity")]
public sealed class GovernFundingArrangementIntegrity : Migration
{
    protected override void Up(MigrationBuilder m)=>m.Sql("""
        create view finance_funding_integrity_findings as
          select a.id,a.organization_id,a.branch_id,'RELATED_SCOPE_OR_CURRENCY'::text code
          from funding_arrangements a
          left join "ServiceUsers" s on s."Id"=a.service_user_id
          left join finance_funders f on f.id=a.funder_id
          left join finance_rate_cards c on c.id=a.rate_card_id
          where s."OrganizationId" is distinct from a.organization_id or s."BranchId" is distinct from a.branch_id
            or (a.funder_id is not null and (f.organization_id is distinct from a.organization_id or (f.branch_id is not null and f.branch_id<>a.branch_id)))
            or (a.rate_card_id is not null and (c.organization_id is distinct from a.organization_id or (c.branch_id is not null and c.branch_id<>a.branch_id) or c.currency is distinct from f.currency))
          union all
          select a.id,a.organization_id,a.branch_id,'OVERLAPPING_PRIMARY'::text
          from funding_arrangements a where a.status='Active' and a.allocation_rule='Primary' and exists(
            select 1 from funding_arrangements b where b.id<>a.id and b.organization_id=a.organization_id and b.service_user_id=a.service_user_id
              and b.status='Active' and b.allocation_rule='Primary'
              and b.valid_from<=coalesce(a.valid_to,'infinity'::timestamptz) and a.valid_from<=coalesce(b.valid_to,'infinity'::timestamptz));

        create function govern_funding_arrangement() returns trigger language plpgsql as $$
        begin
          if TG_OP='DELETE' then raise exception 'Funding history cannot be deleted' using errcode='23514'; end if;
          -- Every writer, including direct SQL, serializes on the actual person row.
          perform 1 from "ServiceUsers" where "Id"=new.service_user_id and "OrganizationId"=new.organization_id and "BranchId"=new.branch_id for update;
          if not found then raise exception 'Funding recipient scope mismatch' using errcode='23514'; end if;
          if TG_OP='UPDATE' and exists(select 1 from finance_contract_versions where funding_arrangement_id=old.id) and new is distinct from old then
            raise exception 'Contract-linked funding history is immutable; create a new authorisation' using errcode='23514';
          end if;
          if new.funder_id is not null and not exists(select 1 from finance_funders where id=new.funder_id and organization_id=new.organization_id and (branch_id is null or branch_id=new.branch_id) and active) then
            raise exception 'Funding payer scope mismatch' using errcode='23514';
          end if;
          if new.rate_card_id is not null and not exists(select 1 from finance_rate_cards c join finance_funders f on f.id=new.funder_id
            where c.id=new.rate_card_id and c.organization_id=new.organization_id and f.organization_id=new.organization_id
              and (c.branch_id is null or c.branch_id=new.branch_id) and c.currency=f.currency and c.active) then
            raise exception 'Funding rate scope or currency mismatch' using errcode='23514';
          end if;
          if new.valid_to<new.valid_from then raise exception 'Invalid funding range' using errcode='23514'; end if;
          if new.status='Active' and new.allocation_rule='Primary' and exists(select 1 from funding_arrangements a where a.id<>new.id
            and a.organization_id=new.organization_id and a.service_user_id=new.service_user_id and a.status='Active' and a.allocation_rule='Primary'
            and a.valid_from<=coalesce(new.valid_to,'infinity'::timestamptz) and new.valid_from<=coalesce(a.valid_to,'infinity'::timestamptz)) then
            raise exception 'Primary funding effective dates overlap' using errcode='23P01';
          end if; return new;
        end $$;
        create trigger finance_funding_integrity before insert or update or delete on funding_arrangements for each row execute function govern_funding_arrangement();
        create function preserve_finance_reference_scope() returns trigger language plpgsql as $$ begin
          if TG_TABLE_NAME='finance_funders' then
            if (new.organization_id,new.branch_id,new.currency) is distinct from (old.organization_id,old.branch_id,old.currency)
              and exists(select 1 from funding_arrangements where funder_id=old.id) then
              raise exception 'Referenced payer scope and currency cannot change' using errcode='23514';
            end if;
          else
            if (new.organization_id,new.branch_id,new.currency,new.service_type) is distinct from (old.organization_id,old.branch_id,old.currency,old.service_type)
              and (exists(select 1 from funding_arrangements where rate_card_id=old.id) or exists(select 1 from finance_rate_versions where rate_card_id=old.id)) then
              raise exception 'Referenced rate card identity cannot change' using errcode='23514';
            end if;
          end if; return new;
        end $$;
        create trigger finance_funder_scope_guard before update on finance_funders for each row execute function preserve_finance_reference_scope();
        create trigger finance_card_scope_guard before update on finance_rate_cards for each row execute function preserve_finance_reference_scope();

        """);
    protected override void Down(MigrationBuilder m)=>m.Sql("""
        drop trigger finance_funder_scope_guard on finance_funders;
        drop trigger finance_card_scope_guard on finance_rate_cards;
        drop function preserve_finance_reference_scope();
        drop trigger finance_funding_integrity on funding_arrangements;
        drop function govern_funding_arrangement();
        drop view finance_funding_integrity_findings;
        """);
}
