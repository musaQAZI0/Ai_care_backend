using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261001140000_AddMedicationChangeWorkflow")]
public sealed class AddMedicationChangeWorkflow : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table medication_change_requests(
          id uuid primary key,
          medication_id uuid not null references "Medications"("Id") on delete restrict,
          organization_id uuid not null,branch_id uuid not null,
          change_type text not null check(change_type in('Regimen','Stop')),
          status text not null check(status in('Pending','Approved','Rejected','Applied','Cancelled')),
          baseline_profile_version integer not null,
          proposal jsonb not null,
          reason text not null,source_type text not null,source_reference text not null,
          prescriber_instruction text not null,
          requested_by_user_id uuid not null,requested_by text not null,requested_at timestamptz not null default now(),
          effective_at timestamptz not null,
          reviewed_by_user_id uuid null,reviewed_by text not null default '',reviewed_at timestamptz null,
          review_reason text not null default '',
          applied_by_user_id uuid null,applied_by text not null default '',applied_at timestamptz null,
          updated_at timestamptz not null default now());
        create unique index ux_medication_open_change on medication_change_requests(organization_id,medication_id)
          where status in('Pending','Approved');
        create index ix_medication_change_due on medication_change_requests(organization_id,branch_id,status,effective_at);
        create table medication_change_events(
          id uuid primary key,change_id uuid not null references medication_change_requests(id) on delete restrict,
          medication_id uuid not null,organization_id uuid not null,branch_id uuid not null,
          action text not null,detail text not null,actor_user_id uuid not null,actor text not null,
          occurred_at timestamptz not null default now());
        create index ix_medication_change_events on medication_change_events(organization_id,branch_id,change_id,occurred_at);
        create function reject_medication_change_event_mutation() returns trigger language plpgsql as $$
        begin raise exception 'Medication change history is immutable'; end $$;
        create trigger trg_medication_change_event_immutable before update or delete on medication_change_events
          for each row execute function reject_medication_change_event_mutation();
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop table medication_change_events;
        drop function reject_medication_change_event_mutation();
        drop table medication_change_requests;
        """);
}
