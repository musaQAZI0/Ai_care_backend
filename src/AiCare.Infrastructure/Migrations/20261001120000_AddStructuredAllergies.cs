using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261001120000_AddStructuredAllergies")]
public sealed class AddStructuredAllergies : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table service_user_allergy_status (
          service_user_id uuid primary key references "ServiceUsers"("Id") on delete cascade,
          organization_id uuid not null, branch_id uuid not null,
          status text not null check (status in ('Unknown','KnownAllergies','NoKnownAllergies')),
          source text not null default '', reviewed_by_user_id uuid null, reviewed_by text not null default '',
          reviewed_at timestamptz null, updated_at timestamptz not null default now());
        create table service_user_allergies (
          id uuid primary key, service_user_id uuid not null references "ServiceUsers"("Id") on delete cascade,
          organization_id uuid not null, branch_id uuid not null,
          allergen text not null, allergen_type text not null check (allergen_type in ('Medicine','Ingredient','Food','Environmental','Other')),
          terminology_code text not null default '', terminology_system text not null default '',
          reaction text not null, severity text not null check (severity in ('Mild','Moderate','Severe','Unknown')),
          record_type text not null check (record_type in ('Allergy','Intolerance','AdverseReaction')),
          clinical_status text not null check (clinical_status in ('Active','Resolved','EnteredInError')),
          verification_status text not null check (verification_status in ('Unverified','Verified')),
          information_source text not null, notes text not null default '',
          recorded_by_user_id uuid not null, recorded_by text not null, recorded_at timestamptz not null default now(),
          reviewed_by_user_id uuid null, reviewed_by text not null default '', reviewed_at timestamptz null,
          version integer not null default 1);
        create index ix_service_user_allergies_person on service_user_allergies(organization_id,branch_id,service_user_id,clinical_status);
        create table service_user_allergy_history (
          id uuid primary key, service_user_id uuid not null, allergy_id uuid null,
          organization_id uuid not null, branch_id uuid not null, action text not null,
          snapshot jsonb not null, reason text not null default '',
          actor_user_id uuid not null, actor text not null, occurred_at timestamptz not null default now());
        create index ix_service_user_allergy_history_person on service_user_allergy_history(organization_id,branch_id,service_user_id,occurred_at desc);
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop table service_user_allergy_history;
        drop table service_user_allergies;
        drop table service_user_allergy_status;
        """);
}
