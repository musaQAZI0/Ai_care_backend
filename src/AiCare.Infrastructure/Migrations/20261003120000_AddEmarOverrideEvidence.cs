using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261003120000_AddEmarOverrideEvidence")]
public sealed class AddEmarOverrideEvidence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table emar_ledger add column override_reason text not null default '';
        alter table emar_ledger add column override_evidence_reference text not null default '';
        alter table emar_ledger add column override_authoriser text not null default '';
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        do $$ begin
          if exists(select 1 from emar_ledger where override_reason <> '') then
            raise exception 'Cannot roll back immutable eMAR override evidence';
          end if;
        end $$;
        alter table emar_ledger drop column override_authoriser;
        alter table emar_ledger drop column override_evidence_reference;
        alter table emar_ledger drop column override_reason;
        """);
}
