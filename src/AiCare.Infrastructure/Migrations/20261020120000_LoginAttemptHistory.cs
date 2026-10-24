using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261020120000_LoginAttemptHistory")]
public sealed class LoginAttemptHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table auth_login_attempts (
            id uuid primary key,
            organization_id uuid not null references "Organizations"("Id") on delete cascade,
            user_id uuid not null,
            branch_id uuid null,
            outcome text not null check (outcome in ('Succeeded','InvalidPassword','Locked','MfaFailed','MfaLocked','MfaEnrollmentRequired')),
            ip_address text not null,
            occurred_at timestamptz not null default now()
        );
        create index ix_auth_login_attempts_org_time on auth_login_attempts(organization_id,occurred_at desc);
        create index ix_auth_login_attempts_user_time on auth_login_attempts(user_id,occurred_at desc);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("drop table if exists auth_login_attempts;");
}
