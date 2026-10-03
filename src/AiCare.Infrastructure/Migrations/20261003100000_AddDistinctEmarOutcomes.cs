using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261003100000_AddDistinctEmarOutcomes")]
public sealed class AddDistinctEmarOutcomes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table emar_ledger drop constraint ck_emar_ledger_event;
        alter table emar_ledger add constraint ck_emar_ledger_event
          check(event_type in ('Administration','Omission','Refusal','Held','NotRequired','Correction','PRNEffect'));
        drop index ux_emar_terminal_event;
        create unique index ux_emar_terminal_event on emar_ledger(mar_record_id)
          where event_type in ('Administration','Omission','Refusal','Held','NotRequired');
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        do $$ begin
          if exists(select 1 from emar_ledger where event_type in ('Held','NotRequired')) then
            raise exception 'Cannot roll back distinct eMAR outcomes while Held or NotRequired history exists';
          end if;
        end $$;
        drop index ux_emar_terminal_event;
        create unique index ux_emar_terminal_event on emar_ledger(mar_record_id)
          where event_type in ('Administration','Omission','Refusal');
        alter table emar_ledger drop constraint ck_emar_ledger_event;
        alter table emar_ledger add constraint ck_emar_ledger_event
          check(event_type in ('Administration','Omission','Refusal','Correction','PRNEffect'));
        """);
}
