using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20261101190000_AddFamilyPlanShareDelivery")]
public sealed class AddFamilyPlanShareDelivery : Migration
{
    protected override void Up(MigrationBuilder m)=>m.Sql("""
        create table care_plan_share_recipients (
          share_id uuid primary key references care_plan_shares(id),request_id uuid not null unique,
          family_member_id uuid not null references "FamilyMembers"("Id"),consent_id uuid not null references consent_records(id),
          grant_revision bigint not null,scope_confirmation text not null check(length(trim(scope_confirmation)) between 1 and 2000),
          created_by uuid not null references "AppUsers"("Id"),created_at timestamptz not null default now(),request_snapshot jsonb not null
        );
        create table care_plan_share_receipts (
          share_id uuid primary key references care_plan_share_recipients(share_id),family_member_id uuid not null references "FamilyMembers"("Id"),
          acknowledged_by uuid not null references "AppUsers"("Id"),acknowledged_at timestamptz not null default now()
        );
        create function family_plan_share_authorized(sid uuid,fid uuid,org uuid) returns boolean language sql stable as $$
          select exists(select 1 from care_plan_shares s
            join care_plan_share_recipients b on b.share_id=s.id
            join care_plan_versions p on p.care_plan_id=s.care_plan_id and p.status='Active'
            join "ServiceUsers" person on person."Id"=s.service_user_id and person."OrganizationId"=s.organization_id and person."BranchId"=s.branch_id
            join "FamilyMembers" f on f."Id"=b.family_member_id and f."ServiceUserId"=s.service_user_id
              and f."OrganizationId"=s.organization_id and f."BranchId"=s.branch_id and f."Status"='Active'
            join family_access_grants g on g.family_member_id=f."Id" and g.service_user_id=s.service_user_id and g.organization_id=s.organization_id
              and g.revision=b.grant_revision and g.verification_status='Verified' and g.access_status='Active'
              and (g.valid_from is null or g.valid_from<=now()) and (g.valid_until is null or g.valid_until>now())
            join family_access_permissions perm on perm.access_grant_id=g.id and perm.permission='ViewCarePlan'
            join consent_records c on c.id=b.consent_id and c.service_user_id=s.service_user_id and c.organization_id=s.organization_id and c.branch_id=s.branch_id
              and c.status='Active' and c.effective_from<=now() and (c.expires_at is null or c.expires_at>now())
              and (c.review_due_at is null or c.review_due_at>now())
            where s.id=sid and b.family_member_id=fid and s.organization_id=org and s.expires_at>now()
              and not exists(select 1 from care_plan_share_revocations r where r.share_id=s.id))
        $$;
        create function guard_family_share_history() returns trigger language plpgsql as $$
        begin
          if TG_OP<>'INSERT' then raise exception 'Family share delivery history is append-only' using errcode='23514'; end if;
          return new;
        end $$;
        create trigger family_share_recipient_history before update or delete on care_plan_share_recipients for each row execute function guard_family_share_history();
        create trigger family_share_receipt_history before update or delete on care_plan_share_receipts for each row execute function guard_family_share_history();
        """);
    protected override void Down(MigrationBuilder m)=>throw new NotSupportedException("Retain recipient disclosure history; recover forwards.");
}
