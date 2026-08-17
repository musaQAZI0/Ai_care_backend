using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260817233000_AddFamilyPortalGovernance")]
public sealed class AddFamilyPortalGovernance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            create table if not exists family_access_grants (
                id uuid primary key,
                family_member_id uuid not null,
                service_user_id uuid not null,
                authority_type text not null,
                verification_status text not null check (verification_status in ('Pending','Verified','Rejected','Expired','Revoked')),
                access_status text not null default 'PendingVerification' check (access_status in ('PendingVerification','Active','Suspended','Expired','Revoked')),
                verified_by_user_id uuid null,
                verified_by text not null default '',
                verified_at timestamptz null,
                valid_from timestamptz null,
                valid_until timestamptz null,
                revision bigint not null default 1 check (revision > 0),
                created_at timestamptz not null default now(),
                updated_at timestamptz not null default now(),
                organization_id uuid not null,
                branch_id uuid null,
                constraint fk_family_access_member foreign key (family_member_id) references "FamilyMembers"("Id") on delete cascade,
                constraint fk_family_access_person foreign key (service_user_id) references "ServiceUsers"("Id") on delete cascade,
                constraint ck_family_access_validity check (valid_until is null or valid_from is null or valid_until > valid_from)
            );
            create unique index if not exists ux_family_access_grant on family_access_grants(organization_id, family_member_id, service_user_id);
            create index if not exists ix_family_access_person on family_access_grants(organization_id, service_user_id, access_status);

            create table if not exists family_access_permissions (
                access_grant_id uuid not null,
                permission text not null,
                created_at timestamptz not null default now(),
                primary key (access_grant_id, permission),
                constraint fk_family_access_permission_grant foreign key (access_grant_id) references family_access_grants(id) on delete cascade,
                constraint ck_family_access_permission check (permission in ('ViewCareSummary','ViewTimeline','ViewVisits','ViewAppointments','ViewCarePlan','SignCarePlan','ViewDocuments','ViewMedicationSummary','ViewIncidentSummary','MessageCareTeam','SubmitFeedback','ViewFinance'))
            );

            create table if not exists family_portal_invitations (
                id uuid primary key,
                family_member_id uuid not null,
                token_hash text not null unique,
                email text not null,
                status text not null default 'Pending' check (status in ('Pending','Sent','Accepted','Expired','Revoked','Failed')),
                expires_at timestamptz not null,
                created_at timestamptz not null default now(),
                created_by_user_id uuid null,
                created_by text not null,
                accepted_at timestamptz null,
                revoked_at timestamptz null,
                provider_message_id text null,
                delivered_at timestamptz null,
                failed_at timestamptz null,
                failure_reason text null,
                organization_id uuid not null,
                branch_id uuid null,
                constraint fk_family_invitation_member foreign key (family_member_id) references "FamilyMembers"("Id") on delete cascade
            );
            create index if not exists ix_family_invitation_member on family_portal_invitations(organization_id, family_member_id, created_at desc);
            create unique index if not exists ux_family_invitation_open on family_portal_invitations(organization_id, family_member_id) where status in ('Pending','Sent');

            create table if not exists family_feedback_cases (
                id uuid primary key,
                service_user_id uuid not null,
                family_member_id uuid not null,
                type text not null check (type in ('Feedback','Compliment','Concern','Complaint','Suggestion')),
                subject text not null,
                description text not null,
                priority text not null default 'Routine' check (priority in ('Routine','Medium','High')),
                status text not null default 'Submitted' check (status in ('Submitted','Acknowledged','InReview','Responded','Resolved','Closed')),
                submitted_at timestamptz not null default now(),
                assigned_to_user_id uuid null,
                response_due_at timestamptz null,
                resolution text not null default '',
                resolved_at timestamptz null,
                organization_id uuid not null,
                branch_id uuid null,
                constraint fk_family_feedback_person foreign key (service_user_id) references "ServiceUsers"("Id") on delete cascade,
                constraint fk_family_feedback_member foreign key (family_member_id) references "FamilyMembers"("Id") on delete restrict
            );
            create index if not exists ix_family_feedback_person on family_feedback_cases(organization_id, service_user_id, submitted_at desc);

            create table if not exists family_document_access (
                document_id uuid primary key,
                visibility text not null default 'InternalOnly' check (visibility in ('InternalOnly','ServiceUserAndRepresentative','ExplicitFamilyAccess')),
                family_member_id uuid null,
                organization_id uuid not null,
                created_at timestamptz not null default now(),
                constraint fk_family_document_access_document foreign key (document_id) references "Documents"("Id") on delete cascade,
                constraint fk_family_document_access_member foreign key (family_member_id) references "FamilyMembers"("Id") on delete cascade
            );

            insert into family_access_grants (id, family_member_id, service_user_id, authority_type, verification_status, access_status, valid_from, revision, created_at, updated_at, organization_id, branch_id)
            select gen_random_uuid(), f."Id", f."ServiceUserId", coalesce(nullif(f."Relationship", ''), 'Family contact'), 'Pending', 'PendingVerification', now(), 1, now(), now(), f."OrganizationId", f."BranchId"
            from "FamilyMembers" f
            where f."OrganizationId" is not null
              and not exists (select 1 from family_access_grants g where g.organization_id = f."OrganizationId" and g.family_member_id = f."Id" and g.service_user_id = f."ServiceUserId");
        """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            drop table if exists family_document_access;
            drop table if exists family_feedback_cases;
            drop table if exists family_portal_invitations;
            drop table if exists family_access_permissions;
            drop table if exists family_access_grants;
        """);
    }
}
