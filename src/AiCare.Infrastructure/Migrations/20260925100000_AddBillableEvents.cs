using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260925100000_AddBillableEvents")]
public sealed class AddBillableEvents : Migration
{
 protected override void Up(MigrationBuilder migrationBuilder)=>migrationBuilder.Sql("""
 create table finance_billable_events(
  id uuid primary key,organization_id uuid not null,branch_id uuid not null,service_user_id uuid not null references "ServiceUsers"("Id"),visit_id uuid not null references "Visits"("Id"),
  funder_id uuid not null references finance_funders(id),funding_arrangement_id uuid not null references funding_arrangements(id),rate_version_id uuid not null references finance_rate_versions(id),rate_rule_id uuid not null references finance_rate_rules(id),
  service_date date not null,quantity numeric(10,2) not null check(quantity>0),unit text not null,unit_rate numeric(12,4) not null check(unit_rate>=0),gross_amount numeric(12,2) not null check(gross_amount>=0),adjustment_amount numeric(12,2) not null default 0,final_amount numeric(12,2) not null check(final_amount>=0),currency char(3) not null,
  calculation_explanation text not null,status text not null default 'Pending',adjustment_reason text not null default '',created_at timestamptz not null default now(),created_by text not null,approved_at timestamptz null,approved_by text null,revision integer not null default 1,
  check(status in ('Pending','Validated','Approved','Rejected','Invoiced')),check(unit in ('Hour','Visit','Mile','Item')),unique(organization_id,visit_id)
 );
 create index ix_billable_events_queue on finance_billable_events(organization_id,branch_id,status,service_date);
 create table finance_billing_exceptions(
  id uuid primary key,organization_id uuid not null,branch_id uuid not null,visit_id uuid not null references "Visits"("Id"),service_user_id uuid not null references "ServiceUsers"("Id"),exception_code text not null,message text not null,status text not null default 'Open',created_at timestamptz not null default now(),resolved_at timestamptz null,resolved_by text null,resolution text not null default '',
  check(status in ('Open','Resolved','Dismissed')),unique(organization_id,visit_id,exception_code,status)
 );
 create index ix_billing_exceptions_queue on finance_billing_exceptions(organization_id,branch_id,status,created_at);
 create function protect_billable_event() returns trigger language plpgsql as $$ begin
  if old.status in ('Approved','Invoiced') and (new.visit_id<>old.visit_id or new.funding_arrangement_id<>old.funding_arrangement_id or new.rate_version_id<>old.rate_version_id or new.rate_rule_id<>old.rate_rule_id or new.quantity<>old.quantity or new.unit_rate<>old.unit_rate or new.gross_amount<>old.gross_amount or new.adjustment_amount<>old.adjustment_amount or new.final_amount<>old.final_amount or new.currency<>old.currency) then raise exception 'Approved billable event calculation is immutable'; end if;return new;end $$;
 create trigger trg_protect_billable_event before update on finance_billable_events for each row execute function protect_billable_event();
 """);
 protected override void Down(MigrationBuilder migrationBuilder)=>migrationBuilder.Sql("""drop trigger if exists trg_protect_billable_event on finance_billable_events;drop function if exists protect_billable_event();drop table if exists finance_billing_exceptions;drop table if exists finance_billable_events;""");
}
