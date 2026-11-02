using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261102120000_AddDocumentLifecycle")]
public sealed class AddDocumentLifecycle : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        create table document_lifecycle_events (
          "Id" uuid primary key,
          "DocumentId" uuid not null references "Documents"("Id") on delete restrict,
          "EventType" varchar(32) not null check ("EventType" in ('Withdrawn','Superseded')),
          "Reason" varchar(300) not null check (length(trim("Reason")) > 0),
          "CreatedBy" varchar(200) not null,
          "CreatedAt" timestamptz not null,
          "OrganizationId" uuid not null,
          "BranchId" uuid not null,
          "ReplacementDocumentId" uuid null references "Documents"("Id") on delete restrict,
          constraint ck_document_lifecycle_replacement check (
            ("EventType" = 'Withdrawn' and "ReplacementDocumentId" is null) or
            ("EventType" = 'Superseded' and "ReplacementDocumentId" is not null and "ReplacementDocumentId" <> "DocumentId")),
          unique ("DocumentId"),
          unique ("ReplacementDocumentId")
        );
        create index ix_document_lifecycle_scope on document_lifecycle_events ("OrganizationId", "BranchId");
        create function guard_document_lifecycle() returns trigger language plpgsql as $$
        begin
          if TG_OP <> 'INSERT' then raise exception 'Document lifecycle history is append-only' using errcode='23514'; end if;
          if not exists (select 1 from "Documents" d where d."Id"=new."DocumentId" and d."OrganizationId"=new."OrganizationId" and d."BranchId"=new."BranchId") then
            raise exception 'Document lifecycle scope mismatch' using errcode='23514'; end if;
          if new."ReplacementDocumentId" is not null and not exists (
            select 1 from "Documents" source join "Documents" replacement on replacement."Id"=new."ReplacementDocumentId"
            where source."Id"=new."DocumentId" and replacement."ServiceUserId"=source."ServiceUserId"
              and replacement."OrganizationId"=source."OrganizationId" and replacement."BranchId"=source."BranchId"
              and lower(trim(replacement."Category")) <> 'withdrawn'
              and not exists(select 1 from document_lifecycle_events e where e."DocumentId"=replacement."Id")) then
            raise exception 'Replacement document must be an active document for the same person and scope' using errcode='23514'; end if;
          return new;
        end $$;
        create trigger document_lifecycle_append_only before insert or update or delete on document_lifecycle_events for each row execute function guard_document_lifecycle();
        create function guard_active_document_verification() returns trigger language plpgsql as $$
        begin
          if exists(select 1 from document_lifecycle_events e where e."DocumentId"=new.document_id) then
            raise exception 'Withdrawn or superseded documents cannot be verified as current' using errcode='23514'; end if;
          return new;
        end $$;
        create trigger active_document_verification before insert on person_document_verifications for each row execute function guard_active_document_verification();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Retain document lifecycle history; recover forwards.");
}
