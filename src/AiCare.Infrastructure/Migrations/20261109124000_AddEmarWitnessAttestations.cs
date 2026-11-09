using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261109124000_AddEmarWitnessAttestations")]
public sealed class AddEmarWitnessAttestations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table emar_ledger drop constraint ck_emar_ledger_event;
        alter table emar_ledger add constraint ck_emar_ledger_event check(event_type in
          ('Administration','Omission','Refusal','Held','NotRequired','Correction','PRNEffect','RecoveryRequest','RecoveryDecision','WitnessRequest','WitnessAttestation'));
        create unique index ux_emar_witness_request on emar_ledger
          (organization_id,branch_id,mar_record_id,created_by_user_id,submission_fingerprint) where event_type='WitnessRequest';
        create unique index ux_emar_witness_attestation on emar_ledger(corrects_ledger_id) where event_type='WitnessAttestation';
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Preserve witness evidence; recover forwards.");
}
