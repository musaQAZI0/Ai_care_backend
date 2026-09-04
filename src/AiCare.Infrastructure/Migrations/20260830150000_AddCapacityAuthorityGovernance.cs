using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260830150000_AddCapacityAuthorityGovernance")]
public sealed class AddCapacityAuthorityGovernance:Migration
{
 protected override void Up(MigrationBuilder migrationBuilder)=>migrationBuilder.Sql("""
  alter table consent_records add column if not exists version integer not null default 1;
  alter table consent_records add column if not exists supersedes_id uuid null references consent_records(id) on delete restrict;
  alter table consent_records add column if not exists information_categories text not null default '';
  alter table consent_records add column if not exists sharing_parties text not null default '';
  alter table consent_records add column if not exists review_due_at timestamptz null;

  create table capacity_decisions(
   id uuid primary key,service_user_id uuid not null,organization_id uuid not null,branch_id uuid not null,
   decision_context text not null,assessment_reason text not null,outcome text not null,
   can_understand boolean not null,can_retain boolean not null,can_use_or_weigh boolean not null,can_communicate boolean not null,
   support_provided text not null,assessor_name text not null,assessor_role text not null,evidence_reference text not null default '',
   assessed_at timestamptz not null,review_due_at timestamptz null,status text not null default 'Current',version integer not null,
   supersedes_id uuid null,best_interest_required boolean not null,created_by text not null,created_at timestamptz not null default now(),
   constraint fk_capacity_person foreign key(service_user_id) references "ServiceUsers"("Id") on delete restrict,
   constraint fk_capacity_supersedes foreign key(supersedes_id) references capacity_decisions(id) on delete restrict,
   constraint ck_capacity_outcome check(outcome in ('HasCapacity','LacksCapacity'))
  );
  create unique index ux_capacity_current_context on capacity_decisions(organization_id,service_user_id,lower(decision_context)) where status='Current';
  create index ix_capacity_review on capacity_decisions(organization_id,branch_id,status,review_due_at);

  create table best_interest_decisions(
   id uuid primary key,capacity_decision_id uuid not null,service_user_id uuid not null,organization_id uuid not null,branch_id uuid not null,
   decision text not null,options_considered text not null,consulted_people text not null,person_wishes text not null,
   risks_and_benefits text not null,least_restrictive_reason text not null,decision_maker text not null,decided_at timestamptz not null,
   review_due_at timestamptz null,status text not null default 'Current',created_by text not null,created_at timestamptz not null default now(),
   constraint fk_best_interest_capacity foreign key(capacity_decision_id) references capacity_decisions(id) on delete restrict,
   constraint fk_best_interest_person foreign key(service_user_id) references "ServiceUsers"("Id") on delete restrict
  );
  create unique index ux_best_interest_capacity on best_interest_decisions(capacity_decision_id) where status='Current';

  create table authority_records(
   id uuid primary key,service_user_id uuid not null,organization_id uuid not null,branch_id uuid not null,
   authority_type text not null,holder_name text not null,holder_relationship text not null,scope text not null,
   verification_status text not null,verification_reference text not null,valid_from timestamptz not null,valid_until timestamptz null,
   status text not null default 'Active',version integer not null,supersedes_id uuid null,revoked_at timestamptz null,
   revocation_reason text not null default '',created_by text not null,created_at timestamptz not null default now(),
   constraint fk_authority_person foreign key(service_user_id) references "ServiceUsers"("Id") on delete restrict,
   constraint fk_authority_supersedes foreign key(supersedes_id) references authority_records(id) on delete restrict
  );
  create index ix_authority_person on authority_records(organization_id,branch_id,service_user_id,status,valid_until);
 """);
 protected override void Down(MigrationBuilder migrationBuilder)=>migrationBuilder.Sql("""
  drop table authority_records;drop table best_interest_decisions;drop table capacity_decisions;
  alter table consent_records drop column if exists review_due_at,drop column if exists sharing_parties,drop column if exists information_categories,drop column if exists supersedes_id,drop column if exists version;
 """);
}
