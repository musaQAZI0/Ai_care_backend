using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261025120000_AddCarePlanMedicationAuthority")]
public sealed class AddCarePlanMedicationAuthority : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table care_plan_medication_authorities (
            care_plan_id uuid not null references "CarePlans"("Id") on delete cascade,
            medication_id uuid not null references "Medications"("Id") on delete restrict,
            service_user_id uuid not null references "ServiceUsers"("Id") on delete cascade,
            organization_id uuid not null,
            branch_id uuid not null,
            support_type text not null check (support_type in ('Prompt','Assist','Administer','Observe')),
            instructions text not null,
            review_due_at timestamptz not null,
            family_medicine_applies boolean not null,
            family_recording_method text not null,
            family_responsible_person text not null,
            family_escalation_route text not null,
            prior_dose_check text not null,
            updated_by_user_id uuid not null,
            updated_at timestamptz not null default now(),
            primary key (care_plan_id,medication_id)
        );
        create index ix_care_plan_medication_authorities_person
            on care_plan_medication_authorities(organization_id,branch_id,service_user_id,medication_id);

        create function aicare_guard_medication_authority_mutation()
        returns trigger language plpgsql as $$
        declare
            authority_row care_plan_medication_authorities%rowtype;
            plan_row "CarePlans"%rowtype;
            lifecycle_status text;
            medication_person uuid;
            medication_organization uuid;
            medication_branch uuid;
        begin
            if tg_op = 'DELETE' then authority_row := old; else authority_row := new; end if;
            select * into plan_row from "CarePlans" where "Id" = authority_row.care_plan_id;
            select status into lifecycle_status from care_plan_versions
                where care_plan_id = authority_row.care_plan_id for share;
            if lifecycle_status is distinct from 'Draft' then
                raise exception 'Medication authority can only be changed on a draft care plan version.';
            end if;
            if tg_op <> 'DELETE' then
                if tg_op = 'UPDATE' and old.care_plan_id is distinct from new.care_plan_id then
                    raise exception 'Medication authority cannot move between care plan versions.';
                end if;
                select "ServiceUserId", "OrganizationId", "BranchId"
                    into medication_person, medication_organization, medication_branch from "Medications"
                    where "Id" = authority_row.medication_id;
                if plan_row."ServiceUserId" is distinct from authority_row.service_user_id
                    or plan_row."OrganizationId" is distinct from authority_row.organization_id
                    or plan_row."BranchId" is distinct from authority_row.branch_id
                    or medication_person is distinct from authority_row.service_user_id
                    or medication_organization is distinct from authority_row.organization_id
                    or medication_branch is distinct from authority_row.branch_id then
                    raise exception 'Medication authority must match its care plan, person, tenant and medication.';
                end if;
                return new;
            end if;
            return old;
        end;
        $$;
        create trigger trg_aicare_guard_medication_authority_mutation
            before insert or update or delete on care_plan_medication_authorities
            for each row execute function aicare_guard_medication_authority_mutation();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop table if exists care_plan_medication_authorities;
        drop function if exists aicare_guard_medication_authority_mutation();
        """);
}
