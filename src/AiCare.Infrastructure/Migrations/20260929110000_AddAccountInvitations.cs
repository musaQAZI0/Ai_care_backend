using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20260929110000_AddAccountInvitations")]
public sealed class AddAccountInvitations:Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)=>migrationBuilder.Sql("""
        create table if not exists account_invitations(
          id uuid primary key,user_id uuid not null,token_hash text not null unique,email text not null,role text not null,
          status text not null check(status in('Pending','Sent','Accepted','Expired','Revoked','Failed')),
          expires_at timestamptz not null,created_at timestamptz not null default now(),created_by_user_id uuid not null,created_by text not null,
          accepted_at timestamptz null,accepted_terms_at timestamptz null,revoked_at timestamptz null,failed_at timestamptz null,failure_reason text null,
          organization_id uuid not null,branch_id uuid null,
          constraint fk_account_invitation_user foreign key(user_id) references "AppUsers"("Id") on delete cascade);
        create index if not exists ix_account_invitation_user on account_invitations(organization_id,user_id,created_at desc);
        create unique index if not exists ux_account_invitation_open on account_invitations(organization_id,user_id) where status in('Pending','Sent');
        """);
    protected override void Down(MigrationBuilder migrationBuilder)=>migrationBuilder.Sql("drop table if exists account_invitations;");
}
