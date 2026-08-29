using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260827183000_AddPersonLifecycleWorkflows")]
public sealed class AddPersonLifecycleWorkflows : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            create table if not exists person_referrals (
                id uuid primary key,
                service_user_id uuid not null,
                organization_id uuid not null,
                branch_id uuid not null,
                referral_source text not null,
                referrer_name text not null default '',
                referrer_contact text not null default '',
                referral_reason text not null,
                priority text not null default 'Routine',
                status text not null default 'Received',
                received_at timestamptz not null default now(),
                screening_due_at timestamptz null,
                owner text not null default '',
                outcome text not null default '',
                closure_reason text not null default '',
                created_by text not null default '',
                updated_at timestamptz not null default now(),
                constraint fk_person_referrals_service_users foreign key (service_user_id) references "ServiceUsers"("Id") on delete cascade
            );
            create index if not exists ix_person_referrals_queue on person_referrals(organization_id, branch_id, status, priority, screening_due_at);
            create index if not exists ix_person_referrals_person on person_referrals(organization_id, service_user_id, received_at desc);

            create table if not exists person_lifecycle_events (
                id uuid primary key,
                service_user_id uuid not null,
                organization_id uuid not null,
                branch_id uuid not null,
                event_type text not null,
                old_status text not null default '',
                new_status text not null default '',
                reason text not null,
                effective_at timestamptz not null default now(),
                created_by text not null default '',
                created_at timestamptz not null default now(),
                constraint fk_person_lifecycle_events_service_users foreign key (service_user_id) references "ServiceUsers"("Id") on delete cascade
            );
            create index if not exists ix_person_lifecycle_events_person on person_lifecycle_events(organization_id, service_user_id, created_at desc);

            create table if not exists person_review_checkpoints (
                id uuid primary key,
                service_user_id uuid not null,
                organization_id uuid not null,
                branch_id uuid not null,
                checkpoint_type text not null,
                status text not null default 'Open',
                due_at timestamptz not null,
                completed_at timestamptz null,
                owner text not null default '',
                outcome text not null default '',
                notes text not null default '',
                created_by text not null default '',
                created_at timestamptz not null default now(),
                updated_at timestamptz not null default now(),
                constraint fk_person_review_checkpoints_service_users foreign key (service_user_id) references "ServiceUsers"("Id") on delete cascade
            );
            create index if not exists ix_person_review_checkpoints_queue on person_review_checkpoints(organization_id, branch_id, status, due_at);
            create index if not exists ix_person_review_checkpoints_person on person_review_checkpoints(organization_id, service_user_id, due_at desc);
        """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            drop table if exists person_review_checkpoints;
            drop table if exists person_lifecycle_events;
            drop table if exists person_referrals;
        """);
    }
}
