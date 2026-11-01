using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiCare.Infrastructure.Migrations;

[DbContext(typeof(CareDbContext))]
[Migration("20261028130000_AddRevenueSourceDrilldown")]
public sealed class AddRevenueSourceDrilldown : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        create view finance_invoice_service_lines as
          select line.id,line.invoice_id,line.organization_id,line.branch_id,details.payer_id funder_id,
            coalesce(correction.service_type,recharge_policy.service_type,contract.service_type) service_type,
            coalesce(correction.service_date,recharge.service_date,event.service_date) service_date,
            case when line.correction_line_id is not null then 'SourceCorrection' when line.recharge_id is not null then 'ExpenseRecharge'
              when line.billable_event_id is not null then 'Attendance' else 'LegacyUnattributed' end source_kind,
            coalesce(line.correction_line_id,line.recharge_id,line.billable_event_id) source_id,
            line.description,line.quantity,line.unit_rate,line.amount net_amount,line.vat_amount tax_amount
          from finance_invoice_lines line join finance_invoice_details details on details.invoice_id=line.invoice_id and details.organization_id=line.organization_id
            left join finance_billable_events event on event.id=line.billable_event_id and event.organization_id=line.organization_id
            left join finance_contract_versions contract on contract.id=event.contract_version_id
            left join finance_expense_recharges recharge on recharge.id=line.recharge_id and recharge.organization_id=line.organization_id
            left join finance_recharge_policies recharge_policy on recharge_policy.id=recharge.policy_id
            left join finance_invoice_correction_lines correction on correction.id=line.correction_line_id;
        create view finance_revenue_sources as
          select line.organization_id,line.branch_id,line.funder_id,line.service_type,line.service_date,ledger.currency,
            line.invoice_id,ledger.invoice_number,'InvoiceLine'::text record_kind,line.id record_id,line.source_kind,line.source_id,
            line.net_amount,line.tax_amount
          from finance_invoice_service_lines line join finance_invoice_ledger ledger on ledger.invoice_id=line.invoice_id and ledger.organization_id=line.organization_id where ledger.is_issued
          union all
          select credit.organization_id,credit.branch_id,details.payer_id,source.service_type,allocation.service_date,ledger.currency,
            credit.invoice_id,ledger.invoice_number,'CreditAllocation',allocation.id,'Credit',credit.id,-allocation.net_amount,-allocation.tax_amount
          from finance_credit_service_attributions attribution join finance_credit_service_lines allocation on allocation.attribution_id=attribution.id
            join finance_credit_reporting credit on credit.id=attribution.credit_id and credit.organization_id=attribution.organization_id
            join finance_invoice_service_lines source on source.id=allocation.invoice_line_id
            join finance_invoice_details details on details.invoice_id=credit.invoice_id
            join finance_invoice_ledger ledger on ledger.invoice_id=credit.invoice_id and ledger.organization_id=credit.organization_id
          where attribution.approved_by is not null and credit.status='Issued' and ledger.is_issued
          union all
          select credit.organization_id,credit.branch_id,details.payer_id,
            case when supply.types=1 and supply.unknown=0 then supply.service_type else null end,
            credit.service_date,ledger.currency,credit.invoice_id,ledger.invoice_number,'Credit',credit.id,'Credit',credit.id,-credit.net_amount,-credit.tax_amount
          from finance_credit_reporting credit join finance_invoice_details details on details.invoice_id=credit.invoice_id
            join finance_invoice_ledger ledger on ledger.invoice_id=credit.invoice_id and ledger.organization_id=credit.organization_id
            left join lateral(select count(distinct service_type) types,count(*) filter(where service_type is null) unknown,min(service_type) service_type
              from finance_invoice_service_lines where invoice_id=credit.invoice_id)supply on true
          where credit.status='Issued' and ledger.is_issued and not exists(select 1 from finance_credit_service_attributions
            where credit_id=credit.id and approved_by is not null);
        """);

    protected override void Down(MigrationBuilder m) => throw new NotSupportedException("Keep financial reporting provenance; recover forwards.");
}
