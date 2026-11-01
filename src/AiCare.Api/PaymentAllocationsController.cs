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
        if (request.Amount <= 0 || request.Amount > 9999999999.99m || string.IsNullOrWhiteSpace(request.Reference) || (request.Currency?.Length != 3 || !request.Currency.All(char.IsAsciiLetter)))
            return BadRequest(new { message = "Positive amount, reference, and three-letter currency are required." });

        if (decimal.Round(request.Amount, 2) != request.Amount || request.ReceiptId == Guid.Empty)
            return BadRequest(new { message = "Use a nonempty receipt ID and at most two decimal places." });
        var id = request.ReceiptId ?? Guid.NewGuid();
        return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var inserted = await Execute("""
                insert into finance_receipts(id,organization_id,branch_id,amount,currency,reference,received_at,received_by,notes)
                values(@id,@org,@branch,@amount,@currency,@reference,@received,@actor,@notes)
                on conflict do nothing
                """, command =>
            {
                Add(command, "id", id); Add(command, "amount", request.Amount);
                Add(command, "currency", request.Currency.ToUpperInvariant());
                Add(command, "reference", request.Reference.Trim());
                Add(command, "received", request.ReceivedAt ?? DateTimeOffset.UtcNow);
                Add(command, "notes", request.Notes?.Trim() ?? string.Empty);
            }, token);
            if (inserted == 0)
            {
                await using var replay = await Command("""
                    select id from finance_receipts where id=@id and organization_id=@org and branch_id=@branch
                      and amount=@amount and currency=@currency and reference=@reference and notes=@notes
                      and (@received is null or received_at=@received)
                    """, token);
                Add(replay, "id", id); Add(replay, "amount", request.Amount);
                Add(replay, "currency", request.Currency.ToUpperInvariant()); Add(replay, "reference", request.Reference.Trim());
                Add(replay, "notes", request.Notes?.Trim() ?? string.Empty);
                var received = replay.CreateParameter(); received.ParameterName = "received";
                received.DbType = DbType.DateTimeOffset; received.Value = (object?)request.ReceivedAt ?? DBNull.Value; replay.Parameters.Add(received);
                if (await replay.ExecuteScalarAsync(token) is not Guid)
                    return Conflict(new { message = "Receipt ID or reference already exists with different details or scope." });
            }
            else { Audit("PAYMENT_RECORDED", id); await db.SaveChangesAsync(token); }
            var used = await Money("select coalesce(sum(amount),0) from finance_payment_allocations where receipt_id=@id and organization_id=@org and reversed_at is null", c => Add(c,"id",id), token);
            await transaction.CommitAsync(token);
            var result = new { id, unallocated = request.Amount-used };
            return inserted == 1 ? Created($"/api/phase1/finance/receipts/{id}", result) : Ok(result);
        });
    }

    [HttpPost("{id:guid}/allocate")]
    public async Task<IActionResult> Allocate(Guid id, AllocationRequest request, CancellationToken token)
    {
        if (request.Amount <= 0 || request.Amount > 9999999999.99m || decimal.Round(request.Amount, 2) != request.Amount || request.AllocationId == Guid.Empty) return BadRequest(new { message = "Allocation must be positive." });

        return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var receipt = await Pair("""
                select amount,currency from finance_receipts
                where id=@id and organization_id=@org and (@wide or branch_id=@branch)
                for update
                """, command => Add(command, "id", id), token);
            if (receipt is null) return NotFound();

            // Receipt first, then invoice: all allocation/reversal paths use this order.
            if (request.AllocationId is Guid retryId)
            {
                await using var replay = await Command("select invoice_id,amount,reversed_at from finance_payment_allocations where id=@allocation and receipt_id=@id and organization_id=@org", token);
                Add(replay,"allocation",retryId); Add(replay,"id",id);
                await using var reader = await replay.ExecuteReaderAsync(token);
                if (await reader.ReadAsync(token))
                    return reader.GetGuid(0)==request.InvoiceId && reader.GetDecimal(1)==request.Amount && reader.IsDBNull(2)
                        ? Ok(new { id=retryId, replayed=true })
                        : Conflict(new { message="Allocation ID already exists with different details or was reversed." });
            }
            var used = await Money("select coalesce(sum(amount),0) from finance_payment_allocations where receipt_id=@id and organization_id=@org and reversed_at is null", command => Add(command, "id", id), token);
            if (used + request.Amount > receipt.Value.Amount)
                return Conflict(new { message = "Allocation exceeds the unallocated receipt balance." });

            var invoice = await Pair("""
                select i."Amount",coalesce(d.currency,'GBP') from "Invoices" i
                left join finance_invoice_details d on d.invoice_id=i."Id" and d.organization_id=i."OrganizationId"
                where i."Id"=@invoice and i."OrganizationId"=@org and (@wide or i."BranchId"=@branch)
                  and i."Status" in ('Issued','Part paid','Overdue')
                  and i."BranchId"=(select branch_id from finance_receipts where id=@receipt and organization_id=@org)
                for update of i
                """, command => { Add(command,"receipt",id); Add(command, "invoice", request.InvoiceId); }, token);
            if (invoice is null) return NotFound();
            if (!string.Equals(receipt.Value.Currency, invoice.Value.Currency, StringComparison.OrdinalIgnoreCase))
                return Conflict(new { message = "Receipt and invoice currencies must match." });

            var balance = await InvoiceBalance.Read(db, tenant.OrganizationId, request.InvoiceId, invoice.Value.Amount, token);
            var invoiceBalance = balance.Outstanding;
            if (request.Amount > invoiceBalance)
                return Conflict(new { message = "Allocation exceeds invoice balance." });

            var allocation = request.AllocationId ?? Guid.NewGuid();
            var inserted = await Execute("""
                insert into finance_payment_allocations(id,receipt_id,invoice_id,organization_id,branch_id,amount,allocated_by)
                values(@allocation,@id,@invoice,@org,(select branch_id from finance_receipts where id=@id),@amount,@actor)
                on conflict do nothing
                """, command =>
            {
                Add(command, "allocation", allocation);
                Add(command, "id", id);
                Add(command, "invoice", request.InvoiceId);
                Add(command, "amount", request.Amount);
            }, token);

            if (inserted == 0) return Conflict(new { message="Allocation ID already exists." });
            var status = (balance with { Receipts=balance.Receipts+request.Amount }).Status;
            await Execute("""update "Invoices" set "Status"=@status where "Id"=@invoice and "OrganizationId"=@org""", command =>
            {
                Add(command, "status", status);
                Add(command, "invoice", request.InvoiceId);
            }, token);

            Audit("PAYMENT_ALLOCATED", allocation, await InvoiceBranch(request.InvoiceId,token));
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

    [HttpPost("{id:guid}/allocations/{allocation:guid}/reverse")]
    public async Task<IActionResult> Reverse(Guid id, Guid allocation, RejectFinancialRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) return BadRequest(new { message="A reversal reason is required." });
        return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var receipt = await Pair("select amount,currency from finance_receipts where id=@id and organization_id=@org and (@wide or branch_id=@branch) for update", c=>Add(c,"id",id), token);
            if (receipt is null) return NotFound();
            Guid invoiceId; decimal amount; bool reversed;
            await using (var command = await Command("select invoice_id,amount,reversed_at from finance_payment_allocations where id=@allocation and receipt_id=@id and organization_id=@org",token))
            {
                Add(command,"allocation",allocation); Add(command,"id",id);
                await using var reader = await command.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token)) return NotFound();
                invoiceId=reader.GetGuid(0); amount=reader.GetDecimal(1); reversed=!reader.IsDBNull(2);
            }
            if (reversed) return Ok(new { id=allocation, reversed=true });
            var invoice = await Pair("""
                select i."Amount",d.currency from "Invoices" i join finance_invoice_details d on d.invoice_id=i."Id" and d.organization_id=i."OrganizationId"
                where i."Id"=@invoice and i."OrganizationId"=@org and (@wide or i."BranchId"=@branch) for update of i
                """,c=>Add(c,"invoice",invoiceId),token);
            if (invoice is null) return NotFound();
            var balance=await InvoiceBalance.Read(db,tenant.OrganizationId,invoiceId,invoice.Value.Amount,token);
            if (balance.Paid<amount) return Conflict(new { message="Refunded funds cannot be released for reallocation." });
            await Execute("update finance_payment_allocations set reversed_at=now(),reversed_by=@actor,reversal_reason=@reason where id=@allocation and organization_id=@org",c=>{Add(c,"allocation",allocation);Add(c,"reason",request.Reason.Trim());},token);
            var status=(balance with { Receipts=balance.Receipts-amount }).Status;
            await Execute("""update "Invoices" set "Status"=@status where "Id"=@invoice and "OrganizationId"=@org""",c=>{Add(c,"invoice",invoiceId);Add(c,"status",status);},token);
            Audit("PAYMENT_ALLOCATION_REVERSED",allocation,await InvoiceBranch(invoiceId,token)); await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return Ok(new { id=allocation, reversed=true, status });
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

    private Task<Guid?> InvoiceBranch(Guid id,CancellationToken token) => db.Invoices.AsNoTracking().Where(x=>x.Id==id&&x.OrganizationId==tenant.OrganizationId).Select(x=>x.BranchId).SingleAsync(token);

    private void Audit(string action, Guid id, Guid? recordBranch=null) => db.AuditEvents.Add(new AuditEvent(
        Guid.NewGuid(), action, user.UserName, "Payment", id, DateTimeOffset.UtcNow,
        tenant.OrganizationId, recordBranch??Branch));
}

public sealed record ReceiptRequest(decimal Amount, string Currency, string Reference, DateTimeOffset? ReceivedAt, string? Notes, Guid? ReceiptId = null);
public sealed record AllocationRequest(Guid InvoiceId, decimal Amount, Guid? AllocationId = null);
