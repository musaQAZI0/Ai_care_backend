using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20261115130000_AddCustomFormSubmissions")]
public sealed class AddCustomFormSubmissions : Migration
{
    protected override void Up(MigrationBuilder m)=>m.Sql("""
        create table custom_form_submissions(
          id uuid primary key,organization_id uuid not null,branch_id uuid not null,
          service_user_id uuid not null references "ServiceUsers"("Id") on delete restrict,
          template_version_id uuid not null references assessment_template_versions(id) on delete restrict,
          supersedes_id uuid unique null references custom_form_submissions(id),reason text not null,
          answers jsonb not null check(jsonb_typeof(answers)='object'),request_snapshot jsonb not null,
          created_by uuid not null,created_at timestamptz not null default now());
        create index ix_custom_form_person_scope on custom_form_submissions(organization_id,branch_id,service_user_id,created_at desc);
        create function guard_custom_form_submission() returns trigger language plpgsql as $$
        declare template assessment_template_versions%rowtype;prior custom_form_submissions%rowtype;prior_template assessment_template_versions%rowtype;begin
          if TG_OP<>'INSERT' then raise exception 'Custom form history is immutable; submit a reasoned correction' using errcode='23514';end if;
          perform 1 from "ServiceUsers" where "Id"=new.service_user_id and "OrganizationId"=new.organization_id and "BranchId"=new.branch_id;
          if not found then raise exception 'Custom form person scope mismatch' using errcode='23514';end if;
          select * into template from assessment_template_versions where id=new.template_version_id for share;
          if not found or template.organization_id<>new.organization_id or (template.branch_id is not null and template.branch_id<>new.branch_id) or
            template.assessment_type<>'Custom form' or template.status<>'Published' then
            raise exception 'Custom form requires an accessible published version' using errcode='23514';end if;
          if new.supersedes_id is not null then
            select * into prior from custom_form_submissions where id=new.supersedes_id;
            select * into prior_template from assessment_template_versions where id=prior.template_version_id;
            if prior.id is null or prior.organization_id<>new.organization_id or prior.branch_id<>new.branch_id or prior.service_user_id<>new.service_user_id or
              prior_template.template_key<>template.template_key or prior_template.branch_id is distinct from template.branch_id or length(trim(new.reason))<10 then
              raise exception 'Correction must retain person and form lineage and include a reason' using errcode='23514';end if;
          end if;
          return new;
        end $$;
        create trigger custom_form_submission_history before insert or update or delete on custom_form_submissions for each row execute function guard_custom_form_submission();
        """);
    protected override void Down(MigrationBuilder m)=>throw new NotSupportedException("Retain custom form versions, answers and correction history.");
}
