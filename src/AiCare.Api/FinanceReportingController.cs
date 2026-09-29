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
public sealed class FinanceReportingController(CareDbContext db, ITenantContext tenant) : ControllerBase
{
    private Guid Branch => tenant.BranchId ?? AiCare.Domain.TenantDefaults.BranchId;

    [HttpGet("operational-performance")]
    public async Task<IActionResult> Performance([FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken token)
    {
        if (from > to) return BadRequest(new { message = "Invalid report period." });

        var revenue = await Money("""select coalesce(sum(d.net_amount),0) from finance_invoice_details d join "Invoices" i on i."Id"=d.invoice_id where d.organization_id=@org and (@wide or d.branch_id=@branch) and d.invoice_date between @from and @to and i."Status"<>'Void'""", from, to, token);
        var workforce = await Money("select coalesce(sum(l.gross_pay),0) from finance_payroll_lines l where l.organization_id=@org and (@wide or l.branch_id=@branch) and l.created_at::date between @from and @to", from, to, token);
        var claims = await Money("select coalesce(sum(amount),0) from finance_workforce_claims where organization_id=@org and (@wide or branch_id=@branch) and claim_date between @from and @to and status in ('Approved','Paid')", from, to, token);
        var outstanding = await Money("""
            select coalesce(sum(i."Amount"-coalesce(p.paid,0)-coalesce(a.allocated,0)-coalesce(c.credited,0)+coalesce(r.refunded,0)),0)
            from "Invoices" i
            left join lateral(select sum(amount) paid from finance_payments where invoice_id=i."Id") p on true
            left join lateral(select sum(amount) allocated from finance_payment_allocations where invoice_id=i."Id" and reversed_at is null) a on true
            left join lateral(select sum(amount) credited from finance_credit_notes where invoice_id=i."Id" and status='Issued') c on true
            left join lateral(select sum(amount) refunded from finance_refunds where invoice_id=i."Id") r on true
            where i."OrganizationId"=@org and (@wide or i."BranchId"=@branch) and i."Status" not in ('Void','Credited')
            """, from, to, token);

        return Ok(new
        {
            from,
            to,
            revenue,
            directWorkforceCost = workforce,
            approvedClaims = claims,
            contribution = revenue - workforce - claims,
            outstanding,
            warning = "Operational management figures; not statutory accounts."
        });
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
