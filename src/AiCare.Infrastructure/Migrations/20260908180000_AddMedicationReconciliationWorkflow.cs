using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260908180000_AddMedicationReconciliationWorkflow")]
public sealed class AddMedicationReconciliationWorkflow : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            alter table medication_safety_profiles add column if not exists reconciliation_status text not null default 'NeedsReview';
            alter table medication_safety_profiles add column if not exists source_type text not null default '';
            alter table medication_safety_profiles add column if not exists source_reference text not null default '';
            alter table medication_safety_profiles add column if not exists reviewed_by_user_id uuid null;
            alter table medication_safety_profiles add column if not exists reviewed_at timestamptz null;
            alter table medication_safety_profiles add column if not exists profile_version integer not null default 1;
            alter table medication_safety_profiles add column if not exists change_reason text not null default '';
            do $$ begin
                alter table medication_safety_profiles add constraint ck_medication_reconciliation_status check (reconciliation_status in ('Draft','NeedsReview','Verified','Superseded','Discontinued'));
            exception when duplicate_object then null; end $$;
            create index if not exists ix_medication_reconciliation_status on medication_safety_profiles(organization_id, branch_id, reconciliation_status);

            create table if not exists medication_reconciliation_history (
                id uuid primary key,
                medication_id uuid not null,
                organization_id uuid not null,
                branch_id uuid not null,
                previous_status text not null default '',
                new_status text not null,
                source_type text not null default '',
                source_reference text not null default '',
                reviewed_by_user_id uuid null,
                reviewed_by text not null default '',
                reviewed_at timestamptz null,
                change_reason text not null,
                created_by text not null,
                created_at timestamptz not null default now(),
                constraint fk_medication_reconciliation_medication foreign key (medication_id) references "Medications"("Id") on delete restrict
            );
            create index if not exists ix_medication_reconciliation_history on medication_reconciliation_history(organization_id, branch_id, medication_id, created_at desc);
        """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            drop table if exists medication_reconciliation_history;
            alter table medication_safety_profiles drop constraint if exists ck_medication_reconciliation_status;
            alter table medication_safety_profiles drop column if exists change_reason;
            alter table medication_safety_profiles drop column if exists profile_version;
            alter table medication_safety_profiles drop column if exists reviewed_at;
            alter table medication_safety_profiles drop column if exists reviewed_by_user_id;
            alter table medication_safety_profiles drop column if exists source_reference;
            alter table medication_safety_profiles drop column if exists source_type;
            alter table medication_safety_profiles drop column if exists reconciliation_status;
        """);
    }
}
