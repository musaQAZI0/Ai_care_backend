using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261101090000_AddAssessmentTemplates")]
public sealed class AddAssessmentTemplates : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table assessment_template_versions (
          id uuid primary key, organization_id uuid not null references "Organizations"("Id"),
          branch_id uuid null references "Branches"("Id"), template_key text not null,
          title text not null, assessment_type text not null, version integer not null check(version>0),
          definition_json jsonb not null check(jsonb_typeof(definition_json)='array'),
          status text not null default 'Draft' check(status in ('Draft','InReview','Published','Retired')),
          source_version_id uuid null references assessment_template_versions(id),
          change_reason text not null, impact_review text not null, request_snapshot jsonb not null,
          created_by uuid not null, created_at timestamptz not null default now(),
          submitted_at timestamptz null, approved_by uuid null, approved_at timestamptz null,
          approval_reason text not null default '',
          check(approved_by is null or (approved_by<>created_by and approved_at is not null))
        );
        create unique index ux_assessment_template_version on assessment_template_versions
          (organization_id,coalesce(branch_id,'00000000-0000-0000-0000-000000000000'::uuid),template_key,version);
        create unique index ux_assessment_template_published on assessment_template_versions
          (organization_id,coalesce(branch_id,'00000000-0000-0000-0000-000000000000'::uuid),template_key) where status='Published';
        create function govern_assessment_template_version() returns trigger language plpgsql as $$ begin
          if TG_OP='DELETE' then raise exception 'Template history cannot be deleted' using errcode='23514'; end if;
          if new.branch_id is not null and not exists(select 1 from "Branches" where "Id"=new.branch_id and "OrganizationId"=new.organization_id)
            then raise exception 'Template branch is outside its organization' using errcode='23514'; end if;
          if new.source_version_id is not null and not exists(select 1 from assessment_template_versions where id=new.source_version_id and organization_id=new.organization_id)
            then raise exception 'Source template is outside its organization' using errcode='23514'; end if;
          if TG_OP='INSERT' then
            if new.status<>'Draft' or new.approved_by is not null then raise exception 'Create a draft before review' using errcode='23514'; end if;
          else
            if (to_jsonb(new)-'status'-'submitted_at'-'approved_by'-'approved_at'-'approval_reason') is distinct from
               (to_jsonb(old)-'status'-'submitted_at'-'approved_by'-'approved_at'-'approval_reason')
              then raise exception 'Template definitions are immutable; create a new version' using errcode='23514'; end if;
            if not ((old.status='Draft' and new.status='InReview' and new.submitted_at is not null and new.approved_by is null)
                or (old.status='InReview' and new.status='Published' and new.approved_by is not null and length(trim(new.approval_reason))>0 and new.submitted_at=old.submitted_at)
                or (old.status='Published' and new.status='Retired' and new.approved_by=old.approved_by and new.approved_at=old.approved_at and new.approval_reason=old.approval_reason and new.submitted_at=old.submitted_at))
              then raise exception 'Invalid template review transition' using errcode='23514'; end if;
          end if; return new;
        end $$;
        create trigger assessment_template_guard before insert or update or delete on assessment_template_versions
          for each row execute function govern_assessment_template_version();

        alter table governed_assessments add column template_version_id uuid null references assessment_template_versions(id);
        create function pin_governed_assessment_template() returns trigger language plpgsql as $$
        declare t assessment_template_versions%rowtype;
        begin
          if TG_OP='DELETE' then raise exception 'Assessment history cannot be deleted' using errcode='23514'; end if;
          if TG_OP='UPDATE' then
            if not ((old.status='Draft' and new.status='InReview') or (old.status='InReview' and new.status='Current') or
                    (old.status='Current' and new.status='Superseded'))
              then raise exception 'Invalid assessment review transition' using errcode='23514'; end if;
            if (to_jsonb(new)-'status'-'submitted_at'-'approved_at'-'approved_by'-'signed_at'-'signed_by'-'signature_declaration') is distinct from
               (to_jsonb(old)-'status'-'submitted_at'-'approved_at'-'approved_by'-'signed_at'-'signed_by'-'signature_declaration')
              then raise exception 'Assessment content is immutable; create a correction' using errcode='23514'; end if;
            if old.status in ('Current','Superseded','Corrected') and
               (to_jsonb(new)-'status') is distinct from (to_jsonb(old)-'status')
              then raise exception 'Signed assessment evidence is immutable' using errcode='23514'; end if;
          end if;
          if TG_OP='INSERT' and new.template_version_id is not null then
            select * into t from assessment_template_versions where id=new.template_version_id;
            if not found or t.status<>'Published' or t.organization_id<>new.organization_id or
               (t.branch_id is not null and t.branch_id<>new.branch_id) or t.template_key<>new.template_key or
               t.version::text<>new.template_version or t.assessment_type<>new.assessment_type
              then raise exception 'An assessment must pin an approved template in its own scope' using errcode='23514'; end if;
          elsif TG_OP='INSERT' and exists(select 1 from assessment_template_versions where organization_id=new.organization_id and template_key=new.template_key)
            then raise exception 'Managed templates require an explicit approved version' using errcode='23514';
          end if;return new;
        end $$;
        create trigger governed_assessment_template_guard before insert or update or delete on governed_assessments
          for each row execute function pin_governed_assessment_template();
        """);

    protected override void Down(MigrationBuilder m) => throw new NotSupportedException("Retain assessment/template history; recover forwards.");
}
