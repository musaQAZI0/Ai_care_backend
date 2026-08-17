using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260817162000_AddCriticalWebSafetyWorkflows")]
public sealed class AddCriticalWebSafetyWorkflows : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            create table if not exists medication_safety_profiles (
                medication_id uuid primary key,
                organization_id uuid not null,
                branch_id uuid not null,
                indication text not null default '',
                prescriber text not null default '',
                form text not null default '',
                strength text not null default '',
                start_date timestamptz null,
                end_date timestamptz null,
                dose_window_minutes integer not null default 60,
                max_prn_doses_24h integer null,
                min_prn_interval_minutes integer null,
                prn_indication text not null default '',
                prn_effect_review_minutes integer null,
                stock_on_hand numeric(12,2) null,
                reorder_level numeric(12,2) null,
                requires_witness boolean not null default false,
                last_reconciled_at timestamptz null,
                reconciled_by text not null default '',
                updated_at timestamptz not null default now(),
                constraint fk_medication_safety_medication foreign key (medication_id) references \"Medications\"(\"Id\") on delete cascade
            );
            create index if not exists ix_medication_safety_tenant on medication_safety_profiles(organization_id, branch_id);

            create table if not exists mar_safety_events (
                id uuid primary key,
                mar_record_id uuid not null,
                organization_id uuid not null,
                branch_id uuid not null,
                event_type text not null,
                reason text not null default '',
                effect text not null default '',
                witnessed_by text not null default '',
                stock_delta numeric(12,2) null,
                created_by text not null,
                created_at timestamptz not null default now(),
                constraint fk_mar_safety_mar foreign key (mar_record_id) references \"MedicationAdministrationRecords\"(\"Id\") on delete cascade
            );
            create index if not exists ix_mar_safety_record on mar_safety_events(organization_id, branch_id, mar_record_id, created_at);

            create table if not exists safeguarding_cases (
                id uuid primary key,
                service_user_id uuid not null,
                incident_id uuid null,
                organization_id uuid not null,
                branch_id uuid not null,
                category text not null,
                concern text not null,
                immediate_actions text not null default '',
                risk_level text not null,
                status text not null default 'Open',
                external_referral text not null default '',
                referral_reference text not null default '',
                owner text not null default '',
                opened_at timestamptz not null default now(),
                review_due_at timestamptz null,
                closed_at timestamptz null,
                closure_summary text not null default '',
                created_by text not null,
                updated_at timestamptz not null default now(),
                constraint fk_safeguarding_person foreign key (service_user_id) references \"ServiceUsers\"(\"Id\") on delete cascade,
                constraint fk_safeguarding_incident foreign key (incident_id) references \"Incidents\"(\"Id\") on delete set null
            );
            create index if not exists ix_safeguarding_person on safeguarding_cases(organization_id, branch_id, service_user_id, status);

            create table if not exists safeguarding_case_actions (
                id uuid primary key,
                case_id uuid not null,
                action_type text not null,
                detail text not null,
                owner text not null default '',
                due_at timestamptz null,
                completed_at timestamptz null,
                status text not null default 'Open',
                created_at timestamptz not null default now(),
                constraint fk_safeguarding_action_case foreign key (case_id) references safeguarding_cases(id) on delete cascade
            );
            create index if not exists ix_safeguarding_actions_case on safeguarding_case_actions(case_id, status, due_at);

            create table if not exists auth_refresh_tokens (
                id uuid primary key,
                user_id uuid not null,
                token_hash text not null unique,
                expires_at timestamptz not null,
                revoked_at timestamptz null,
                replaced_by_token_hash text null,
                created_at timestamptz not null default now(),
                created_ip text not null default '',
                constraint fk_refresh_user foreign key (user_id) references \"AppUsers\"(\"Id\") on delete cascade
            );
            create index if not exists ix_refresh_user on auth_refresh_tokens(user_id, expires_at);

            create table if not exists password_reset_tokens (
                id uuid primary key,
                user_id uuid not null,
                token_hash text not null unique,
                expires_at timestamptz not null,
                used_at timestamptz null,
                created_at timestamptz not null default now(),
                constraint fk_reset_user foreign key (user_id) references \"AppUsers\"(\"Id\") on delete cascade
            );
            create index if not exists ix_reset_user on password_reset_tokens(user_id, expires_at);

            create table if not exists auth_user_security (
                user_id uuid primary key,
                failed_attempts integer not null default 0,
                lockout_until timestamptz null,
                mfa_secret text null,
                mfa_enabled boolean not null default false,
                updated_at timestamptz not null default now(),
                constraint fk_security_user foreign key (user_id) references \"AppUsers\"(\"Id\") on delete cascade
            );
        """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            drop table if exists safeguarding_case_actions;
            drop table if exists safeguarding_cases;
            drop table if exists mar_safety_events;
            drop table if exists medication_safety_profiles;
            drop table if exists auth_refresh_tokens;
            drop table if exists password_reset_tokens;
            drop table if exists auth_user_security;
        """);
    }
}
