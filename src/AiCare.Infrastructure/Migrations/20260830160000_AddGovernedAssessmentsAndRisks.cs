using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20260830160000_AddGovernedAssessmentsAndRisks")]
public sealed class AddGovernedAssessmentsAndRisks:Migration
{
 protected override void Up(MigrationBuilder migrationBuilder)=>migrationBuilder.Sql("""
 create table governed_assessments(
  id uuid primary key,service_user_id uuid not null,organization_id uuid not null,branch_id uuid not null,
  assessment_type text not null,template_key text not null,template_version text not null,answers_json jsonb not null,
  score integer not null,risk_level text not null,summary text not null,recommended_actions text not null,
  status text not null,version integer not null,supersedes_id uuid null,change_reason text not null default '',
  assessor_name text not null,assessor_role text not null,review_due_at timestamptz not null,
  submitted_at timestamptz null,approved_at timestamptz null,approved_by text not null default '',
  signed_at timestamptz null,signed_by text not null default '',signature_declaration text not null default '',
  created_by text not null,created_at timestamptz not null default now(),
  constraint fk_governed_assessment_person foreign key(service_user_id) references "ServiceUsers"("Id") on delete restrict,
  constraint fk_governed_assessment_previous foreign key(supersedes_id) references governed_assessments(id) on delete restrict,
  constraint ck_governed_assessment_status check(status in ('Draft','InReview','Current','Superseded','Corrected'))
 );
 create unique index ux_governed_assessment_current on governed_assessments(organization_id,service_user_id,lower(assessment_type)) where status='Current';
 create index ix_governed_assessment_review on governed_assessments(organization_id,branch_id,status,review_due_at);

 create table governed_risk_assessments(
  id uuid primary key,service_user_id uuid not null,organization_id uuid not null,branch_id uuid not null,
  category text not null,hazard text not null,likelihood integer not null,severity integer not null,score integer not null,
  risk_level text not null,controls text not null,contingency text not null,owner text not null,
  status text not null,version integer not null,supersedes_id uuid null,change_reason text not null default '',
  review_due_at timestamptz not null,submitted_at timestamptz null,approved_at timestamptz null,approved_by text not null default '',
  signed_at timestamptz null,signed_by text not null default '',signature_declaration text not null default '',
  created_by text not null,created_at timestamptz not null default now(),
  constraint fk_governed_risk_person foreign key(service_user_id) references "ServiceUsers"("Id") on delete restrict,
  constraint fk_governed_risk_previous foreign key(supersedes_id) references governed_risk_assessments(id) on delete restrict,
  constraint ck_governed_risk_status check(status in ('Draft','InReview','Current','Superseded','Corrected')),
  constraint ck_governed_risk_scores check(likelihood between 1 and 5 and severity between 1 and 5 and score=likelihood*severity)
 );
 create unique index ux_governed_risk_current on governed_risk_assessments(organization_id,service_user_id,lower(category)) where status='Current';
 create index ix_governed_risk_review on governed_risk_assessments(organization_id,branch_id,status,review_due_at);
 """);
 protected override void Down(MigrationBuilder migrationBuilder)=>migrationBuilder.Sql("drop table governed_risk_assessments;drop table governed_assessments;");
}
