using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260829110000_AddReportingComplianceIntelligence")]
public sealed class AddReportingComplianceIntelligence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            create table if not exists report_runs (
                id uuid primary key,
                report_definition_id uuid null,
                organization_id uuid not null,
                branch_id uuid not null,
                name text not null,
                category text not null,
                format text not null,
                status text not null default 'Generated',
                filters_json jsonb not null default '{}'::jsonb,
                metrics_json jsonb not null default '{}'::jsonb,
                generated_by text not null default '',
                generated_at timestamptz not null default now(),
                constraint fk_report_runs_definition foreign key (report_definition_id) references "Reports"("Id") on delete set null
            );
            create index if not exists ix_report_runs_tenant on report_runs(organization_id, branch_id, generated_at desc);

            create table if not exists compliance_evidence_items (
                id uuid primary key,
                organization_id uuid not null,
                branch_id uuid not null,
                domain text not null,
                requirement text not null,
                evidence_type text not null,
                evidence_reference text not null,
                status text not null default 'Ready',
                owner text not null default '',
                review_due_at timestamptz null,
                notes text not null default '',
                created_by text not null default '',
                created_at timestamptz not null default now(),
                updated_at timestamptz not null default now()
            );
            create index if not exists ix_compliance_evidence_queue on compliance_evidence_items(organization_id, branch_id, status, review_due_at);

            create table if not exists compliance_actions (
                id uuid primary key,
                organization_id uuid not null,
                branch_id uuid not null,
                evidence_id uuid null,
                action_type text not null,
                detail text not null,
                owner text not null default '',
                status text not null default 'Open',
                due_at timestamptz null,
                completed_at timestamptz null,
                created_by text not null default '',
                created_at timestamptz not null default now(),
                constraint fk_compliance_actions_evidence foreign key (evidence_id) references compliance_evidence_items(id) on delete set null
            );
            create index if not exists ix_compliance_actions_queue on compliance_actions(organization_id, branch_id, status, due_at);
        """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            drop table if exists compliance_actions;
            drop table if exists compliance_evidence_items;
            drop table if exists report_runs;
        """);
    }
}
