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
[TransactionalAction]
[Authorize(Roles="Administrator,BackOffice")]
[Route("api/phase1/invoices")]
[Route("api/phase1/finance/invoices")]
public sealed class InvoiceLifecycleController(CareDbContext db,ITenantContext tenant,ICurrentUserContext user):ControllerBase
{
    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id,CancellationToken token)
    {
        var invoice=await Lock(id,token);if(invoice is null)return NotFound();
        if(invoice.Status!="Generated")return Conflict(new{message="Only a generated invoice can be approved."});
        await SetStatus(id,"Approved",token);Audit("invoice.approved",id);await db.SaveChangesAsync(token);
        return Ok(new{id,status="Approved"});
    }

    [HttpPost("{id:guid}/issue")]
    public async Task<IActionResult> Issue(Guid id,CancellationToken token)
    {
        var invoice=await Lock(id,token);if(invoice is null)return NotFound();
        if(invoice.Status!="Approved")return Conflict(new{message="Only an approved invoice can be issued."});
        await SetStatus(id,"Issued",token);Audit("invoice.issued",id);await db.SaveChangesAsync(token);
        return Ok(new{id,status="Issued"});
    }

    [HttpPost("{id:guid}/payments")]
    [HttpPost("{id:guid}/record-payment")]
    public async Task<IActionResult> RecordPayment(Guid id,RecordFinancePaymentRequest request,CancellationToken token)
    {
        if(request.Amount<=0||string.IsNullOrWhiteSpace(request.Reference))return BadRequest(new{message="Payment amount and reference are required."});
        var invoice=await Lock(id,token);if(invoice is null)return NotFound();
        if(invoice.Status is not ("Issued" or "Part paid" or "Overdue"))return Conflict(new{message="Only an issued invoice can receive payments."});
        var paid=await Money("select coalesce((select sum(amount) from finance_payments where invoice_id=@invoice and organization_id=@organization),0)-coalesce((select sum(amount) from finance_refunds where invoice_id=@invoice and organization_id=@organization),0)",id,token);
        var credited=await Money("select coalesce(sum(amount),0) from finance_credit_notes where invoice_id=@invoice and organization_id=@organization and status='Issued'",id,token);
        if(request.Amount>invoice.Amount-credited-paid)return Conflict(new{message="Payment exceeds the outstanding invoice balance."});
        var payment=Guid.NewGuid();await using(var command=await Command("insert into finance_payments(id,invoice_id,organization_id,branch_id,amount,reference,received_at,received_by) values(@payment,@invoice,@organization,@branch,@amount,@reference,@received,@actor) on conflict (organization_id,invoice_id,reference) do nothing",token)){Add(command,"payment",payment);Add(command,"invoice",id);Add(command,"amount",request.Amount);Add(command,"reference",request.Reference.Trim());Add(command,"received",request.ReceivedAt??DateTimeOffset.UtcNow);if(await command.ExecuteNonQueryAsync(token)==0)return Conflict(new{message="A payment with this reference already exists for the invoice."});}
        paid+=request.Amount;var status=paid>=invoice.Amount-credited?"Paid":"Part paid";await SetStatus(id,status,token);Audit("finance.payment_recorded",payment);await db.SaveChangesAsync(token);
        return Created($"/api/phase1/finance/invoices/{id}/payments/{payment}",new{id=payment,invoiceId=id,paid,status});
    }

    [HttpPost("{id:guid}/credit-notes")]
    public async Task<IActionResult> Credit(Guid id,InvoiceCreditRequest request,CancellationToken token)
    {
        if(request.Amount<=0||string.IsNullOrWhiteSpace(request.Reason))return BadRequest(new{message="Credit amount and reason are required."});
        var invoice=await Lock(id,token);if(invoice is null)return NotFound();
        if(invoice.Status is not ("Issued" or "Part paid" or "Paid" or "Overdue"))return Conflict(new{message="Only an issued invoice can be credited."});
        var existing=await Money("select coalesce(sum(amount),0) from finance_credit_notes where invoice_id=@invoice and organization_id=@organization and status='Issued'",id,token);
        if(request.Amount>invoice.Amount-existing)return Conflict(new{message="Credit exceeds the remaining invoice value."});
        var ordinal=await Count("select count(*) from finance_credit_notes where invoice_id=@invoice and organization_id=@organization",id,token)+1;
        var invoiceNumber=await Text("select invoice_number from finance_invoice_details where invoice_id=@invoice and organization_id=@organization",id,token);var creditNumber=$"CRN-{invoiceNumber}-{ordinal:00}";var credit=Guid.NewGuid();
        await using(var command=await Command("insert into finance_credit_notes(id,invoice_id,organization_id,branch_id,credit_number,amount,reason,issued_by) values(@credit,@invoice,@organization,@branch,@number,@amount,@reason,@actor)",token)){Add(command,"credit",credit);Add(command,"invoice",id);Add(command,"number",creditNumber);Add(command,"amount",request.Amount);Add(command,"reason",request.Reason.Trim());await command.ExecuteNonQueryAsync(token);}
        if(existing+request.Amount>=invoice.Amount)await SetStatus(id,"Credited",token);Audit("finance.credit_note_issued",credit);await db.SaveChangesAsync(token);
        return Created($"/api/phase1/finance/invoices/{id}/credit-notes/{credit}",new{id=credit,invoiceId=id,creditNumber,request.Amount,status=existing+request.Amount>=invoice.Amount?"Credited":invoice.Status});
    }

    [HttpPost("{id:guid}/refunds")]
    public async Task<IActionResult> Refund(Guid id,InvoiceRefundRequest request,CancellationToken token)
    {
        if(request.Amount<=0||string.IsNullOrWhiteSpace(request.Reference)||string.IsNullOrWhiteSpace(request.Reason))return BadRequest(new{message="Refund amount, reference, and reason are required."});
        var invoice=await Lock(id,token);if(invoice is null)return NotFound();
        if(invoice.Status is not ("Issued" or "Part paid" or "Paid" or "Overdue" or "Credited"))return Conflict(new{message="Only an issued invoice with received payments can be refunded."});
        var received=await Money("select coalesce(sum(amount),0) from finance_payments where invoice_id=@invoice and organization_id=@organization",id,token);var refunded=await Money("select coalesce(sum(amount),0) from finance_refunds where invoice_id=@invoice and organization_id=@organization",id,token);
        if(request.Amount>received-refunded)return Conflict(new{message="Refund exceeds payments received."});
        var refund=Guid.NewGuid();await using(var command=await Command("insert into finance_refunds(id,invoice_id,organization_id,branch_id,amount,reference,reason,refunded_by) values(@refund,@invoice,@organization,@branch,@amount,@reference,@reason,@actor) on conflict(organization_id,invoice_id,reference) do nothing",token)){Add(command,"refund",refund);Add(command,"invoice",id);Add(command,"amount",request.Amount);Add(command,"reference",request.Reference.Trim());Add(command,"reason",request.Reason.Trim());if(await command.ExecuteNonQueryAsync(token)==0)return Conflict(new{message="A refund with this reference already exists for the invoice."});}
        var credits=await Money("select coalesce(sum(amount),0) from finance_credit_notes where invoice_id=@invoice and organization_id=@organization and status='Issued'",id,token);var effectivePaid=received-refunded-request.Amount;var adjusted=invoice.Amount-credits;var status=adjusted<=0?"Credited":effectivePaid>=adjusted?"Paid":effectivePaid>0?"Part paid":"Issued";await SetStatus(id,status,token);Audit("finance.refund_recorded",refund);await db.SaveChangesAsync(token);
        return Created($"/api/phase1/finance/invoices/{id}/refunds/{refund}",new{id=refund,invoiceId=id,request.Amount,status});
    }
    [HttpPost("{id:guid}/void")]
    public async Task<IActionResult> Void(Guid id,RejectFinancialRequest request,CancellationToken token)
    {
        if(string.IsNullOrWhiteSpace(request.Reason))return BadRequest(new{message="A void reason is required."});
        var invoice=await Lock(id,token);if(invoice is null)return NotFound();
        if(invoice.Status is not ("Generated" or "Approved" or "Issued"))return Conflict(new{message="Paid, part-paid, or already void invoices cannot be voided."});
        if(await Money("select coalesce((select sum(amount) from finance_payments where invoice_id=@invoice and organization_id=@organization),0)+coalesce((select sum(amount) from finance_credit_notes where invoice_id=@invoice and organization_id=@organization),0)",id,token)>0)return Conflict(new{message="An invoice with payments or credits must be corrected through a credit or refund workflow."});
        await SetStatus(id,"Void",token);Audit($"invoice.voided: {request.Reason.Trim()}",id);await db.SaveChangesAsync(token);
        return Ok(new{id,status="Void"});
    }

    private async Task<InvoiceLock?> Lock(Guid id,CancellationToken token)
    {
        var lockClause = db.Database.IsNpgsql() ? " for update" : string.Empty;
        await using var command=await Command("select \"Amount\",\"Status\" from \"Invoices\" where \"Id\"=@invoice and \"OrganizationId\"=@organization and (@organizationWide or \"BranchId\"=@branch)" + lockClause,token);Add(command,"invoice",id);Add(command,"organizationWide",tenant.IsOrganizationWide);
        await using var reader=await command.ExecuteReaderAsync(token);return await reader.ReadAsync(token)?new(reader.GetDecimal(0),reader.GetString(1)):null;
    }
    private async Task SetStatus(Guid id,string status,CancellationToken token){await using var command=await Command("update \"Invoices\" set \"Status\"=@status where \"Id\"=@invoice and \"OrganizationId\"=@organization",token);Add(command,"invoice",id);Add(command,"status",status);await command.ExecuteNonQueryAsync(token);}
    private async Task<decimal> Money(string sql,Guid invoice,CancellationToken token){await using var command=await Command(sql,token);Add(command,"invoice",invoice);return Convert.ToDecimal(await command.ExecuteScalarAsync(token));}
    private async Task<int> Count(string sql,Guid invoice,CancellationToken token){await using var command=await Command(sql,token);Add(command,"invoice",invoice);return Convert.ToInt32(await command.ExecuteScalarAsync(token));}
    private async Task<string> Text(string sql,Guid invoice,CancellationToken token){await using var command=await Command(sql,token);Add(command,"invoice",invoice);return Convert.ToString(await command.ExecuteScalarAsync(token))??throw new InvalidOperationException("Invoice detail is missing.");}
    private async Task<DbCommand> Command(string sql,CancellationToken token){var connection=db.Database.GetDbConnection();if(connection.State!=ConnectionState.Open)await connection.OpenAsync(token);var command=connection.CreateCommand();command.Transaction=db.Database.CurrentTransaction?.GetDbTransaction();command.CommandText=sql;Add(command,"organization",tenant.OrganizationId);Add(command,"branch",tenant.BranchId??TenantDefaults.BranchId);Add(command,"actor",user.UserName);return command;}
    private static void Add(DbCommand command,string name,object? value){if(command.Parameters.Contains(name))return;var parameter=command.CreateParameter();parameter.ParameterName=name;parameter.Value=value??DBNull.Value;command.Parameters.Add(parameter);}
    private void Audit(string action,Guid id)=>db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),action,user.UserName,"Invoice",id,DateTimeOffset.UtcNow,tenant.OrganizationId,tenant.BranchId??TenantDefaults.BranchId));
    private sealed record InvoiceLock(decimal Amount,string Status);
}

public sealed record InvoiceCreditRequest(decimal Amount,string Reason);
public sealed record InvoiceRefundRequest(decimal Amount,string Reference,string Reason);
