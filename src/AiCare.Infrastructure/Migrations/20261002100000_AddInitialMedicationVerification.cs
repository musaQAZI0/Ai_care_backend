using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261002100000_AddInitialMedicationVerification")]
public sealed class AddInitialMedicationVerification : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table medication_initial_verification_requests(
          id uuid primary key,
          medication_id uuid not null references "Medications"("Id") on delete restrict,
          organization_id uuid not null, branch_id uuid not null,
          submitted_profile_version integer not null,
          profile_snapshot jsonb not null,
          status text not null check(status in ('Pending','Approved','Rejected')),
          submitted_by_user_id uuid not null,
          submitted_by text not null,
          submitted_at timestamptz not null default now(),
          reviewed_by_user_id uuid null,
          reviewed_by text not null default '',
          reviewed_at timestamptz null,
          decision_reason text not null default ''
        );
        create unique index ux_medication_initial_pending
          on medication_initial_verification_requests(organization_id,branch_id,medication_id)
          where status='Pending';
        create index ix_medication_initial_review
          on medication_initial_verification_requests(organization_id,branch_id,status,submitted_at);
        create function protect_initial_verification_evidence() returns trigger language plpgsql as $$
        begin
          if old.medication_id is distinct from new.medication_id
            or old.organization_id is distinct from new.organization_id
            or old.branch_id is distinct from new.branch_id
            or old.submitted_profile_version is distinct from new.submitted_profile_version
            or old.profile_snapshot is distinct from new.profile_snapshot
            or old.submitted_by_user_id is distinct from new.submitted_by_user_id
            or old.submitted_by is distinct from new.submitted_by
            or old.submitted_at is distinct from new.submitted_at then
            raise exception 'Initial medication verification evidence is immutable';
          end if;
          return new;
        end $$;
        create trigger initial_verification_evidence_immutable
          before update on medication_initial_verification_requests
          for each row execute function protect_initial_verification_evidence();
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop trigger if exists initial_verification_evidence_immutable on medication_initial_verification_requests;
        drop function if exists protect_initial_verification_evidence();
        drop table medication_initial_verification_requests;
        """);
}
