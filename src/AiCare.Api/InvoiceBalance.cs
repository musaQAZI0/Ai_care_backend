using System.Data.Common;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AiCare.Api;

// Every writer must lock the invoice before reading or changing this ledger.
internal sealed record InvoiceBalance(decimal Gross, decimal Receipts, decimal Direct, decimal Refunds, decimal Credits, decimal WriteOffs=0)
{
    public decimal Paid => Receipts + Direct - Refunds;
    public decimal Outstanding => Gross - Credits - WriteOffs - Paid;
    public string Status => Credits >= Gross ? "Credited" : Outstanding <= 0 ? WriteOffs>0?"Written off":"Paid" : Paid > 0 ? "Part paid" : "Issued";

    public static async Task<InvoiceBalance> Read(CareDbContext db, Guid organization, Guid invoice, decimal gross, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Invoice balances require a transaction and invoice lock.");
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction.GetDbTransaction();
        command.CommandText = "select receipt_allocations,direct_payments,refunds,credits,write_offs from finance_invoice_ledger where organization_id=@org and invoice_id=@invoice";
        Bind(command, "org", organization); Bind(command, "invoice", invoice);
        await using var reader = await command.ExecuteReaderAsync(token);
        if(!await reader.ReadAsync(token))throw new InvalidOperationException("Invoice ledger details are missing or outside the tenant scope.");
        return new(gross, reader.GetDecimal(0), reader.GetDecimal(1), reader.GetDecimal(2), reader.GetDecimal(3), reader.GetDecimal(4));
    }

    private static void Bind(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter);
    }
}
