using System.Data;
using System.Data.Common;
using AiCare.Application;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AiCare.Api;

[ApiController]
[Authorize(Roles = "Administrator,BackOffice,CareManager")]
[Route("api/phase1/finance/reports")]
public sealed partial class FinanceReportingController(CareDbContext db, ITenantContext tenant) : ControllerBase
{
    private Guid Branch => tenant.BranchId ?? AiCare.Domain.TenantDefaults.BranchId;

    [HttpGet("operational-performance")]
    public async Task<IActionResult> Performance([FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken token)
    {
        if (from==default || from>to || to==DateOnly.MaxValue) return BadRequest(new{message="Valid inclusive UK service dates are required."});
        var rows=new List<object>();
        await using var command=await Command("""
          with charges as (
           select l.currency,sum(line.net_amount) billed,count(distinct l.invoice_id)::int invoice_count
           from finance_invoice_ledger l join finance_invoice_service_lines line on line.invoice_id=l.invoice_id and line.organization_id=l.organization_id
           where l.organization_id=@org and (@wide or l.branch_id=@branch) and l.is_issued and line.service_date>=@from and line.service_date<@end group by l.currency
          ), credits as (
           select l.currency,-sum(c.net_amount) net_credits,-sum(c.tax_amount) tax_credits from finance_revenue_sources c
           join finance_invoice_ledger l on l.invoice_id=c.invoice_id and l.organization_id=c.organization_id
           where l.organization_id=@org and (@wide or l.branch_id=@branch) and l.is_issued and c.record_kind<>'InvoiceLine' and c.service_date>=@from and c.service_date<@end group by l.currency
          )
          select coalesce(charges.currency,credits.currency),coalesce(charges.billed,0),coalesce(charges.invoice_count,0),coalesce(credits.net_credits,0),coalesce(credits.tax_credits,0),coalesce(charges.billed,0)-coalesce(credits.net_credits,0)
          from charges full join credits on credits.currency=charges.currency order by 1
          """,token);
        Add(command,"from",from);Add(command,"end",to.AddDays(1));
        await using(var reader=await command.ExecuteReaderAsync(token))
        {
          while(await reader.ReadAsync(token))rows.Add(new{currency=reader.GetString(0),billedServiceNet=reader.GetDecimal(1),invoiceCount=reader.GetInt32(2),attributedNetCredits=reader.GetDecimal(3),attributedTaxCredits=reader.GetDecimal(4),netServiceRevenue=reader.GetDecimal(5)});
        }
        var balances=await Rows("""
          select currency,sum(outstanding) outstanding,sum(customer_credit) customer_credit,sum(credits) issued_gross_credits,sum(refunds) refunds,sum(net_cash) net_cash,sum(write_offs) write_offs
          from finance_invoice_ledger where organization_id=@org and (@wide or branch_id=@branch) and is_issued group by currency order by currency
          """,token);
        var unattributed=await Rows("""
          select l.invoice_id,l.invoice_number,l.currency,l.credits gross_credits,
            case when exists(select 1 from finance_credit_reporting c where c.invoice_id=l.invoice_id and c.organization_id=l.organization_id and c.status='Issued' and (c.net_amount is null or c.tax_amount is null or c.service_date is null)) then 'CREDIT_SERVICE_ATTRIBUTION_REQUIRED' else 'MISSING_SERVICE_LINEAGE' end exception
          from finance_invoice_ledger l where l.organization_id=@org and (@wide or l.branch_id=@branch) and l.is_issued
          and (exists(select 1 from finance_credit_reporting c where c.invoice_id=l.invoice_id and c.organization_id=l.organization_id and c.status='Issued' and (c.net_amount is null or c.tax_amount is null or c.service_date is null)) or not exists(select 1 from finance_invoice_lines line where line.invoice_id=l.invoice_id and line.organization_id=l.organization_id) or exists(select 1 from finance_invoice_lines line where line.invoice_id=l.invoice_id and line.organization_id=l.organization_id and line.billable_event_id is null and line.recharge_id is null and line.correction_line_id is null))
          order by l.invoice_id
          """,token);
        return Ok(new{from,to,serviceCurrencyTotals=rows,ledgerCurrencyTotals=balances,attributionExceptions=unattributed,
          contribution=(decimal?)null,warning="Net service revenue subtracts explicitly attributed credit net amounts. Unattributed legacy records are listed separately. Write-offs, refunds and current balances are separate from revenue. Workforce cost allocations remain incomplete; contribution is unavailable."});
    }

    [HttpGet("sync-health")]
    public async Task<IActionResult> SyncHealth(CancellationToken token) => Ok(await Rows(
        "select provider,status,count(*) count,max(last_attempt_at) last_attempt from accounting_outbox where organization_id=@org group by provider,status order by provider,status",
        token));

    private async Task<decimal> Money(string sql, DateOnly from, DateOnly to, CancellationToken token)
    {
        await using var command = await Command(sql, token);
        Add(command, "from", from);
        Add(command, "to", to);
        return Convert.ToDecimal(await command.ExecuteScalarAsync(token));
    }

    private async Task<List<Dictionary<string, object?>>> Rows(string sql, CancellationToken token)
    {
        var rows = new List<Dictionary<string, object?>>();
        await using var command = await Command(sql, token);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var row = new Dictionary<string, object?>();
            for (var index = 0; index < reader.FieldCount; index++)
                row[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            rows.Add(row);
        }

        return rows;
    }

    private async Task<DbCommand> Command(string sql, CancellationToken token)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(token);
        var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = sql;
        Add(command, "org", tenant.OrganizationId);
        Add(command, "branch", Branch);
        Add(command, "wide", tenant.IsOrganizationWide);
        return command;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        if (command.Parameters.Contains(name)) return;
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
