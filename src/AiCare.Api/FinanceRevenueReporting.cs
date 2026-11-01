using System.Data;
using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

public sealed partial class FinanceReportingController
{
    [HttpGet("revenue")]
    public async Task<IActionResult> Revenue([FromQuery] DateOnly from, [FromQuery] DateOnly to,
        [FromQuery] Guid? branchId, [FromQuery] Guid? funderId, [FromQuery] string? serviceType,
        [FromQuery] string? currency, [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken token = default)
    {
        if (from == default || to < from || to == DateOnly.MaxValue || page < 1 || page > 1000000 || pageSize is < 1 or > 200)
            return BadRequest(new { message = "Inclusive UK service dates and a page size between 1 and 200 are required." });
        if (!tenant.IsOrganizationWide && branchId.HasValue && branchId != tenant.BranchId) return Forbid();
        if (currency is not null && (currency.Length != 3 || !currency.All(char.IsAsciiLetter))) return BadRequest(new { message = "Currency must be a three-letter code." });
        // All report sections see one PostgreSQL snapshot, including concurrently posted credits and replacement invoices.
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        void Bind(DbCommand c)
        {
            Add(c, "from", from); Add(c, "end", to.AddDays(1)); Typed(c, "filterBranch", branchId, DbType.Guid);
            Typed(c, "funder", funderId, DbType.Guid); Typed(c, "service", string.IsNullOrWhiteSpace(serviceType) ? null : serviceType.Trim(), DbType.String);
            Typed(c, "currency", currency?.ToUpperInvariant(), DbType.String);
        }
        const string sourceScope = """
            organization_id=@org and (@wide or branch_id=@branch) and (@filterBranch is null or branch_id=@filterBranch)
            and (@funder is null or funder_id=@funder) and (@currency is null or currency=@currency)
            """;
        var totals = await RevenueRows($"""
            select organization_id,branch_id,funder_id,service_type,to_char(service_date,'YYYY-MM') period,currency,
              coalesce(sum(net_amount) filter(where record_kind='InvoiceLine'),0) billed_net,
              -coalesce(sum(net_amount) filter(where record_kind<>'InvoiceLine'),0) credited_net,
              coalesce(sum(net_amount),0) revenue_net,coalesce(sum(tax_amount),0) revenue_tax,
              count(distinct invoice_id) invoice_count
            from finance_revenue_sources where {sourceScope} and service_date>=@from and service_date<@end
              and (@service is null or lower(service_type)=lower(@service))
            group by organization_id,branch_id,funder_id,service_type,to_char(service_date,'YYYY-MM'),currency
            order by currency,period,branch_id,funder_id,service_type nulls last
            """, Bind, token);
        var sources = await RevenueRows($"""
            select *,count(*) over() source_count from finance_revenue_sources where {sourceScope}
              and service_date>=@from and service_date<@end and (@service is null or lower(service_type)=lower(@service))
            order by service_date,invoice_id,record_kind,record_id limit @take offset @skip
            """, c => { Bind(c); Add(c, "take", pageSize); Add(c, "skip", (page - 1) * pageSize); }, token);
        var exceptions = await RevenueRows($"""
            select invoice_id,invoice_number,currency,record_kind,record_id,source_id,service_date,net_amount,tax_amount,
              case when service_type is null then 'SERVICE_TYPE_ATTRIBUTION_REQUIRED' when service_date is null then 'SERVICE_DATE_ATTRIBUTION_REQUIRED' else 'NET_TAX_ATTRIBUTION_REQUIRED' end exception
            from finance_revenue_sources where {sourceScope} and (service_date is null or service_type is null or net_amount is null or tax_amount is null)
            order by currency,invoice_id,record_kind,record_id
            """, Bind, token);
        var reconciliation = await RevenueRows("""
            with ledger as (
              select l.currency,sum(l.net_amount) invoice_net,sum(l.vat_amount) invoice_tax,sum(l.credits) gross_credits,
                sum(l.net_cash) net_cash,sum(l.outstanding) outstanding,sum(l.customer_credit) customer_credit
              from finance_invoice_ledger l join finance_invoice_details d on d.invoice_id=l.invoice_id
              where l.organization_id=@org and (@wide or l.branch_id=@branch) and (@filterBranch is null or l.branch_id=@filterBranch)
                and (@funder is null or d.payer_id=@funder) and (@currency is null or l.currency=@currency) and l.is_issued group by l.currency
            ), source as (
              select currency,coalesce(sum(net_amount) filter(where record_kind='InvoiceLine'),0) invoice_net,
                coalesce(sum(tax_amount) filter(where record_kind='InvoiceLine'),0) invoice_tax,
                -coalesce(sum(net_amount+tax_amount) filter(where record_kind<>'InvoiceLine'),0) credit_gross
              from finance_revenue_sources where organization_id=@org and (@wide or branch_id=@branch)
                and (@filterBranch is null or branch_id=@filterBranch) and (@funder is null or funder_id=@funder)
                and (@currency is null or currency=@currency) group by currency
            )
            select ledger.*,coalesce(source.invoice_net,0) source_invoice_net,coalesce(source.invoice_tax,0) source_invoice_tax,
              coalesce(source.credit_gross,0) source_credit_gross,ledger.invoice_net-coalesce(source.invoice_net,0) invoice_net_difference,
              ledger.invoice_tax-coalesce(source.invoice_tax,0) invoice_tax_difference,ledger.gross_credits-coalesce(source.credit_gross,0) credit_gross_difference
            from ledger left join source on source.currency=ledger.currency order by ledger.currency
            """, Bind, token);
        await tx.CommitAsync(token);
        return Ok(new { from, to, branchId, funderId, serviceType, currency, page, pageSize, groups = totals, sources,
            attributionExceptions = exceptions, reconciliationCurrencyTotals = reconciliation,
            reconciliationScope = "All service dates and service types within the selected branch, funder and currency. Revenue groups and drill-down use the requested service period and service type.",
            warning = "Unattributed records are listed explicitly. A mixed-service credit needs independently reviewed line allocations; no proportional service or VAT split is inferred. Cash, debt and write-offs are separate from revenue." });
    }

    async Task<List<Dictionary<string, object?>>> RevenueRows(string sql, Action<DbCommand> bind, CancellationToken token)
    {
        var result = new List<Dictionary<string, object?>>(); await using var command = await Command(sql, token); bind(command);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) { var row = new Dictionary<string, object?>(); for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i); result.Add(row); }
        return result;
    }

    static void Typed(DbCommand c, string name, object? value, DbType type)
    { var p = c.CreateParameter(); p.ParameterName = name; p.DbType = type; p.Value = value ?? DBNull.Value; c.Parameters.Add(p); }
}
