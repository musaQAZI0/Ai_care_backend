using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260904170000_AddAccessReviewGovernance")]
public sealed class AddAccessReviewGovernance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table access_review_campaigns (
            id uuid primary key, organization_id uuid not null, branch_id uuid null,
            name text not null, dormant_days integer not null, status text not null default 'Draft',
            due_at timestamptz not null, created_by_user_id uuid not null references "AppUsers"("Id") on delete restrict,
            created_by text not null, completed_at timestamptz null,
            created_at timestamptz not null default now(),
            constraint ck_access_review_status check (status in ('Draft','InProgress','Completed')),
            constraint ck_access_review_dormant_days check (dormant_days between 30 and 730)
        );
        create index ix_access_review_campaign_scope on access_review_campaigns(organization_id,branch_id,status,due_at);

        create table access_review_items (
            id uuid primary key, campaign_id uuid not null references access_review_campaigns(id) on delete cascade,
            organization_id uuid not null, branch_id uuid null, user_id uuid not null references "AppUsers"("Id") on delete restrict,
            user_name text not null, role text not null, account_active boolean not null, last_seen_at timestamptz null,
            severity text not null, finding_codes text[] not null, recommended_action text not null,
            status text not null default 'Pending', decision text null, justification text null,
            decided_by_user_id uuid null, decided_by text null, decided_at timestamptz null,
            remediated_at timestamptz null, remediation_detail text null,
            constraint uq_access_review_item unique(campaign_id,user_id),
            constraint ck_access_review_item_status check (status in ('Pending','Decided','Remediated'))
        );
        create index ix_access_review_items_campaign on access_review_items(organization_id,campaign_id,status,severity);

        create table access_review_events (
            id uuid primary key, campaign_id uuid not null, item_id uuid null,
            organization_id uuid not null, branch_id uuid null, event_type text not null,
            actor_user_id uuid null, actor text not null, detail text not null,
            occurred_at timestamptz not null default now()
        );
        create index ix_access_review_events_campaign on access_review_events(organization_id,campaign_id,occurred_at);
        create or replace function reject_access_review_event_mutation() returns trigger language plpgsql as $$
        begin raise exception 'access review events are immutable'; end $$;
        create trigger trg_access_review_events_immutable before update or delete on access_review_events
            for each row execute function reject_access_review_event_mutation();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop trigger if exists trg_access_review_events_immutable on access_review_events;
        drop function if exists reject_access_review_event_mutation();
        drop table if exists access_review_events;
        drop table if exists access_review_items;
        drop table if exists access_review_campaigns;
        """);
}
