using System.Data;
using System.Data.Common;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AiCare.Api;

[ApiController]
[Authorize(Roles = "Administrator,BackOffice")]
[Route("api/phase1/finance/receipts")]
public sealed class PaymentAllocationsController(
    CareDbContext db,
    ITenantContext tenant,
    ICurrentUserContext user) : ControllerBase
{
    private Guid Branch => tenant.BranchId ?? TenantDefaults.BranchId;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken token)
    {
        var rows = new List<object>();
        await using var command = await Command("""
            select r.id,r.amount,r.currency,r.reference,r.received_at,r.notes,
                   r.amount-coalesce(sum(a.amount) filter(where a.reversed_at is null),0) unallocated
            from finance_receipts r
            left join finance_payment_allocations a on a.receipt_id=r.id and a.organization_id=r.organization_id
            where r.organization_id=@org and (@wide or r.branch_id=@branch)
            group by r.id
            order by r.received_at desc
            """, token);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            rows.Add(new
            {
                id = reader.GetGuid(0),
                amount = reader.GetDecimal(1),
                currency = reader.GetString(2).Trim(),
                reference = reader.GetString(3),
                receivedAt = reader.GetFieldValue<DateTimeOffset>(4),
                notes = reader.GetString(5),
                unallocated = reader.GetDecimal(6)
            });
        }

        return Ok(rows);
    }

    [HttpPost]
    public async Task<IActionResult> Create(ReceiptRequest request, CancellationToken token)
    {
        if (request.Amount <= 0 || string.IsNullOrWhiteSpace(request.Reference) || request.Currency?.Length != 3)
            return BadRequest(new { message = "Positive amount, reference, and three-letter currency are required." });

        var id = Guid.NewGuid();
        try
        {
            await Execute("""
                insert into finance_receipts(id,organization_id,branch_id,amount,currency,reference,received_at,received_by,notes)
                values(@id,@org,@branch,@amount,@currency,@reference,@received,@actor,@notes)
                """, command =>
            {
                Add(command, "id", id);
                Add(command, "amount", request.Amount);
                Add(command, "currency", request.Currency.ToUpperInvariant());
                Add(command, "reference", request.Reference.Trim());
                Add(command, "received", request.ReceivedAt ?? DateTimeOffset.UtcNow);
                Add(command, "notes", request.Notes?.Trim() ?? string.Empty);
            }, token);
        }
        catch (Npgsql.PostgresException exception) when (exception.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
        {
            return Conflict(new { message = "Receipt reference already exists." });
        }

        Audit("PAYMENT_RECORDED", id);
        await db.SaveChangesAsync(token);
        return Created($"/api/phase1/finance/receipts/{id}", new { id, unallocated = request.Amount });
    }

    [HttpPost("{id:guid}/allocate")]
    public async Task<IActionResult> Allocate(Guid id, AllocationRequest request, CancellationToken token)
    {
        if (request.Amount <= 0) return BadRequest(new { message = "Allocation must be positive." });

        return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var receipt = await Pair("""
                select amount,currency from finance_receipts
                where id=@id and organization_id=@org and (@wide or branch_id=@branch)
                for update
                """, command => Add(command, "id", id), token);
            if (receipt is null) return NotFound();

            var used = await Money("select coalesce(sum(amount),0) from finance_payment_allocations where receipt_id=@id and organization_id=@org and reversed_at is null", command => Add(command, "id", id), token);
            if (used + request.Amount > receipt.Value.Amount)
                return Conflict(new { message = "Allocation exceeds the unallocated receipt balance." });

            var invoice = await Pair("""
                select i."Amount",coalesce(d.currency,'GBP') from "Invoices" i
                left join finance_invoice_details d on d.invoice_id=i."Id"
                where i."Id"=@invoice and i."OrganizationId"=@org and (@wide or i."BranchId"=@branch)
                  and i."Status" in ('Issued','Part paid','Overdue')
                for update
                """, command => Add(command, "invoice", request.InvoiceId), token);
            if (invoice is null) return NotFound();
            if (!string.Equals(receipt.Value.Currency, invoice.Value.Currency, StringComparison.OrdinalIgnoreCase))
                return Conflict(new { message = "Receipt and invoice currencies must match." });

            var allocated = await Money("select coalesce(sum(amount),0) from finance_payment_allocations where invoice_id=@invoice and organization_id=@org and reversed_at is null", command => Add(command, "invoice", request.InvoiceId), token);
            var direct = await Money("select coalesce(sum(amount),0) from finance_payments where invoice_id=@invoice and organization_id=@org", command => Add(command, "invoice", request.InvoiceId), token);
            var refunded = await Money("select coalesce(sum(amount),0) from finance_refunds where invoice_id=@invoice and organization_id=@org", command => Add(command, "invoice", request.InvoiceId), token);
            var credited = await Money("select coalesce(sum(amount),0) from finance_credit_notes where invoice_id=@invoice and organization_id=@org and status='Issued'", command => Add(command, "invoice", request.InvoiceId), token);
            var effectivePaid = allocated + direct - refunded;
            var invoiceBalance = invoice.Value.Amount - credited - effectivePaid;
            if (request.Amount > invoiceBalance)
                return Conflict(new { message = "Allocation exceeds invoice balance." });

            var allocation = Guid.NewGuid();
            await Execute("""
                insert into finance_payment_allocations(id,receipt_id,invoice_id,organization_id,branch_id,amount,allocated_by)
                values(@allocation,@id,@invoice,@org,@branch,@amount,@actor)
                """, command =>
            {
                Add(command, "allocation", allocation);
                Add(command, "id", id);
                Add(command, "invoice", request.InvoiceId);
                Add(command, "amount", request.Amount);
            }, token);

            var status = request.Amount >= invoiceBalance ? "Paid" : "Part paid";
            await Execute("""update "Invoices" set "Status"=@status where "Id"=@invoice and "OrganizationId"=@org""", command =>
            {
                Add(command, "status", status);
                Add(command, "invoice", request.InvoiceId);
            }, token);

            Audit("PAYMENT_ALLOCATED", allocation);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Created($"/api/phase1/finance/receipts/{id}/allocations/{allocation}", new
            {
                id = allocation,
                status,
                unallocated = receipt.Value.Amount - used - request.Amount
            });
        });
    }

    private async Task<(decimal Amount, string Currency)?> Pair(string sql, Action<DbCommand> bind, CancellationToken token)
    {
        await using var command = await Command(sql, token);
        bind(command);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? (reader.GetDecimal(0), reader.GetString(1).Trim()) : null;
    }

    private async Task<decimal> Money(string sql, Action<DbCommand> bind, CancellationToken token)
    {
        await using var command = await Command(sql, token);
        bind(command);
        return Convert.ToDecimal(await command.ExecuteScalarAsync(token));
    }

    private async Task<int> Execute(string sql, Action<DbCommand> bind, CancellationToken token)
    {
        await using var command = await Command(sql, token);
        bind(command);
        return await command.ExecuteNonQueryAsync(token);
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
        Add(command, "actor", user.UserName);
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

    private void Audit(string action, Guid id) => db.AuditEvents.Add(new AuditEvent(
        Guid.NewGuid(), action, user.UserName, "Payment", id, DateTimeOffset.UtcNow,
        tenant.OrganizationId, Branch));
}

public sealed record ReceiptRequest(decimal Amount, string Currency, string Reference, DateTimeOffset? ReceivedAt, string? Notes);
public sealed record AllocationRequest(Guid InvoiceId, decimal Amount);
