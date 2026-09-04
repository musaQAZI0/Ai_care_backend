using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260830140000_AddAdmissionTransferDischargeWorkflows")]
public sealed class AddAdmissionTransferDischargeWorkflows : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            create table person_admissions (
                id uuid primary key,
                service_user_id uuid not null,
                organization_id uuid not null,
                branch_id uuid not null,
                referral_id uuid null,
                admission_type text not null,
                planned_at timestamptz null,
                admitted_at timestamptz not null,
                funding_confirmed boolean not null,
                initial_plan_confirmed boolean not null,
                medication_reconciled boolean not null,
                status text not null default 'Admitted',
                notes text not null default '',
                admitted_by text not null,
                created_at timestamptz not null default now(),
                constraint fk_person_admissions_person foreign key(service_user_id) references "ServiceUsers"("Id") on delete restrict,
                constraint fk_person_admissions_referral foreign key(referral_id) references person_referrals(id) on delete set null
            );
            create unique index ux_person_admissions_active on person_admissions(organization_id,service_user_id) where status='Admitted';
            create index ix_person_admissions_branch on person_admissions(organization_id,branch_id,admitted_at desc);

            create table person_transfers (
                id uuid primary key,
                service_user_id uuid not null,
                organization_id uuid not null,
                source_branch_id uuid not null,
                destination_branch_id uuid not null,
                effective_at timestamptz not null,
                reason text not null,
                handover_summary text not null,
                receiving_manager text not null,
                transferred_by text not null,
                created_at timestamptz not null default now(),
                constraint ck_person_transfer_branch check(source_branch_id<>destination_branch_id),
                constraint fk_person_transfers_person foreign key(service_user_id) references "ServiceUsers"("Id") on delete restrict,
                constraint fk_person_transfers_source foreign key(source_branch_id) references "Branches"("Id") on delete restrict,
                constraint fk_person_transfers_destination foreign key(destination_branch_id) references "Branches"("Id") on delete restrict
            );
            create index ix_person_transfers_person on person_transfers(organization_id,service_user_id,effective_at desc);

            create table person_discharges (
                id uuid primary key,
                service_user_id uuid not null,
                organization_id uuid not null,
                branch_id uuid not null,
                admission_id uuid not null,
                discharged_at timestamptz not null,
                reason text not null,
                destination text not null,
                summary text not null,
                medication_handover text not null,
                property_returned boolean not null,
                follow_up_required boolean not null,
                follow_up_details text not null default '',
                authorized_by text not null,
                created_at timestamptz not null default now(),
                constraint fk_person_discharges_person foreign key(service_user_id) references "ServiceUsers"("Id") on delete restrict,
                constraint fk_person_discharges_admission foreign key(admission_id) references person_admissions(id) on delete restrict
            );
            create unique index ux_person_discharges_admission on person_discharges(admission_id);
            create index ix_person_discharges_person on person_discharges(organization_id,service_user_id,discharged_at desc);
        """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            drop table person_discharges;
            drop table person_transfers;
            drop table person_admissions;
        """);
    }
}
