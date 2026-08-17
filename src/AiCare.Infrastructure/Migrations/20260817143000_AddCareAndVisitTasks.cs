using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260817143000_AddCareAndVisitTasks")]
public sealed class AddCareAndVisitTasks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            create table if not exists care_plan_tasks (
                id uuid primary key,
                care_plan_id uuid not null,
                service_user_id uuid not null,
                organization_id uuid not null,
                branch_id uuid not null,
                title text not null,
                category text not null default 'General',
                instructions text not null default '',
                is_required boolean not null default true,
                frequency text not null default 'Every visit',
                status text not null default 'Active',
                created_at timestamptz not null default now(),
                constraint fk_care_plan_tasks_plan foreign key (care_plan_id) references "CarePlans"("Id") on delete cascade,
                constraint fk_care_plan_tasks_person foreign key (service_user_id) references "ServiceUsers"("Id") on delete cascade
            );
            create index if not exists ix_care_plan_tasks_plan on care_plan_tasks(organization_id, branch_id, care_plan_id);

            create table if not exists visit_tasks (
                id uuid primary key,
                visit_id uuid not null,
                care_plan_task_id uuid null,
                service_user_id uuid not null,
                care_worker_id uuid not null,
                organization_id uuid not null,
                branch_id uuid not null,
                title text not null,
                category text not null default 'General',
                instructions text not null default '',
                is_required boolean not null default true,
                status text not null default 'Pending',
                outcome text not null default '',
                exception_reason text not null default '',
                completed_at timestamptz null,
                created_at timestamptz not null default now(),
                constraint fk_visit_tasks_visit foreign key (visit_id) references "Visits"("Id") on delete cascade,
                constraint fk_visit_tasks_plan_task foreign key (care_plan_task_id) references care_plan_tasks(id) on delete set null
            );
            create index if not exists ix_visit_tasks_visit on visit_tasks(organization_id, branch_id, visit_id);
            create unique index if not exists ux_visit_tasks_plan_task on visit_tasks(visit_id, care_plan_task_id) where care_plan_task_id is not null;
        """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            drop table if exists visit_tasks;
            drop table if exists care_plan_tasks;
        """);
    }
}
