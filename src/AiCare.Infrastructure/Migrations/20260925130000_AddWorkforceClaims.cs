using Microsoft.EntityFrameworkCore.Infrastructure;using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))][Migration("20260925130000_AddWorkforceClaims")]
public sealed class AddWorkforceClaims:Migration{protected override void Up(MigrationBuilder m)=>m.Sql("""
create table finance_workforce_claims(id uuid primary key,organization_id uuid not null,branch_id uuid not null,care_worker_id uuid not null references "CareWorkers"("Id"),visit_id uuid null references "Visits"("Id"),claim_type text not null,claim_date date not null,quantity numeric(10,2) not null check(quantity>=0),unit_rate numeric(12,4) not null check(unit_rate>=0),amount numeric(12,2) not null check(amount>=0),category text not null default '',notes text not null default '',evidence_reference text not null default '',status text not null default 'Submitted',submitted_by text not null,submitted_at timestamptz not null default now(),reviewed_by text null,reviewed_at timestamptz null,review_reason text not null default '',payroll_run_id uuid null references "PayrollRuns"("Id"),check(claim_type in ('Mileage','Expense')),check(status in ('Submitted','Approved','Rejected','Paid')));
create index ix_workforce_claims_queue on finance_workforce_claims(organization_id,branch_id,status,claim_date);
""");protected override void Down(MigrationBuilder m)=>m.Sql("drop table if exists finance_workforce_claims;");}
