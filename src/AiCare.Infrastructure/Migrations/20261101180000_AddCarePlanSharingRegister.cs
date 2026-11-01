using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20261101180000_AddCarePlanSharingRegister")]
public sealed class AddCarePlanSharingRegister : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table care_plan_shares (
          id uuid primary key,care_plan_id uuid not null references "CarePlans"("Id"),service_user_id uuid not null,
          organization_id uuid not null,branch_id uuid not null,plan_revision bigint not null,
          recipient text not null check(length(trim(recipient)) between 1 and 300),
          relationship text not null check(length(trim(relationship)) between 1 and 300),
          purpose text not null check(length(trim(purpose)) between 1 and 2000),
          authority_reference text not null check(length(trim(authority_reference)) between 1 and 2000),
          sections_json jsonb not null,expires_at timestamptz not null,
          html text not null,sha256 text not null,file_name text not null,
          created_by uuid not null references "AppUsers"("Id"),created_at timestamptz not null default now(),request_snapshot jsonb not null
        );
        create table care_plan_share_revocations (
          id uuid primary key,share_id uuid not null unique references care_plan_shares(id),
          reason text not null check(length(trim(reason)) between 1 and 2000),created_by uuid not null references "AppUsers"("Id"),
          created_at timestamptz not null default now(),request_snapshot jsonb not null
        );
        create function guard_care_plan_share() returns trigger language plpgsql as $$
        declare v care_plan_versions%rowtype;
        begin
          if TG_OP<>'INSERT' then raise exception 'Sharing history is append-only' using errcode='23514'; end if;
          if TG_TABLE_NAME='care_plan_shares' then
            select * into v from care_plan_versions where care_plan_id=new.care_plan_id for share;
            if not found or v.status<>'Active' or v.revision<>new.plan_revision or v.organization_id<>new.organization_id
              or v.branch_id<>new.branch_id or v.service_user_id<>new.service_user_id or new.expires_at<=now()
              or jsonb_typeof(new.sections_json)<>'array' or jsonb_array_length(new.sections_json)=0 then
              raise exception 'Share requires the matching active plan, selected sections and future expiry' using errcode='23514'; end if;
            if exists(select 1 from jsonb_array_elements(new.sections_json) x where jsonb_typeof(x)<>'string'
              or x#>>'{}' not in ('PersonalCare','MedicationSupport','MobilityAndTransfers','Nutrition')) then
              raise exception 'Unsupported shared section' using errcode='23514'; end if;
          end if;
          return new;
        end $$;
        create trigger care_plan_share_guard before insert or update or delete on care_plan_shares for each row execute function guard_care_plan_share();
        create trigger care_plan_share_revocation_guard before insert or update or delete on care_plan_share_revocations for each row execute function guard_care_plan_share();
        """);
    protected override void Down(MigrationBuilder m) => throw new NotSupportedException("Retain sharing history; recover forwards.");
}
