using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AiCare.Infrastructure.Migrations;
[DbContext(typeof(CareDbContext))]
[Migration("20261101160000_AddCompletenessRemediation")]
public sealed class AddCompletenessRemediation : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create table person_completeness_events (
          id uuid primary key, service_user_id uuid not null references "ServiceUsers"("Id"), organization_id uuid not null, branch_id uuid not null,
          check_key text not null check(check_key in ('identity','emergency-contact','consent','capacity','person-centred-record','funding','assessment','care-plan')),
          revision integer not null check(revision>0), action text not null check(action in ('Assigned','ExceptionAccepted','Resolved')),
          owner_id uuid not null references "AppUsers"("Id"), owner_name text not null, due_at timestamptz not null,
          evidence text not null check(length(trim(evidence)) between 1 and 2000), exception_expires_at timestamptz null,
          check_snapshot jsonb not null, created_by uuid not null references "AppUsers"("Id"), created_by_name text not null,
          created_at timestamptz not null default now(), request_snapshot jsonb not null,
          unique(service_user_id,organization_id,branch_id,check_key,revision),
          check((action='ExceptionAccepted')=(exception_expires_at is not null))
        );
        create function guard_completeness_event() returns trigger language plpgsql as $$
        declare latest integer;
        begin
          if TG_OP<>'INSERT' then raise exception 'Completeness history is append-only' using errcode='23514'; end if;
          perform pg_advisory_xact_lock(hashtextextended(new.service_user_id::text,29));
          if not exists(select 1 from "ServiceUsers" p where p."Id"=new.service_user_id and p."OrganizationId"=new.organization_id and p."BranchId"=new.branch_id)
            or not exists(select 1 from "AppUsers" u where u."Id"=new.owner_id and u."OrganizationId"=new.organization_id and u."IsActive"
              and u."Role" in ('CareCoordinator','CareManager','Administrator') and (u."BranchId"=new.branch_id or (u."BranchId" is null and u."Role"='Administrator'))) then
            raise exception 'Select a current accountable owner in this branch' using errcode='23514'; end if;
          select coalesce(max(revision),0) into latest from person_completeness_events where service_user_id=new.service_user_id
            and organization_id=new.organization_id and branch_id=new.branch_id and check_key=new.check_key;
          if new.revision<>latest+1 then raise exception 'Completeness review changed' using errcode='23514'; end if;
          if new.action<>'Resolved' and new.due_at<=now() then raise exception 'A future remediation deadline is required' using errcode='23514'; end if;
          if new.action='ExceptionAccepted' and (new.exception_expires_at<=now() or not exists(select 1 from "AppUsers" u where u."Id"=new.created_by
            and u."OrganizationId"=new.organization_id and u."IsActive" and u."Role" in ('CareManager','Administrator')
            and (u."BranchId"=new.branch_id or u."Role"='Administrator'))) then
            raise exception 'A current manager and future exception expiry are required' using errcode='23514'; end if;
          if new.action='Resolved' and (new.check_snapshot->>'complete') is distinct from 'true' then
            raise exception 'Missing evidence cannot be marked resolved' using errcode='23514'; end if;
          return new;
        end $$;
        create trigger completeness_history_guard before insert or update or delete on person_completeness_events for each row execute function guard_completeness_event();
        """);
    protected override void Down(MigrationBuilder m) => throw new NotSupportedException("Retain completeness review history; recover forwards.");
}
