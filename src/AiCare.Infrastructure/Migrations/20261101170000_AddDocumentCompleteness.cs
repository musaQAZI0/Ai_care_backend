using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20261101170000_AddDocumentCompleteness")]
public sealed class AddDocumentCompleteness : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table person_document_requirements (
          id uuid primary key,root_id uuid not null,version integer not null check(version>0),
          service_user_id uuid not null references "ServiceUsers"("Id"),organization_id uuid not null,branch_id uuid not null,
          title text not null check(length(trim(title)) between 1 and 200),category text not null check(length(trim(category)) between 1 and 200),
          expiry_required boolean not null,enabled boolean not null,reason text not null check(length(trim(reason)) between 1 and 2000),
          created_by uuid not null references "AppUsers"("Id"),created_at timestamptz not null default now(),request_snapshot jsonb not null,
          unique(root_id,version)
        );
        create table person_document_verifications (
          id uuid primary key,document_id uuid not null references "Documents"("Id"),revision integer not null check(revision>0),
          service_user_id uuid not null references "ServiceUsers"("Id"),organization_id uuid not null,branch_id uuid not null,
          status text not null check(status in ('Verified','Rejected')),valid_until timestamptz null,
          evidence text not null check(length(trim(evidence)) between 1 and 2000),document_snapshot jsonb not null,
          created_by uuid not null references "AppUsers"("Id"),created_at timestamptz not null default now(),request_snapshot jsonb not null,
          unique(document_id,revision),check(status='Verified' or valid_until is null)
        );
        create function guard_document_completeness() returns trigger language plpgsql as $$
        declare latest integer; snapshot jsonb;
        begin
          if TG_OP<>'INSERT' then raise exception 'Document completeness history is append-only' using errcode='23514'; end if;
          perform pg_advisory_xact_lock(hashtextextended(new.service_user_id::text,29));
          if not exists(select 1 from "ServiceUsers" p where p."Id"=new.service_user_id and p."OrganizationId"=new.organization_id and p."BranchId"=new.branch_id) then
            raise exception 'Document requirement person scope mismatch' using errcode='23514'; end if;
          if TG_TABLE_NAME='person_document_requirements' then
            select coalesce(max(version),0) into latest from person_document_requirements where root_id=new.root_id;
            if new.version<>latest+1 or (latest=0 and new.root_id<>new.id) or (latest>0 and not exists(select 1 from person_document_requirements r
              where r.root_id=new.root_id and r.service_user_id=new.service_user_id and r.organization_id=new.organization_id and r.branch_id=new.branch_id)) then
              raise exception 'Document requirement lineage changed' using errcode='23514'; end if;
          else
            select to_jsonb(d) into snapshot from "Documents" d where d."Id"=new.document_id and d."ServiceUserId"=new.service_user_id
              and d."OrganizationId"=new.organization_id and d."BranchId"=new.branch_id and lower(trim(d."Category"))<>'withdrawn' for share;
            if snapshot is null then raise exception 'Select a document in the same person scope' using errcode='23514'; end if;
            select coalesce(max(revision),0) into latest from person_document_verifications where document_id=new.document_id;
            if new.revision<>latest+1 or new.valid_until<=now() then raise exception 'Verification changed or expiry is not future' using errcode='23514'; end if;
            new.document_snapshot=snapshot;
          end if;
          return new;
        end $$;
        create trigger document_requirement_history before insert or update or delete on person_document_requirements for each row execute function guard_document_completeness();
        create trigger document_verification_history before insert or update or delete on person_document_verifications for each row execute function guard_document_completeness();
        alter table person_completeness_events drop constraint person_completeness_events_check_key_check;
        alter table person_completeness_events add constraint person_completeness_events_check_key_check
          check(check_key in ('identity','emergency-contact','consent','capacity','person-centred-record','funding','assessment','care-plan')
            or check_key ~ '^document:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$');
        """);
    protected override void Down(MigrationBuilder m) => throw new NotSupportedException("Retain document review history; recover forwards.");
}
