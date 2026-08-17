using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260817210000_AddCarePlanLifecycle")]
public sealed class AddCarePlanLifecycle : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            create table if not exists care_plan_versions (
                id uuid primary key,
                care_plan_id uuid not null unique,
                service_user_id uuid not null,
                version_number integer not null check (version_number > 0),
                previous_care_plan_id uuid null,
                change_reason text not null default '',
                status text not null default 'Draft' check (status in ('Draft','InReview','Approved','Signed','Active','Superseded')),
                revision bigint not null default 1 check (revision > 0),
                created_at timestamptz not null default now(),
                updated_at timestamptz not null default now(),
                organization_id uuid not null,
                branch_id uuid not null,
                constraint fk_care_plan_versions_plan foreign key (care_plan_id) references "CarePlans"("Id") on delete cascade,
                constraint fk_care_plan_versions_previous foreign key (previous_care_plan_id) references "CarePlans"("Id") on delete restrict,
                constraint fk_care_plan_versions_person foreign key (service_user_id) references "ServiceUsers"("Id") on delete cascade
            );

            create unique index if not exists ux_care_plan_versions_person_version
                on care_plan_versions(organization_id, branch_id, service_user_id, version_number);
            create index if not exists ix_care_plan_versions_person
                on care_plan_versions(organization_id, branch_id, service_user_id, version_number desc);

            create table if not exists care_plan_signatures (
                id uuid primary key,
                care_plan_id uuid not null,
                signer_type text not null check (signer_type in ('ServiceUser','Representative','CareCoordinator','CareManager')),
                signer_user_id uuid null,
                family_member_id uuid null,
                signer_name text not null,
                relationship text not null default '',
                declaration text not null,
                signature_method text not null check (signature_method in ('AuthenticatedConfirmation','RepresentativeConfirmation')),
                signed_at timestamptz not null default now(),
                organization_id uuid not null,
                branch_id uuid not null,
                constraint fk_care_plan_signatures_plan foreign key (care_plan_id) references "CarePlans"("Id") on delete cascade,
                constraint fk_care_plan_signatures_family foreign key (family_member_id) references "FamilyMembers"("Id") on delete restrict
            );
            create index if not exists ix_care_plan_signatures_plan on care_plan_signatures(organization_id, branch_id, care_plan_id, signed_at desc);

            create table if not exists care_plan_acknowledgements (
                id uuid primary key,
                care_plan_id uuid not null,
                care_worker_id uuid not null,
                acknowledged_by_user_id uuid null,
                acknowledged_by text not null,
                acknowledged_at timestamptz not null default now(),
                organization_id uuid not null,
                branch_id uuid not null,
                constraint fk_care_plan_ack_plan foreign key (care_plan_id) references "CarePlans"("Id") on delete cascade,
                constraint fk_care_plan_ack_worker foreign key (care_worker_id) references "CareWorkers"("Id") on delete restrict
            );
            create unique index if not exists ux_care_plan_ack_worker on care_plan_acknowledgements(care_plan_id, care_worker_id);

            create table if not exists care_plan_lifecycle_events (
                id uuid primary key,
                care_plan_id uuid not null,
                from_status text not null,
                to_status text not null,
                reason text not null default '',
                comment text not null default '',
                performed_by_user_id uuid null,
                performed_by text not null,
                performed_at timestamptz not null default now(),
                organization_id uuid not null,
                branch_id uuid not null,
                constraint fk_care_plan_events_plan foreign key (care_plan_id) references "CarePlans"("Id") on delete cascade
            );
            create index if not exists ix_care_plan_events_plan on care_plan_lifecycle_events(organization_id, branch_id, care_plan_id, performed_at desc);

            insert into care_plan_versions(
                id, care_plan_id, service_user_id, version_number, previous_care_plan_id, change_reason, status,
                revision, created_at, updated_at, organization_id, branch_id)
            select
                gen_random_uuid(), p."Id", p."ServiceUserId",
                row_number() over(partition by p."OrganizationId", p."BranchId", p."ServiceUserId" order by p."Version", p."Id")::integer,
                null,
                'Existing care plan imported into lifecycle',
                case
                    when lower(p."Status") = 'active' then 'Active'
                    when lower(p."Status") = 'approved' then 'Approved'
                    else 'Draft'
                end,
                1, now(), now(), p."OrganizationId", p."BranchId"
            from "CarePlans" p
            where not exists (select 1 from care_plan_versions v where v.care_plan_id = p."Id");

            with ranked as (
                select id,
                    row_number() over(partition by organization_id, branch_id, service_user_id order by version_number desc) as rn
                from care_plan_versions
                where status = 'Active'
            )
            update care_plan_versions v
            set status = 'Superseded', updated_at = now(), revision = revision + 1
            from ranked r
            where v.id = r.id and r.rn > 1;

            update "CarePlans" p
            set "Status" = v.status,
                "Version" = 'v' || v.version_number::text
            from care_plan_versions v
            where v.care_plan_id = p."Id";

            create unique index if not exists ux_care_plan_versions_one_active
                on care_plan_versions(organization_id, branch_id, service_user_id)
                where status = 'Active';

            create or replace function aicare_prepare_care_plan_insert()
            returns trigger
            language plpgsql
            as $$
            declare
                next_version integer;
            begin
                perform pg_advisory_xact_lock(hashtextextended(
                    new."OrganizationId"::text || ':' || new."BranchId"::text || ':' || new."ServiceUserId"::text,
                    0));

                select coalesce(max(version_number), 0) + 1
                into next_version
                from care_plan_versions
                where organization_id = new."OrganizationId"
                  and branch_id = new."BranchId"
                  and service_user_id = new."ServiceUserId";

                new."Version" := 'v' || next_version::text;
                new."Status" := 'Draft';
                return new;
            end;
            $$;

            create or replace function aicare_register_care_plan_version()
            returns trigger
            language plpgsql
            as $$
            declare
                version_number_value integer;
            begin
                if exists (select 1 from care_plan_versions where care_plan_id = new."Id") then
                    return new;
                end if;

                version_number_value := nullif(regexp_replace(new."Version", '[^0-9]', '', 'g'), '')::integer;
                if version_number_value is null then
                    raise exception 'Care plan version number could not be determined.';
                end if;

                insert into care_plan_versions(
                    id, care_plan_id, service_user_id, version_number, previous_care_plan_id, change_reason,
                    status, revision, created_at, updated_at, organization_id, branch_id)
                values(
                    gen_random_uuid(), new."Id", new."ServiceUserId", version_number_value, null,
                    'Care plan draft created', 'Draft', 1, now(), now(), new."OrganizationId", new."BranchId");
                return new;
            end;
            $$;

            create or replace function aicare_guard_care_plan_update()
            returns trigger
            language plpgsql
            as $$
            declare
                lifecycle_status text;
            begin
                select status into lifecycle_status
                from care_plan_versions
                where care_plan_id = old."Id";

                if lifecycle_status is null then
                    return new;
                end if;

                if lifecycle_status <> 'Draft' and (
                    new."ServiceUserId" is distinct from old."ServiceUserId" or
                    new."PersonalCare" is distinct from old."PersonalCare" or
                    new."MedicationSupport" is distinct from old."MedicationSupport" or
                    new."MobilityAndTransfers" is distinct from old."MobilityAndTransfers" or
                    new."Nutrition" is distinct from old."Nutrition" or
                    new."ReviewDueAt" is distinct from old."ReviewDueAt" or
                    new."Version" is distinct from old."Version"
                ) then
                    raise exception 'Approved, signed, active, or superseded care plan versions are immutable. Create a revision instead.';
                end if;

                new."Status" := lifecycle_status;
                return new;
            end;
            $$;

            create or replace function aicare_guard_care_plan_delete()
            returns trigger
            language plpgsql
            as $$
            declare
                lifecycle_status text;
            begin
                select status into lifecycle_status
                from care_plan_versions
                where care_plan_id = old."Id";

                if lifecycle_status is not null and lifecycle_status <> 'Draft' then
                    raise exception 'Only draft care plan versions can be deleted.';
                end if;
                return old;
            end;
            $$;

            drop trigger if exists trg_aicare_prepare_care_plan_insert on "CarePlans";
            create trigger trg_aicare_prepare_care_plan_insert
                before insert on "CarePlans"
                for each row execute function aicare_prepare_care_plan_insert();

            drop trigger if exists trg_aicare_register_care_plan_version on "CarePlans";
            create constraint trigger trg_aicare_register_care_plan_version
                after insert on "CarePlans"
                deferrable initially deferred
                for each row execute function aicare_register_care_plan_version();

            drop trigger if exists trg_aicare_guard_care_plan_update on "CarePlans";
            create trigger trg_aicare_guard_care_plan_update
                before update on "CarePlans"
                for each row execute function aicare_guard_care_plan_update();

            drop trigger if exists trg_aicare_guard_care_plan_delete on "CarePlans";
            create trigger trg_aicare_guard_care_plan_delete
                before delete on "CarePlans"
                for each row execute function aicare_guard_care_plan_delete();
        """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            drop trigger if exists trg_aicare_guard_care_plan_delete on "CarePlans";
            drop trigger if exists trg_aicare_guard_care_plan_update on "CarePlans";
            drop trigger if exists trg_aicare_register_care_plan_version on "CarePlans";
            drop trigger if exists trg_aicare_prepare_care_plan_insert on "CarePlans";
            drop function if exists aicare_guard_care_plan_delete();
            drop function if exists aicare_guard_care_plan_update();
            drop function if exists aicare_register_care_plan_version();
            drop function if exists aicare_prepare_care_plan_insert();
            drop table if exists care_plan_lifecycle_events;
            drop table if exists care_plan_acknowledgements;
            drop table if exists care_plan_signatures;
            drop table if exists care_plan_versions;
        """);
    }
}
