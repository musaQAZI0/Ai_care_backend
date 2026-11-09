using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261109125000_AddMedicationStockCounts")]
public sealed class AddMedicationStockCounts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        alter table medication_stock_transactions add column counted_balance numeric(12,2) null,
          add column submission_fingerprint text not null default '';
        alter table medication_stock_transactions drop constraint ck_stock_type;
        alter table medication_stock_transactions add constraint ck_stock_type check(transaction_type in
          ('Receipt','Administration','Return','Waste','Disposal','Correction','Adjustment','Count'));
        alter table medication_monitoring_alerts drop constraint medication_monitoring_alerts_alert_type_check;
        alter table medication_monitoring_alerts add constraint medication_monitoring_alerts_alert_type_check
          check(alert_type in ('ReviewDue','EndDateApproaching','StockDiscrepancy'));
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Preserve stock count evidence; recover forwards.");
}
