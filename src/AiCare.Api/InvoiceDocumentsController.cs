using System.Data;
using System.Data.Common;
using System.Net.Mail;
using System.Security.Cryptography;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using AiCare.Infrastructure.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AiCare.Api;

[ApiController]
[Authorize(Roles = "Administrator,BackOffice")]
[Route("api/phase1/finance")]
public sealed class InvoiceDocumentsController(
    CareDbContext db,
    ITenantContext tenant,
    ICurrentUserContext user,
    IConfiguration configuration,
    IResendEmailClient resend,
    ILogger<InvoiceDocumentsController> logger) : ControllerBase
{
    [HttpGet("invoice-profile")]
    public async Task<IActionResult> GetProfile(CancellationToken token)
    {
        await using var command = await Command("""
            select provider_name,provider_address,provider_email,provider_phone,company_number,vat_number,remittance_details,default_payment_terms_days,default_vat_rate,vat_exemption_reason,logo_sha256,
                   trim(default_currency),currency_symbol,tax_label,vat_rounding_policy,custom_numbering_enabled,invoice_number_prefix,invoice_number_start,invoice_number_padding,
                   coalesce((select max(v.version) from finance_invoice_profile_versions v where v.organization_id=p.organization_id and v.branch_id=p.branch_id),0)
            from finance_invoice_profiles p where organization_id=@organization and branch_id=@branch
            """, token);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (await reader.ReadAsync(token))
            return Ok(new {
                providerName=reader.GetString(0),providerAddress=reader.GetString(1),providerEmail=reader.GetString(2),
                providerPhone=reader.GetString(3),companyNumber=reader.GetString(4),vatNumber=reader.GetString(5),
                remittanceDetails=reader.GetString(6),paymentTermsDays=reader.GetInt32(7),vatRate=reader.GetDecimal(8),
                vatExemptionReason=reader.GetString(9),logoAvailable=!reader.IsDBNull(10),logoVersion=reader.IsDBNull(10)?null:reader.GetString(10),
                defaultCurrency=reader.GetString(11),currencySymbol=reader.GetString(12),taxLabel=reader.GetString(13),vatRoundingPolicy=reader.GetString(14),customNumberingEnabled=reader.GetBoolean(15),invoiceNumberPrefix=reader.GetString(16),invoiceNumberStart=reader.GetInt64(17),invoiceNumberPadding=reader.GetInt32(18),
                version=reader.GetInt32(19)
            });
        await reader.DisposeAsync();
        var organization = await db.Organizations.AsNoTracking().SingleAsync(x => x.Id == tenant.OrganizationId, token);
        return Ok(new {
            providerName=organization.Name,providerAddress="",providerEmail="",providerPhone="",companyNumber="",
            vatNumber="",remittanceDetails="",paymentTermsDays=30,vatRate=0m,vatExemptionReason="VAT not charged",defaultCurrency="GBP",currencySymbol="£",taxLabel="VAT",vatRoundingPolicy="Early",customNumberingEnabled=false,invoiceNumberPrefix="INV-",invoiceNumberStart=1L,invoiceNumberPadding=6,logoAvailable=false,logoVersion=(string?)null,version=0
        });
    }

    [HttpGet("invoice-profile/number-preview")]
    public async Task<IActionResult> NumberPreview(CancellationToken token)
    {
        var year = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).Year;
        await using var command = await Command("""
            select coalesce(p.custom_numbering_enabled,false),coalesce(p.invoice_number_prefix,'INV-'),coalesce(p.invoice_number_start,1),coalesce(p.invoice_number_padding,6),coalesce(s.next_value,p.invoice_number_start,1)
            from finance_invoice_profiles p left join finance_invoice_sequences s on s.organization_id=p.organization_id and s.invoice_year=@year
            where p.organization_id=@organization and p.branch_id=@branch
            """, token);
        Add(command, "year", year);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return NotFound();
        var enabled = reader.GetBoolean(0);
        var prefix = reader.GetString(1);
        var next = reader.GetInt64(4);
        var padding = reader.GetInt32(3);
        return Ok(new { year, enabled, nextValue = next, invoiceNumber = $"{(enabled ? prefix : "INV-")}{year}-{next.ToString().PadLeft(padding, '0')}" });
    }

    [HttpGet("invoice-profile/logo")]
    public async Task<IActionResult> GetLogo(CancellationToken token)
    {
        await using var command=await Command("select logo_content_type,logo_bytes,logo_sha256 from finance_invoice_profiles where organization_id=@organization and branch_id=@branch and logo_bytes is not null",token);
        await using var reader=await command.ExecuteReaderAsync(token);
        if(!await reader.ReadAsync(token))return NotFound();
        Response.Headers.CacheControl="private, max-age=300";
        Response.Headers.ETag=$"\"{reader.GetString(2)}\"";
        return File((byte[])reader[1],reader.GetString(0));
    }

    [HttpPost("invoice-profile/logo")]
    [RequestSizeLimit(2_097_152)]
    public async Task<IActionResult> UploadLogo(IFormFile logo,[FromForm] int expectedVersion,CancellationToken token)
    {
        if(expectedVersion<0||logo is null||logo.Length is <=0 or >2_097_152)return BadRequest(new{message="Choose a logo smaller than 2 MB and provide the current settings version."});
        await using var stream=new MemoryStream();await logo.CopyToAsync(stream,token);var bytes=stream.ToArray();
        var type=DetectLogoType(bytes);if(type is null)return BadRequest(new{message="Only valid PNG and JPEG logo files are supported."});
        var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async()=>
        {
            await using var transaction=await db.Database.BeginTransactionAsync(token);
            await using(var profileLock=await Command("select pg_advisory_xact_lock(hashtextextended(@key,0))",token)){Add(profileLock,"key",$"invoice-profile:{tenant.OrganizationId}:{tenant.BranchId??TenantDefaults.BranchId}");await profileLock.ExecuteNonQueryAsync(token);}
            var currentVersion=await CurrentProfileVersion(token);if(currentVersion!=expectedVersion){await transaction.RollbackAsync(token);return StaleProfile(currentVersion);}
            var organization=await db.Organizations.AsNoTracking().SingleAsync(x=>x.Id==tenant.OrganizationId,token);
            await using(var update=await Command("""
                insert into finance_invoice_profiles(organization_id,branch_id,provider_name,logo_content_type,logo_bytes,logo_sha256)
                values(@organization,@branch,@provider,@contentType,@bytes,@hash)
                on conflict(organization_id,branch_id) do update set logo_content_type=excluded.logo_content_type,logo_bytes=excluded.logo_bytes,logo_sha256=excluded.logo_sha256,updated_at=now()
                """,token)){Add(update,"provider",organization.Name);Add(update,"contentType",type);Add(update,"bytes",bytes);Add(update,"hash",hash);await update.ExecuteNonQueryAsync(token);}
            await AppendProfileVersion(token);
            Audit("finance.invoice_logo_updated",null);await db.SaveChangesAsync(token);await transaction.CommitAsync(token);
            return Ok(new{logoAvailable=true,logoVersion=hash});
        });
    }

    [HttpDelete("invoice-profile/logo")]
    public async Task<IActionResult> DeleteLogo([FromQuery] int expectedVersion,CancellationToken token)
    {
        if(expectedVersion<0)return BadRequest(new{message="Provide the current settings version."});
        return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async()=>
        {
            await using var transaction=await db.Database.BeginTransactionAsync(token);
            await using(var profileLock=await Command("select pg_advisory_xact_lock(hashtextextended(@key,0))",token)){Add(profileLock,"key",$"invoice-profile:{tenant.OrganizationId}:{tenant.BranchId??TenantDefaults.BranchId}");await profileLock.ExecuteNonQueryAsync(token);}
            var currentVersion=await CurrentProfileVersion(token);if(currentVersion!=expectedVersion){await transaction.RollbackAsync(token);return StaleProfile(currentVersion);}
            await using(var update=await Command("update finance_invoice_profiles set logo_content_type=null,logo_bytes=null,logo_sha256=null,updated_at=now() where organization_id=@organization and branch_id=@branch",token))await update.ExecuteNonQueryAsync(token);
            await AppendProfileVersion(token);
            Audit("finance.invoice_logo_removed",null);await db.SaveChangesAsync(token);await transaction.CommitAsync(token);return NoContent();
        });
    }
    [HttpPut("invoice-profile")]
    public async Task<IActionResult> SaveProfile(InvoiceProfileRequest request, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        if (request.ExpectedVersion < 0 || string.IsNullOrWhiteSpace(request.ProviderName) || request.ProviderName.Trim().Length > 200
            || request.ProviderAddress?.Trim().Length > 1000 || request.ProviderEmail?.Trim().Length > 254
            || request.ProviderPhone?.Trim().Length > 80 || request.CompanyNumber?.Trim().Length > 80
            || request.VatNumber?.Trim().Length > 80 || request.RemittanceDetails?.Trim().Length > 2000
            || request.VatExemptionReason?.Trim().Length > 500
            || request.DefaultCurrency?.Trim().Length != 3 || request.DefaultCurrency.Any(c=>c is < 'A' or > 'Z')
            || string.IsNullOrWhiteSpace(request.CurrencySymbol) || request.CurrencySymbol.Trim().Length > 8
            || string.IsNullOrWhiteSpace(request.TaxLabel) || request.TaxLabel.Trim().Length > 30
            || request.VatRoundingPolicy is not ("Early" or "Late")
            || request.InvoiceNumberPrefix?.Trim().Length is < 1 or > 20 || (request.InvoiceNumberPrefix??"").Any(c=>!(char.IsAsciiLetterOrDigit(c)||c is '/' or '_' or '-'))
            || request.InvoiceNumberStart is < 1 or > 999999999999 || request.InvoiceNumberPadding is < 1 or > 12
            || request.EffectiveFrom is DateTimeOffset effectiveFrom && (effectiveFrom < now.AddMinutes(-5) || effectiveFrom > now.AddYears(2))
            || request.PaymentTermsDays is < 1 or > 365 || request.VatRate is < 0 or > 100)
            return BadRequest(new { message = "Provider name, valid payment terms, a VAT rate between 0 and 100, and an effective time within two years are required." });
        if (!string.IsNullOrWhiteSpace(request.ProviderEmail)
            && (!MailAddress.TryCreate(request.ProviderEmail.Trim(), out var providerEmail)
                || !string.Equals(providerEmail.Address, request.ProviderEmail.Trim(), StringComparison.OrdinalIgnoreCase)))
            return BadRequest(new { message = "Enter a valid provider email address." });
        if (request.VatRate == 0 && string.IsNullOrWhiteSpace(request.VatExemptionReason))
            return BadRequest(new { message = "A VAT exemption or non-taxable reason is required when VAT is zero." });

        return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () =>
        {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await using (var profileLock = await Command("select pg_advisory_xact_lock(hashtextextended(@key,0))", token))
        {
            Add(profileLock, "key", $"invoice-profile:{tenant.OrganizationId}:{tenant.BranchId ?? TenantDefaults.BranchId}");
            await profileLock.ExecuteNonQueryAsync(token);
        }

        var currentVersion = await CurrentProfileVersion(token);
        if (currentVersion != request.ExpectedVersion)
        {
            await transaction.RollbackAsync(token);
            return StaleProfile(currentVersion);
        }

        if (request.EffectiveFrom is DateTimeOffset scheduledFor && scheduledFor > now)
        {
            if(currentVersion==0)
            {
                var organization=await db.Organizations.AsNoTracking().SingleAsync(x=>x.Id==tenant.OrganizationId,token);
                await using var baseline=await Command("insert into finance_invoice_profiles(organization_id,branch_id,provider_name) values(@organization,@branch,@provider) on conflict(organization_id,branch_id) do nothing",token);
                Add(baseline,"provider",organization.Name);await baseline.ExecuteNonQueryAsync(token);
            }
            await AppendScheduledProfileVersion(request, scheduledFor, token);
            Audit("finance.invoice_profile_scheduled", null);await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return NoContent();
        }

        await using var command = await Command("""
            insert into finance_invoice_profiles(organization_id,branch_id,provider_name,provider_address,provider_email,provider_phone,company_number,vat_number,remittance_details,default_payment_terms_days,default_vat_rate,vat_exemption_reason,default_currency,currency_symbol,tax_label,vat_rounding_policy,custom_numbering_enabled,invoice_number_prefix,invoice_number_start,invoice_number_padding,updated_at)
            values(@organization,@branch,@provider,@address,@email,@phone,@company,@vatNumber,@remittance,@terms,@vatRate,@exemption,@currency,@symbol,@taxLabel,@roundingPolicy,@numberingEnabled,@numberPrefix,@numberStart,@numberPadding,now())
            on conflict(organization_id,branch_id) do update set provider_name=excluded.provider_name,provider_address=excluded.provider_address,provider_email=excluded.provider_email,provider_phone=excluded.provider_phone,company_number=excluded.company_number,vat_number=excluded.vat_number,remittance_details=excluded.remittance_details,default_payment_terms_days=excluded.default_payment_terms_days,default_vat_rate=excluded.default_vat_rate,vat_exemption_reason=excluded.vat_exemption_reason,default_currency=excluded.default_currency,currency_symbol=excluded.currency_symbol,tax_label=excluded.tax_label,vat_rounding_policy=excluded.vat_rounding_policy,custom_numbering_enabled=excluded.custom_numbering_enabled,invoice_number_prefix=excluded.invoice_number_prefix,invoice_number_start=excluded.invoice_number_start,invoice_number_padding=excluded.invoice_number_padding,updated_at=now()
            """, token);
        Add(command,"provider",request.ProviderName.Trim());Add(command,"address",request.ProviderAddress?.Trim()??"");Add(command,"email",request.ProviderEmail?.Trim()??"");Add(command,"phone",request.ProviderPhone?.Trim()??"");Add(command,"company",request.CompanyNumber?.Trim()??"");Add(command,"vatNumber",request.VatNumber?.Trim()??"");Add(command,"remittance",request.RemittanceDetails?.Trim()??"");Add(command,"terms",request.PaymentTermsDays);Add(command,"vatRate",request.VatRate);Add(command,"exemption",request.VatExemptionReason?.Trim()??"");Add(command,"currency",request.DefaultCurrency.Trim());Add(command,"symbol",request.CurrencySymbol.Trim());Add(command,"taxLabel",request.TaxLabel.Trim());Add(command,"roundingPolicy",request.VatRoundingPolicy);Add(command,"numberingEnabled",request.CustomNumberingEnabled);Add(command,"numberPrefix",request.InvoiceNumberPrefix.Trim());Add(command,"numberStart",request.InvoiceNumberStart);Add(command,"numberPadding",request.InvoiceNumberPadding);
        await command.ExecuteNonQueryAsync(token);
        await AppendProfileVersion(token);
        Audit("finance.invoice_profile_updated",null);await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return NoContent();
        });
    }

    [HttpGet("invoices")]
    public async Task<IActionResult> List(string? status,CancellationToken token)
    {
        await MarkOverdue(token);var rows=new List<object>();await using var command=await Command("""
            select d.invoice_id,d.invoice_number,d.invoice_date,d.due_date,d.customer_name,d.funder_name,d.currency,d.net_amount,d.vat_amount,d.gross_amount,l.status,l.balance,d.care_recipient_name
            from finance_invoice_ledger l join finance_invoice_details d on d.invoice_id=l.invoice_id and d.organization_id=l.organization_id
            where l.organization_id=@organization and (@organizationWide or l.branch_id=@branch) and (@status='' or l.status=@status) order by d.invoice_date desc,d.invoice_number desc limit 500
            """,token);Add(command,"organizationWide",tenant.IsOrganizationWide);Add(command,"status",status?.Trim()??"");await using var reader=await command.ExecuteReaderAsync(token);while(await reader.ReadAsync(token))rows.Add(new{id=reader.GetGuid(0),invoiceNumber=reader.GetString(1),invoiceDate=reader.GetFieldValue<DateOnly>(2),dueDate=reader.GetFieldValue<DateOnly>(3),customer=reader.GetString(4),funder=reader.GetString(5),currency=reader.GetString(6),netAmount=reader.GetDecimal(7),vatAmount=reader.GetDecimal(8),grossAmount=reader.GetDecimal(9),status=reader.GetString(10),balance=reader.GetDecimal(11),careRecipient=reader.IsDBNull(12)?null:reader.GetString(12)});return Ok(rows);
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
        if(document.Status is "Generated" or "Reviewed" or "Approved")return Conflict(new{message="Issue the invoice before downloading its final PDF."});
        if(!await CanExportDocument(id,token))return Forbid();
        byte[] bytes;
        try { bytes=await GetOrCreatePdf(id,document,token); }
        catch(FinancePdfTextException ex) { return Conflict(new{code="invoice_pdf_text_unsupported",message=ex.Message}); }
        Audit("finance.invoice_pdf_downloaded",id);await db.SaveChangesAsync(token);
        return File(bytes,"application/pdf",$"{document.Number}.pdf");
    }

    [HttpPost("invoices/{id:guid}/deliver")]
    public async Task<IActionResult> Deliver(Guid id,InvoiceDeliveryRequest request,CancellationToken token)
    {
        if(string.IsNullOrWhiteSpace(request.RecipientEmail)||!MailAddress.TryCreate(request.RecipientEmail.Trim(),out var recipient))return BadRequest(new{message="A valid recipient email is required."});
        var document=await Load(id,token);if(document is null)return NotFound();if(document.Status is not ("Issued" or "Part paid" or "Paid" or "Overdue" or "Credited" or "Written off"))return Conflict(new{message="Only an issued invoice can be delivered."});
        if(!configuration.GetValue<bool>("Email:Enabled")||string.IsNullOrWhiteSpace(configuration["Resend:ApiKey"])||string.IsNullOrWhiteSpace(configuration["Resend:FromAddress"]))return StatusCode(StatusCodes.Status503ServiceUnavailable,new{message="Invoice email delivery is not configured."});
        if(!await CanExportDocument(id,token))return Forbid();
        byte[] bytes;
        try { bytes=await GetOrCreatePdf(id,document,token); }
        catch(FinancePdfTextException ex) { return Conflict(new{code="invoice_pdf_text_unsupported",message=ex.Message}); }
        var deliveryId=Guid.NewGuid();
        try
        {
            var body=$"Please find invoice {document.Number} attached. Amount due: {document.Currency} {Math.Max(0,document.Gross-document.Paid-document.Credits-document.WriteOffs):0.00}.";
            var providerId=await resend.SendMessageAsync(new ResendMessage(recipient.Address,$"Invoice {document.Number}",body,$"invoice:{deliveryId:N}",Attachment:new ResendAttachment($"{document.Number}.pdf",bytes)),token);
            await RecordDelivery(deliveryId,id,recipient.Address,"Accepted",$"Resend message ID: {providerId}",null,token);Audit("finance.invoice_delivery_accepted",id);await db.SaveChangesAsync(token);logger.LogInformation("Resend accepted invoice {InvoiceId} for {RecipientDomain} as {ProviderMessageId}.",id,Domain(recipient.Address),providerId);return Accepted(new{id=deliveryId,status="Accepted"});
        }
        catch(Exception ex)
        {
            await RecordDelivery(deliveryId,id,recipient.Address,"Failed","Resend request failed.",null,token);Audit("finance.invoice_delivery_failed",id);await db.SaveChangesAsync(token);logger.LogError(ex,"Invoice {InvoiceId} delivery failed.",id);return StatusCode(StatusCodes.Status502BadGateway,new{message="Invoice delivery failed."});
        }
    }

    [HttpGet("aged-receivables")]
    public async Task<IActionResult> AgedReceivables(CancellationToken token)
    {
        await MarkOverdue(token);var rows=new List<object>();var buckets=new Dictionary<string,decimal[]>();
        await using var command=await Command("""
          select l.invoice_id,l.invoice_number,d.customer_name,d.funder_name,l.invoice_date,l.due_date,l.status,l.outstanding,
            greatest((now() at time zone 'Europe/London')::date-l.due_date,0),l.currency
          from finance_invoice_ledger l join finance_invoice_details d on d.invoice_id=l.invoice_id and d.organization_id=l.organization_id
          where l.organization_id=@organization and (@organizationWide or l.branch_id=@branch) and l.is_issued and l.outstanding>0 order by l.due_date,l.invoice_id
          """,token);Add(command,"organizationWide",tenant.IsOrganizationWide);
        await using var reader=await command.ExecuteReaderAsync(token);
        while(await reader.ReadAsync(token))
        {
          var balance=reader.GetDecimal(7);var age=reader.GetInt32(8);var currency=reader.GetString(9);
          if(!buckets.TryGetValue(currency,out var amounts)){amounts=new decimal[5];buckets[currency]=amounts;}
          amounts[age==0?0:age<=30?1:age<=60?2:age<=90?3:4]+=balance;
          rows.Add(new{id=reader.GetGuid(0),invoiceNumber=reader.GetString(1),customer=reader.GetString(2),funder=reader.GetString(3),invoiceDate=reader.GetFieldValue<DateOnly>(4),dueDate=reader.GetFieldValue<DateOnly>(5),status=reader.GetString(6),balance,ageDays=age,currency});
        }
        var totals=buckets.OrderBy(x=>x.Key).Select(x=>new{currency=x.Key,current=x.Value[0],days1To30=x.Value[1],days31To60=x.Value[2],days61To90=x.Value[3],over90=x.Value[4],total=x.Value.Sum()}).ToArray();
        return Ok(new{asOf=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"Europe/London").DateTime),currencyTotals=totals,totals=totals.Length==1?totals[0]:null,invoices=rows});
    }

    private async Task<byte[]> GetOrCreatePdf(Guid id,InvoicePdfData document,CancellationToken token)
    {
        async Task<byte[]?> Read()
        {
            await using var existing=await Command("select pdf_bytes from finance_invoice_document_versions where invoice_id=@invoice and organization_id=@organization and (@organizationWide or branch_id=@branch) and version=1",token);
            Add(existing,"invoice",id);Add(existing,"organizationWide",tenant.IsOrganizationWide);
            return await existing.ExecuteScalarAsync(token) as byte[];
        }
        var archived=await Read();
        if(archived is not null)return archived;
        var rendered=InvoicePdfDocument.Create(document);
        await using(var insert=await Command("insert into finance_invoice_document_versions(id,invoice_id,organization_id,branch_id,version,pdf_bytes,created_by) select @id,i.\"Id\",i.\"OrganizationId\",i.\"BranchId\",1,@bytes,@actor from \"Invoices\" i where i.\"Id\"=@invoice and i.\"OrganizationId\"=@organization and (@organizationWide or i.\"BranchId\"=@branch) on conflict(invoice_id,version) do nothing",token))
        {
            Add(insert,"id",Guid.NewGuid());Add(insert,"invoice",id);Add(insert,"organizationWide",tenant.IsOrganizationWide);Add(insert,"bytes",rendered);
            await insert.ExecuteNonQueryAsync(token);
        }
        return await Read()??throw new InvalidOperationException("The issued invoice document could not be archived.");
    }

    private async Task<bool> CanExportDocument(Guid id,CancellationToken token)
    {
        await using var c=await Command("select p.id from finance_export_permissions p join \"Invoices\" i on i.\"OrganizationId\"=p.organization_id and i.\"BranchId\"=p.branch_id where i.\"Id\"=@invoice and p.organization_id=@organization and p.user_id=@exportUser and p.export_kind='InvoiceDocuments' and p.allow_identifiers and p.allow_rates and p.approved_by is not null and (now() at time zone 'Europe/London')::date between p.valid_from and p.valid_to order by p.id limit 1",token);Add(c,"invoice",id);Add(c,"exportUser",user.UserId??Guid.Empty);if(await c.ExecuteScalarAsync(token) is not Guid grant)return false;Audit($"finance.invoice_document_export_authority:{grant}",id);return true;
    }
    private async Task<InvoicePdfData?> Load(Guid id,CancellationToken token)
    {
        await using var command=await Command("""
            select d.invoice_number,d.invoice_date,d.due_date,i."Status",d.provider_name,d.provider_address,d.provider_email,d.provider_phone,d.company_number,d.vat_number,d.customer_name,d.customer_address,d.funder_name,d.payment_terms,d.remittance_details,d.currency,d.net_amount,d.vat_rate,d.vat_amount,d.gross_amount,d.vat_exemption_reason,coalesce((select sum(amount) from finance_payments where invoice_id=i."Id" and organization_id=i."OrganizationId"),0)+coalesce((select sum(amount) from finance_payment_allocations where invoice_id=i."Id" and organization_id=i."OrganizationId" and reversed_at is null),0)-coalesce((select sum(amount) from finance_refunds where invoice_id=i."Id" and organization_id=i."OrganizationId"),0),coalesce((select sum(amount) from finance_credit_notes where invoice_id=i."Id" and organization_id=i."OrganizationId" and status='Issued'),0),d.care_recipient_name,coalesce((select sum(amount) from finance_write_offs where invoice_id=i."Id" and organization_id=i."OrganizationId"),0),d.settings_snapshot->>'currencySymbol',coalesce(d.settings_snapshot->>'taxLabel','VAT'),coalesce(d.settings_snapshot->>'vatRoundingPolicy','Early')
            from "Invoices" i join finance_invoice_details d on d.invoice_id=i."Id" where i."Id"=@invoice and i."OrganizationId"=@organization and (@organizationWide or i."BranchId"=@branch)
            """,token);Add(command,"invoice",id);Add(command,"organizationWide",tenant.IsOrganizationWide);await using var reader=await command.ExecuteReaderAsync(token);if(!await reader.ReadAsync(token))return null;var values=new object[28];reader.GetValues(values);await reader.DisposeAsync();var lines=new List<InvoicePdfLine>();await using var lineCommand=await Command("select description,quantity,unit_rate,amount,vat_treatment,vat_rate,vat_amount from finance_invoice_lines where invoice_id=@invoice and organization_id=@organization order by created_at",token);Add(lineCommand,"invoice",id);await using var lr=await lineCommand.ExecuteReaderAsync(token);while(await lr.ReadAsync(token))lines.Add(new(lr.GetString(0),lr.GetDecimal(1),lr.GetDecimal(2),lr.GetDecimal(3),lr.IsDBNull(4)?null:lr.GetString(4),lr.IsDBNull(5)?null:lr.GetDecimal(5),lr.IsDBNull(6)?null:lr.GetDecimal(6)));
        static DateOnly Date(object value)=>value is DateOnly date?date:DateOnly.FromDateTime((DateTime)value);
        return new((string)values[0],Date(values[1]),Date(values[2]),(string)values[3],(string)values[4],(string)values[5],(string)values[6],(string)values[7],(string)values[8],(string)values[9],(string)values[10],(string)values[11],(string)values[12],(string)values[13],(string)values[14],(string)values[15],(decimal)values[16],(decimal)values[17],(decimal)values[18],(decimal)values[19],(decimal)values[21],(decimal)values[22],(string)values[20],lines,values[23] is string recipient ? recipient : null,(decimal)values[24],values[25] is string symbol?symbol:null,(string)values[26],(string)values[27]);
    }

    private async Task MarkOverdue(CancellationToken token){await using var command=await Command("update \"Invoices\" i set \"Status\"='Overdue' from finance_invoice_details d where d.invoice_id=i.\"Id\" and i.\"OrganizationId\"=@organization and (@organizationWide or i.\"BranchId\"=@branch) and i.\"Status\"='Issued' and d.due_date<(now() at time zone 'Europe/London')::date",token);Add(command,"organizationWide",tenant.IsOrganizationWide);await command.ExecuteNonQueryAsync(token);}
    private async Task AppendProfileVersion(CancellationToken token)
    {
        await using var version=await Command("""
            insert into finance_invoice_profile_versions(id,organization_id,branch_id,version,effective_from,profile_snapshot,changed_by)
            select @id,p.organization_id,p.branch_id,coalesce((select max(v.version) from finance_invoice_profile_versions v where v.organization_id=p.organization_id and v.branch_id=p.branch_id),0)+1,now(),
                jsonb_build_object('providerName',p.provider_name,'providerAddress',p.provider_address,'providerEmail',p.provider_email,'providerPhone',p.provider_phone,'companyNumber',p.company_number,'vatNumber',p.vat_number,'remittanceDetails',p.remittance_details,'paymentTermsDays',p.default_payment_terms_days,'vatRate',p.default_vat_rate,'vatExemptionReason',p.vat_exemption_reason,'defaultCurrency',trim(p.default_currency),'currencySymbol',p.currency_symbol,'taxLabel',p.tax_label,'vatRoundingPolicy',p.vat_rounding_policy,'logoSha256',p.logo_sha256),@actor
            from finance_invoice_profiles p where p.organization_id=@organization and p.branch_id=@branch
            """,token);Add(version,"id",Guid.NewGuid());await version.ExecuteNonQueryAsync(token);
    }
    private async Task<int> CurrentProfileVersion(CancellationToken token)
    {
        await using var command=await Command("select coalesce(max(version),0) from finance_invoice_profile_versions where organization_id=@organization and branch_id=@branch",token);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token));
    }
    private async Task AppendScheduledProfileVersion(InvoiceProfileRequest request,DateTimeOffset effectiveFrom,CancellationToken token)
    {
        await using var version=await Command("""
            insert into finance_invoice_profile_versions(id,organization_id,branch_id,version,effective_from,profile_snapshot,changed_by)
            values(@id,@organization,@branch,@version,@effectiveFrom,
                jsonb_build_object('providerName',@provider,'providerAddress',@address,'providerEmail',@email,'providerPhone',@phone,'companyNumber',@company,'vatNumber',@vatNumber,'remittanceDetails',@remittance,'paymentTermsDays',@terms,'vatRate',@vatRate,'vatExemptionReason',@exemption,'defaultCurrency',@currency,'currencySymbol',@symbol,'taxLabel',@taxLabel,'vatRoundingPolicy',@roundingPolicy,'logoSha256',(select logo_sha256 from finance_invoice_profiles where organization_id=@organization and branch_id=@branch)),@actor)
            """,token);
        Add(version,"id",Guid.NewGuid());Add(version,"version",request.ExpectedVersion+1);Add(version,"effectiveFrom",effectiveFrom);
        Add(version,"provider",request.ProviderName.Trim());Add(version,"address",request.ProviderAddress?.Trim()??"");Add(version,"email",request.ProviderEmail?.Trim()??"");Add(version,"phone",request.ProviderPhone?.Trim()??"");Add(version,"company",request.CompanyNumber?.Trim()??"");Add(version,"vatNumber",request.VatNumber?.Trim()??"");Add(version,"remittance",request.RemittanceDetails?.Trim()??"");Add(version,"terms",request.PaymentTermsDays);Add(version,"vatRate",request.VatRate);Add(version,"exemption",request.VatExemptionReason?.Trim()??"");Add(version,"currency",request.DefaultCurrency.Trim());Add(version,"symbol",request.CurrencySymbol.Trim());Add(version,"taxLabel",request.TaxLabel.Trim());Add(version,"roundingPolicy",request.VatRoundingPolicy);
        await version.ExecuteNonQueryAsync(token);
    }
    private IActionResult StaleProfile(int currentVersion)=>Conflict(new{message="Invoice settings changed after you opened them. Reload the latest settings before saving again.",currentVersion});
    private static string? DetectLogoType(byte[] bytes)
    {
        if(bytes.Length>=24&&bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})&&bytes[12]==73&&bytes[13]==72&&bytes[14]==68&&bytes[15]==82)
        {
            var width=System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16,4));var height=System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20,4));
            return width is >0 and <=4000&&height is >0 and <=2000?"image/png":null;
        }
        if(bytes.Length>=4&&bytes[0]==0xff&&bytes[1]==0xd8&&bytes[^2]==0xff&&bytes[^1]==0xd9)return "image/jpeg";
        return null;
    }
    private async Task RecordDelivery(Guid id,Guid invoice,string recipient,string status,string detail,DateTimeOffset? delivered,CancellationToken token){await using var command=await Command("insert into finance_invoice_deliveries(id,invoice_id,organization_id,branch_id,recipient_email,status,detail,delivered_at,requested_by) values(@id,@invoice,@organization,@branch,@recipient,@status,@detail,@delivered,@actor)",token);Add(command,"id",id);Add(command,"invoice",invoice);Add(command,"recipient",recipient);Add(command,"status",status);Add(command,"detail",detail);Add(command,"delivered",delivered);await command.ExecuteNonQueryAsync(token);}
    private async Task<DbCommand> Command(string sql,CancellationToken token){var connection=db.Database.GetDbConnection();if(connection.State!=ConnectionState.Open)await connection.OpenAsync(token);var command=connection.CreateCommand();command.Transaction=db.Database.CurrentTransaction?.GetDbTransaction();command.CommandText=sql;Add(command,"organization",tenant.OrganizationId);Add(command,"branch",tenant.BranchId??TenantDefaults.BranchId);Add(command,"actor",user.UserName);return command;}
    private static void Add(DbCommand command,string name,object? value){if(command.Parameters.Contains(name))return;var p=command.CreateParameter();p.ParameterName=name;p.Value=value??DBNull.Value;command.Parameters.Add(p);}
    private void Audit(string action,Guid? id)=>db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),action,user.UserName,"Invoice",id,DateTimeOffset.UtcNow,tenant.OrganizationId,tenant.BranchId??TenantDefaults.BranchId));
    private static string Domain(string email){var index=email.LastIndexOf('@');return index>=0?email[index..]:"unknown-domain";}
}

public sealed record InvoiceProfileRequest(string ProviderName,string? ProviderAddress,string? ProviderEmail,string? ProviderPhone,string? CompanyNumber,string? VatNumber,string? RemittanceDetails,int PaymentTermsDays,decimal VatRate,string? VatExemptionReason,int ExpectedVersion,string DefaultCurrency="GBP",string CurrencySymbol="£",string TaxLabel="VAT",string VatRoundingPolicy="Early",bool CustomNumberingEnabled=false,string InvoiceNumberPrefix="INV-",long InvoiceNumberStart=1,int InvoiceNumberPadding=6,DateTimeOffset? EffectiveFrom=null);
public sealed record InvoiceDeliveryRequest(string RecipientEmail);
