using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20261115140000_AddVisitMediaEvidence")]
public sealed class AddVisitMediaEvidence : Migration
{
    protected override void Up(MigrationBuilder m)=>m.Sql("""
        create table visit_media_evidence(
          id uuid primary key,organization_id uuid not null,branch_id uuid not null,visit_id uuid not null references "Visits"("Id") on delete restrict,
          kind text not null check(kind in ('Photo','Signature')),caption text not null check(length(trim(caption)) between 5 and 1000),
          signer_name text not null,declaration text not null,capture_source text not null check(capture_source in ('Camera','File','Drawn')),
          consent_confirmed boolean not null check(consent_confirmed),file_name text not null,content_type text not null check(content_type in ('image/png','image/jpeg')),
          content bytea not null check(octet_length(content) between 1 and 2097152),sha256 char(64) not null,
          request_snapshot jsonb not null,recorded_by uuid not null,recorded_at timestamptz not null default now(),
          unique(organization_id,visit_id,kind,sha256),
          check((kind='Photo' and capture_source in ('Camera','File') and signer_name='' and declaration='') or (kind='Signature' and capture_source in ('File','Drawn'))),
          check(kind<>'Signature' or (length(trim(signer_name)) between 1 and 200 and length(trim(declaration)) between 10 and 2000 and content_type='image/png')));
        create table visit_media_withdrawals(
          evidence_id uuid primary key references visit_media_evidence(id),organization_id uuid not null,branch_id uuid not null,
          reason text not null check(length(trim(reason)) between 10 and 2000),withdrawn_by uuid not null,withdrawn_at timestamptz not null default now());
        create index ix_visit_media_scope on visit_media_evidence(organization_id,branch_id,visit_id,recorded_at);
        create function guard_visit_media_evidence() returns trigger language plpgsql as $$begin
          if TG_OP<>'INSERT' then raise exception 'Visit evidence history is immutable; record a governed withdrawal' using errcode='23514';end if;
          if TG_TABLE_NAME='visit_media_evidence' then
            perform 1 from "Visits" where "Id"=new.visit_id and "OrganizationId"=new.organization_id and "BranchId"=new.branch_id;
          else
            perform 1 from visit_media_evidence where id=new.evidence_id and organization_id=new.organization_id and branch_id=new.branch_id;
          end if;
          if not found then raise exception 'Visit evidence scope mismatch' using errcode='23514';end if;
          return new;
        end $$;
        create trigger visit_media_history before insert or update or delete on visit_media_evidence for each row execute function guard_visit_media_evidence();
        create trigger visit_media_withdrawal_history before insert or update or delete on visit_media_withdrawals for each row execute function guard_visit_media_evidence();
        """);
    protected override void Down(MigrationBuilder m)=>throw new NotSupportedException("Retain visit evidence and withdrawal audit; use an approved retention process rather than destructive rollback.");
}
