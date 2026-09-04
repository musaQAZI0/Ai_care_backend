using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260830170000_AddProductionEmarLedger")]
public sealed class AddProductionEmarLedger:Migration
{
 protected override void Up(MigrationBuilder migrationBuilder)=>migrationBuilder.Sql("""
 create table emar_ledger(
  id uuid primary key,mar_record_id uuid not null,medication_id uuid not null,service_user_id uuid not null,
  organization_id uuid not null,branch_id uuid not null,event_type text not null,outcome text not null,
  reason_code text not null default '',reason_detail text not null default '',prn_indication text not null default '',
  prn_effect text not null default '',dose_quantity numeric(12,2) null,stock_before numeric(12,2) null,stock_after numeric(12,2) null,
  witness_user_id uuid null,witness_name text not null default '',corrects_ledger_id uuid null,occurred_at timestamptz not null,
  created_by_user_id uuid null,created_by text not null,created_at timestamptz not null default now(),
  constraint fk_emar_ledger_mar foreign key(mar_record_id) references "MedicationAdministrationRecords"("Id") on delete restrict,
  constraint fk_emar_ledger_medication foreign key(medication_id) references "Medications"("Id") on delete restrict,
  constraint fk_emar_ledger_person foreign key(service_user_id) references "ServiceUsers"("Id") on delete restrict,
  constraint fk_emar_ledger_correction foreign key(corrects_ledger_id) references emar_ledger(id) on delete restrict,
  constraint ck_emar_ledger_event check(event_type in ('Administration','Omission','Refusal','Correction','PRNEffect'))
 );
 create unique index ux_emar_terminal_event on emar_ledger(mar_record_id) where event_type in ('Administration','Omission','Refusal');
 create index ix_emar_person_history on emar_ledger(organization_id,branch_id,service_user_id,occurred_at desc);
 create index ix_emar_prn_history on emar_ledger(medication_id,occurred_at desc) where event_type='Administration';

 create table medication_stock_transactions(
  id uuid primary key,medication_id uuid not null,organization_id uuid not null,branch_id uuid not null,
  transaction_type text not null,quantity numeric(12,2) not null,balance_before numeric(12,2) not null,balance_after numeric(12,2) not null,
  reference text not null default '',reason text not null,witness_user_id uuid null,witness_name text not null default '',
  mar_ledger_id uuid null,created_by_user_id uuid null,created_by text not null,created_at timestamptz not null default now(),
  constraint fk_stock_medication foreign key(medication_id) references "Medications"("Id") on delete restrict,
  constraint fk_stock_ledger foreign key(mar_ledger_id) references emar_ledger(id) on delete restrict,
  constraint ck_stock_type check(transaction_type in ('Receipt','Administration','Return','Waste','Disposal','Correction','Adjustment')),
  constraint ck_stock_balance check(balance_after>=0)
 );
 create index ix_stock_medication_history on medication_stock_transactions(organization_id,branch_id,medication_id,created_at desc);

 create table emar_escalations(
  id uuid primary key,mar_record_id uuid not null,ledger_id uuid not null,organization_id uuid not null,branch_id uuid not null,
  escalation_type text not null,severity text not null,status text not null default 'Open',message text not null,
  owner text not null,acknowledged_at timestamptz null,acknowledged_by text not null default '',resolved_at timestamptz null,
  resolution text not null default '',created_at timestamptz not null default now(),
  constraint fk_emar_escalation_mar foreign key(mar_record_id) references "MedicationAdministrationRecords"("Id") on delete restrict,
  constraint fk_emar_escalation_ledger foreign key(ledger_id) references emar_ledger(id) on delete restrict
 );
 create index ix_emar_escalation_queue on emar_escalations(organization_id,branch_id,status,severity,created_at);

 create or replace function reject_emar_ledger_mutation() returns trigger language plpgsql as $$ begin raise exception 'eMAR safety history is immutable'; end $$;
 create trigger trg_emar_ledger_immutable before update or delete on emar_ledger for each row execute function reject_emar_ledger_mutation();
 create trigger trg_stock_ledger_immutable before update or delete on medication_stock_transactions for each row execute function reject_emar_ledger_mutation();
 """);
 protected override void Down(MigrationBuilder migrationBuilder)=>migrationBuilder.Sql("drop trigger if exists trg_stock_ledger_immutable on medication_stock_transactions;drop trigger if exists trg_emar_ledger_immutable on emar_ledger;drop table emar_escalations;drop table medication_stock_transactions;drop table emar_ledger;drop function if exists reject_emar_ledger_mutation();");
}
