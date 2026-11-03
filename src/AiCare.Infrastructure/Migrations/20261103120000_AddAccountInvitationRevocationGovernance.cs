using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261103120000_AddAccountInvitationRevocationGovernance")]
public sealed class AddAccountInvitationRevocationGovernance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table account_invitations add column if not exists revoked_by_user_id uuid null;
        alter table account_invitations add column if not exists revoked_by text null;
        alter table account_invitations add column if not exists revocation_reason text null;
        alter table account_invitations drop constraint if exists ck_account_invitation_revocation_evidence;
        alter table account_invitations add constraint ck_account_invitation_revocation_evidence check (
          status <> 'Revoked' or (
            revoked_at is not null and revoked_by_user_id is not null and
            length(trim(coalesce(revoked_by, ''))) > 0 and
            length(trim(coalesce(revocation_reason, ''))) between 10 and 500
          )
        ) not valid;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table account_invitations drop constraint if exists ck_account_invitation_revocation_evidence;
        alter table account_invitations drop column if exists revocation_reason;
        alter table account_invitations drop column if exists revoked_by;
        alter table account_invitations drop column if exists revoked_by_user_id;
        """);
}
