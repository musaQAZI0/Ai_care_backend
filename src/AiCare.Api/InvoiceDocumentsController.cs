using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Mail;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[ApiController]
[Authorize(Roles = "Administrator,BackOffice")]
[Route("api/phase1/finance")]
public sealed class InvoiceDocumentsController(
    CareDbContext db,
    ITenantContext tenant,
    ICurrentUserContext user,
    IConfiguration configuration,
    ILogger<InvoiceDocumentsController> logger) : ControllerBase
{
    [HttpGet("invoice-profile")]
    public async Task<IActionResult> GetProfile(CancellationToken token)
    {
        await using var command = await Command("""
            select provider_name,provider_address,provider_email,provider_phone,company_number,vat_number,remittance_details,default_payment_terms_days,default_vat_rate,vat_exemption_reason
            from finance_invoice_profiles where organization_id=@organization and branch_id=@branch
            """, token);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (await reader.ReadAsync(token))
            return Ok(new {
                providerName=reader.GetString(0),providerAddress=reader.GetString(1),providerEmail=reader.GetString(2),
                providerPhone=reader.GetString(3),companyNumber=reader.GetString(4),vatNumber=reader.GetString(5),
                remittanceDetails=reader.GetString(6),paymentTermsDays=reader.GetInt32(7),vatRate=reader.GetDecimal(8),
                vatExemptionReason=reader.GetString(9)
            });
        await reader.DisposeAsync();
        var organization = await db.Organizations.AsNoTracking().SingleAsync(x => x.Id == tenant.OrganizationId, token);
        return Ok(new {
            providerName=organization.Name,providerAddress="",providerEmail="",providerPhone="",companyNumber="",
            vatNumber="",remittanceDetails="",paymentTermsDays=30,vatRate=0m,vatExemptionReason="VAT not charged"
        });
    }
    [HttpPut("invoice-profile")]
    public async Task<IActionResult> SaveProfile(InvoiceProfileRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.ProviderName) || request.PaymentTermsDays is < 1 or > 365 || request.VatRate is < 0 or > 100)
            return BadRequest(new { message = "Provider name, valid payment terms, and a VAT rate between 0 and 100 are required." });
        if (request.VatRate == 0 && string.IsNullOrWhiteSpace(request.VatExemptionReason))
            return BadRequest(new { message = "A VAT exemption or non-taxable reason is required when VAT is zero." });

        await using var command = await Command("""
            insert into finance_invoice_profiles(organization_id,branch_id,provider_name,provider_address,provider_email,provider_phone,company_number,vat_number,remittance_details,default_payment_terms_days,default_vat_rate,vat_exemption_reason,updated_at)
            values(@organization,@branch,@provider,@address,@email,@phone,@company,@vatNumber,@remittance,@terms,@vatRate,@exemption,now())
            on conflict(organization_id,branch_id) do update set provider_name=excluded.provider_name,provider_address=excluded.provider_address,provider_email=excluded.provider_email,provider_phone=excluded.provider_phone,company_number=excluded.company_number,vat_number=excluded.vat_number,remittance_details=excluded.remittance_details,default_payment_terms_days=excluded.default_payment_terms_days,default_vat_rate=excluded.default_vat_rate,vat_exemption_reason=excluded.vat_exemption_reason,updated_at=now()
            """, token);
        Add(command,"provider",request.ProviderName.Trim());Add(command,"address",request.ProviderAddress?.Trim()??"");Add(command,"email",request.ProviderEmail?.Trim()??"");Add(command,"phone",request.ProviderPhone?.Trim()??"");Add(command,"company",request.CompanyNumber?.Trim()??"");Add(command,"vatNumber",request.VatNumber?.Trim()??"");Add(command,"remittance",request.RemittanceDetails?.Trim()??"");Add(command,"terms",request.PaymentTermsDays);Add(command,"vatRate",request.VatRate);Add(command,"exemption",request.VatExemptionReason?.Trim()??"");
        await command.ExecuteNonQueryAsync(token);Audit("finance.invoice_profile_updated",null);await db.SaveChangesAsync(token);
        return NoContent();
    }

    [HttpGet("invoices")]
    public async Task<IActionResult> List(string? status,CancellationToken token)
    {
        await MarkOverdue(token);var rows=new List<object>();await using var command=await Command("""
            select d.invoice_id,d.invoice_number,d.invoice_date,d.due_date,d.customer_name,d.funder_name,d.currency,d.net_amount,d.vat_amount,d.gross_amount,i."Status",d.gross_amount-coalesce(p.paid,0)-coalesce(c.credited,0)+coalesce(r.refunded,0) balance
            from finance_invoice_details d join "Invoices" i on i."Id"=d.invoice_id
            left join (select invoice_id,sum(amount) paid from finance_payments group by invoice_id)p on p.invoice_id=d.invoice_id
            left join (select invoice_id,sum(amount) credited from finance_credit_notes where status='Issued' group by invoice_id)c on c.invoice_id=d.invoice_id
            left join (select invoice_id,sum(amount) refunded from finance_refunds group by invoice_id)r on r.invoice_id=d.invoice_id
            where d.organization_id=@organization and (@organizationWide or d.branch_id=@branch) and (@status='' or i."Status"=@status) order by d.invoice_date desc,d.invoice_number desc limit 500
            """,token);Add(command,"organizationWide",tenant.IsOrganizationWide);Add(command,"status",status?.Trim()??"");await using var reader=await command.ExecuteReaderAsync(token);while(await reader.ReadAsync(token))rows.Add(new{id=reader.GetGuid(0),invoiceNumber=reader.GetString(1),invoiceDate=reader.GetFieldValue<DateOnly>(2),dueDate=reader.GetFieldValue<DateOnly>(3),customer=reader.GetString(4),funder=reader.GetString(5),currency=reader.GetString(6),netAmount=reader.GetDecimal(7),vatAmount=reader.GetDecimal(8),grossAmount=reader.GetDecimal(9),status=reader.GetString(10),balance=reader.GetDecimal(11)});return Ok(rows);
    }
    [HttpGet("invoices/{id:guid}")]
    public async Task<IActionResult> Detail(Guid id,CancellationToken token)
    {
        await MarkOverdue(token);
        var document=await Load(id,token);return document is null?NotFound():Ok(document);
    }

    [HttpGet("invoices/{id:guid}/pdf")]
    public async Task<IActionResult> Pdf(Guid id,CancellationToken token)
    {
        await MarkOverdue(token);var document=await Load(id,token);if(document is null)return NotFound();
        if(document.Status=="Generated")return Conflict(new{message="Approve the invoice before downloading its final PDF."});
        Audit("finance.invoice_pdf_downloaded",id);await db.SaveChangesAsync(token);
        return File(InvoicePdfDocument.Create(document),"application/pdf",$"{document.Number}.pdf");
    }

    [HttpPost("invoices/{id:guid}/deliver")]
    public async Task<IActionResult> Deliver(Guid id,InvoiceDeliveryRequest request,CancellationToken token)
    {
        if(string.IsNullOrWhiteSpace(request.RecipientEmail)||!MailAddress.TryCreate(request.RecipientEmail.Trim(),out var recipient))return BadRequest(new{message="A valid recipient email is required."});
        var document=await Load(id,token);if(document is null)return NotFound();if(document.Status is not ("Issued" or "Part paid" or "Paid" or "Overdue" or "Credited"))return Conflict(new{message="Only an issued invoice can be delivered."});
        if(!configuration.GetValue<bool>("Email:Enabled"))return StatusCode(StatusCodes.Status503ServiceUnavailable,new{message="Invoice email delivery is not configured."});
        var bytes=InvoicePdfDocument.Create(document);var deliveryId=Guid.NewGuid();
        try
        {
            using var message=new MailMessage{From=new MailAddress(Required("Email:FromAddress"),configuration["Email:FromName"]??"AiCare"),Subject=$"Invoice {document.Number}",Body=$"Please find invoice {document.Number} attached. Amount due: {document.Currency} {Math.Max(0,document.Gross-document.Paid-document.Credits):0.00}.",IsBodyHtml=false};message.To.Add(recipient);var replyTo=configuration["Email:ReplyToAddress"];if(!string.IsNullOrWhiteSpace(replyTo))message.ReplyToList.Add(new MailAddress(replyTo));message.Attachments.Add(new Attachment(new MemoryStream(bytes),$"{document.Number}.pdf","application/pdf"));
            using var smtp=new SmtpClient(Required("Email:SmtpHost"),configuration.GetValue<int?>("Email:SmtpPort")??587){EnableSsl=configuration.GetValue<bool?>("Email:EnableSsl")??true,UseDefaultCredentials=false,Credentials=new NetworkCredential(Required("Email:Username"),Required("Email:Password")),DeliveryMethod=SmtpDeliveryMethod.Network,Timeout=30_000};
            await smtp.SendMailAsync(message,token);
            await RecordDelivery(deliveryId,id,recipient.Address,"Delivered","",DateTimeOffset.UtcNow,token);Audit("finance.invoice_delivered",id);await db.SaveChangesAsync(token);logger.LogInformation("Invoice {InvoiceId} delivered to {RecipientDomain}.",id,Domain(recipient.Address));return Accepted(new{id=deliveryId,status="Delivered"});
        }
        catch(Exception ex)
        {
            await RecordDelivery(deliveryId,id,recipient.Address,"Failed","SMTP delivery failed.",null,token);Audit("finance.invoice_delivery_failed",id);await db.SaveChangesAsync(token);logger.LogError(ex,"Invoice {InvoiceId} delivery failed.",id);return StatusCode(StatusCodes.Status502BadGateway,new{message="Invoice delivery failed."});
        }
    }

    [HttpGet("aged-receivables")]
    public async Task<IActionResult> AgedReceivables(CancellationToken token)
    {
        await MarkOverdue(token);var rows=new List<object>();await using var command=await Command("""
            select d.invoice_id,d.invoice_number,d.customer_name,d.funder_name,d.invoice_date,d.due_date,i."Status",d.gross_amount-coalesce(p.paid,0)-coalesce(c.credited,0)+coalesce(r.refunded,0) balance,greatest(current_date-d.due_date,0) age
            from finance_invoice_details d join "Invoices" i on i."Id"=d.invoice_id
            left join (select invoice_id,sum(amount) paid from finance_payments group by invoice_id)p on p.invoice_id=d.invoice_id
            left join (select invoice_id,sum(amount) credited from finance_credit_notes where status='Issued' group by invoice_id)c on c.invoice_id=d.invoice_id
            left join (select invoice_id,sum(amount) refunded from finance_refunds group by invoice_id)r on r.invoice_id=d.invoice_id
            where d.organization_id=@organization and (@organizationWide or d.branch_id=@branch) and i."Status" not in ('Generated','Approved','Void') and d.gross_amount-coalesce(p.paid,0)-coalesce(c.credited,0)+coalesce(r.refunded,0)>0 order by d.due_date
            """,token);Add(command,"organizationWide",tenant.IsOrganizationWide);await using var reader=await command.ExecuteReaderAsync(token);decimal current=0,d30=0,d60=0,d90=0,older=0;while(await reader.ReadAsync(token)){var balance=reader.GetDecimal(7);var age=reader.GetInt32(8);if(age==0)current+=balance;else if(age<=30)d30+=balance;else if(age<=60)d60+=balance;else if(age<=90)d90+=balance;else older+=balance;rows.Add(new{id=reader.GetGuid(0),invoiceNumber=reader.GetString(1),customer=reader.GetString(2),funder=reader.GetString(3),invoiceDate=reader.GetDateTime(4),dueDate=reader.GetDateTime(5),status=reader.GetString(6),balance,ageDays=age});}
        return Ok(new{asOf=DateOnly.FromDateTime(DateTime.UtcNow),totals=new{current,days1To30=d30,days31To60=d60,days61To90=d90,over90=older,total=current+d30+d60+d90+older},invoices=rows});
    }

    private async Task<InvoicePdfData?> Load(Guid id,CancellationToken token)
    {
        await using var command=await Command("""
            select d.invoice_number,d.invoice_date,d.due_date,i."Status",d.provider_name,d.provider_address,d.provider_email,d.provider_phone,d.company_number,d.vat_number,d.customer_name,d.customer_address,d.funder_name,d.payment_terms,d.remittance_details,d.currency,d.net_amount,d.vat_rate,d.vat_amount,d.gross_amount,d.vat_exemption_reason,coalesce((select sum(amount) from finance_payments where invoice_id=i."Id"),0)-coalesce((select sum(amount) from finance_refunds where invoice_id=i."Id"),0),coalesce((select sum(amount) from finance_credit_notes where invoice_id=i."Id" and status='Issued'),0)
            from "Invoices" i join finance_invoice_details d on d.invoice_id=i."Id" where i."Id"=@invoice and i."OrganizationId"=@organization and (@organizationWide or i."BranchId"=@branch)
            """,token);Add(command,"invoice",id);Add(command,"organizationWide",tenant.IsOrganizationWide);await using var reader=await command.ExecuteReaderAsync(token);if(!await reader.ReadAsync(token))return null;var values=new object[23];reader.GetValues(values);await reader.DisposeAsync();var lines=new List<InvoicePdfLine>();await using var lineCommand=await Command("select description,quantity,unit_rate,amount from finance_invoice_lines where invoice_id=@invoice and organization_id=@organization order by created_at",token);Add(lineCommand,"invoice",id);await using var lr=await lineCommand.ExecuteReaderAsync(token);while(await lr.ReadAsync(token))lines.Add(new(lr.GetString(0),lr.GetDecimal(1),lr.GetDecimal(2),lr.GetDecimal(3)));
        static DateOnly Date(object value)=>value is DateOnly date?date:DateOnly.FromDateTime((DateTime)value);
        return new((string)values[0],Date(values[1]),Date(values[2]),(string)values[3],(string)values[4],(string)values[5],(string)values[6],(string)values[7],(string)values[8],(string)values[9],(string)values[10],(string)values[11],(string)values[12],(string)values[13],(string)values[14],(string)values[15],(decimal)values[16],(decimal)values[17],(decimal)values[18],(decimal)values[19],(decimal)values[21],(decimal)values[22],(string)values[20],lines);
    }

    private async Task MarkOverdue(CancellationToken token){await using var command=await Command("update \"Invoices\" i set \"Status\"='Overdue' from finance_invoice_details d where d.invoice_id=i.\"Id\" and i.\"OrganizationId\"=@organization and (@organizationWide or i.\"BranchId\"=@branch) and i.\"Status\"='Issued' and d.due_date<current_date",token);Add(command,"organizationWide",tenant.IsOrganizationWide);await command.ExecuteNonQueryAsync(token);}
    private async Task RecordDelivery(Guid id,Guid invoice,string recipient,string status,string detail,DateTimeOffset? delivered,CancellationToken token){await using var command=await Command("insert into finance_invoice_deliveries(id,invoice_id,organization_id,branch_id,recipient_email,status,detail,delivered_at,requested_by) values(@id,@invoice,@organization,@branch,@recipient,@status,@detail,@delivered,@actor)",token);Add(command,"id",id);Add(command,"invoice",invoice);Add(command,"recipient",recipient);Add(command,"status",status);Add(command,"detail",detail);Add(command,"delivered",delivered);await command.ExecuteNonQueryAsync(token);}
    private async Task<DbCommand> Command(string sql,CancellationToken token){var connection=db.Database.GetDbConnection();if(connection.State!=ConnectionState.Open)await connection.OpenAsync(token);var command=connection.CreateCommand();command.CommandText=sql;Add(command,"organization",tenant.OrganizationId);Add(command,"branch",tenant.BranchId??TenantDefaults.BranchId);Add(command,"actor",user.UserName);return command;}
    private static void Add(DbCommand command,string name,object? value){if(command.Parameters.Contains(name))return;var p=command.CreateParameter();p.ParameterName=name;p.Value=value??DBNull.Value;command.Parameters.Add(p);}
    private void Audit(string action,Guid? id)=>db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),action,user.UserName,"Invoice",id,DateTimeOffset.UtcNow,tenant.OrganizationId,tenant.BranchId??TenantDefaults.BranchId));
    private string Required(string key)=>string.IsNullOrWhiteSpace(configuration[key])?throw new InvalidOperationException($"{key} is required."):configuration[key]!;
    private static string Domain(string email){var index=email.LastIndexOf('@');return index>=0?email[index..]:"unknown-domain";}
}

public sealed record InvoiceProfileRequest(string ProviderName,string? ProviderAddress,string? ProviderEmail,string? ProviderPhone,string? CompanyNumber,string? VatNumber,string? RemittanceDetails,int PaymentTermsDays,decimal VatRate,string? VatExemptionReason);
public sealed record InvoiceDeliveryRequest(string RecipientEmail);
