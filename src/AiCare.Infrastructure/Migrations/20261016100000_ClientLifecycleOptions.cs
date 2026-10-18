using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261016100000_ClientLifecycleOptions")]
public sealed class ClientLifecycleOptions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table care_operation_options drop constraint ck_care_operation_option_kind;
        alter table care_operation_options add constraint ck_care_operation_option_kind
          check(kind in ('DiaryCategory','CareProvided','DurationReason','CancellationReason','JobTitle','WorkerLeavingReason','ClientStatus','ClientLeavingReason'));
        insert into care_operation_options(id,organization_id,kind,value)
        select gen_random_uuid(),o."Id",'ClientStatus',s.value
        from "Organizations" o cross join (values ('Referred'),('Assessment'),('Onboarding'),('Onboarded'),('Active'),('Suspended'),('Discharged')) s(value)
        on conflict(organization_id,kind,value) do nothing;
        create or replace function seed_client_status_options() returns trigger language plpgsql as $$
        begin
          insert into care_operation_options(id,organization_id,kind,value)
          select gen_random_uuid(),new."Id",'ClientStatus',s.value
          from (values ('Referred'),('Assessment'),('Onboarding'),('Onboarded'),('Active'),('Suspended'),('Discharged')) s(value);
          return new;
        end $$;
        create trigger trg_seed_client_status_options after insert on "Organizations"
          for each row execute function seed_client_status_options();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        drop trigger if exists trg_seed_client_status_options on "Organizations";
        drop function if exists seed_client_status_options();
        delete from care_operation_options where kind in ('ClientStatus','ClientLeavingReason');
        alter table care_operation_options drop constraint ck_care_operation_option_kind;
        alter table care_operation_options add constraint ck_care_operation_option_kind
          check(kind in ('DiaryCategory','CareProvided','DurationReason','CancellationReason','JobTitle','WorkerLeavingReason'));
        """);
}
