using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261028090000_AddPrivateClaimEvidence")]
public sealed class AddPrivateClaimEvidence : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table finance_claim_evidence (
          id uuid primary key, organization_id uuid not null, branch_id uuid not null,
          care_worker_id uuid not null references "CareWorkers"("Id"),
          file_name text not null, content_type text not null,
          content bytea not null check(octet_length(content) between 1 and 10485760),
          sha256 char(64) not null check(sha256 ~ '^[A-F0-9]{64}$'),
          uploaded_by uuid not null, uploaded_at timestamptz not null default now(),
          unique(organization_id,care_worker_id,sha256));
        create function guard_claim_evidence() returns trigger language plpgsql as $$ begin
          if TG_OP<>'INSERT' then raise exception 'Receipt evidence is immutable' using errcode='23514'; end if;
          if not exists(select 1 from "CareWorkers" where "Id"=new.care_worker_id and "OrganizationId"=new.organization_id and "BranchId"=new.branch_id)
            then raise exception 'Evidence worker scope mismatch' using errcode='23514'; end if;
          return new;
        end $$;
        create trigger claim_evidence_guard before insert or update or delete on finance_claim_evidence for each row execute function guard_claim_evidence();
        alter table finance_workforce_claims
          add column evidence_id uuid null references finance_claim_evidence(id),
          add column evidence_sha256 char(64) null,
          add column receipt_issuer text null,
          add column receipt_number text null,
          add column receipt_identity text null;
        create unique index ux_active_claim_evidence on finance_workforce_claims(organization_id,evidence_sha256)
          where evidence_sha256 is not null and status<>'Rejected';
        create unique index ux_active_receipt_identity on finance_workforce_claims(organization_id,receipt_identity)
          where receipt_identity is not null and status<>'Rejected';
        create function validate_private_claim_evidence() returns trigger language plpgsql as $$
        declare evidence finance_claim_evidence%rowtype; policy finance_claim_policies%rowtype;
        begin
          -- Existing financial history is retained. New submissions and new approvals must meet the stronger controls.
          if TG_OP='UPDATE' and not (old.status='Submitted' and new.status='Approved') then return new; end if;
          select * into policy from finance_claim_policies where id=new.policy_id;
          if policy.evidence_required and new.evidence_id is null then
            raise exception 'Upload a scoped evidence document; a free-text reference is not receipt evidence' using errcode='23514'; end if;
          if new.evidence_id is not null then
            select * into evidence from finance_claim_evidence where id=new.evidence_id and organization_id=new.organization_id
              and branch_id=new.branch_id and care_worker_id=new.care_worker_id;
            if not found then raise exception 'Evidence document is not accessible for this claimant' using errcode='23514'; end if;
            new.evidence_sha256:=evidence.sha256;
          elsif TG_OP='INSERT' then new.evidence_sha256:=null;
          end if;
          if TG_OP='INSERT' and new.claim_type='Expense' then
            if length(trim(coalesce(new.receipt_issuer,'')))=0 or length(trim(coalesce(new.receipt_number,'')))=0 then
              raise exception 'Expense receipt issuer and receipt number are required' using errcode='23514'; end if;
            new.receipt_identity:=upper(regexp_replace(normalize(trim(new.receipt_issuer),NFKC),'\s+',' ','g')) || chr(31) ||
              upper(regexp_replace(normalize(trim(new.receipt_number),NFKC),'\s+',' ','g')) || chr(31) || new.claim_date::text || chr(31) || trim(new.currency);
          elsif TG_OP='INSERT' then new.receipt_identity:=null;
          end if;
          return new;
        end $$;
        create trigger a_private_claim_evidence_guard before insert or update on finance_workforce_claims
          for each row execute function validate_private_claim_evidence();
        create view finance_claim_evidence_findings as
          select c.id,c.organization_id,c.branch_id,c.status,'VALIDATED_EVIDENCE_REQUIRED'::text code
          from finance_workforce_claims c join finance_claim_policies p on p.id=c.policy_id
          where p.evidence_required and c.evidence_id is null;
        """);

    protected override void Down(MigrationBuilder m) => throw new NotSupportedException("Preserve receipt and claim history; recover forwards.");
}
