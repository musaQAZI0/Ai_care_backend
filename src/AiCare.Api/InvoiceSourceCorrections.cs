using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace AiCare.Api;

public sealed partial class FinancialAdjustmentsController
{
    [HttpGet("invoice-corrections")]
    public async Task<IActionResult> InvoiceCorrections(CancellationToken t) => Ok(await Rows("""
        select c.*,coalesce((select jsonb_agg(to_jsonb(l)) from finance_invoice_correction_lines l where l.correction_id=c.id),'[]'::jsonb) lines
        from finance_invoice_corrections c where c.organization_id=@org and (@wide or c.branch_id=@branch) order by c.created_at desc
        """, _ => { }, t));

    [HttpPost("invoice-corrections")]
    public Task<IActionResult> CreateInvoiceCorrection(InvoiceSourceCorrectionRequest r, CancellationToken t) => Tx(async () =>
    {
        if (user.UserId is null) return Unauthorized();
        if (r.RequestId == Guid.Empty || r.InvoiceDate == default || r.PaymentTermsDays is < 1 or > 365 ||
            r.InvoiceDate > DateOnly.MaxValue.AddDays(-r.PaymentTermsDays) || string.IsNullOrWhiteSpace(r.SourceCorrectionReference) ||
            string.IsNullOrWhiteSpace(r.Reason) || r.Lines is null || r.Lines.Length is < 1 or > 500 ||
            r.Lines.Select(x => x.OriginalLineId).Distinct().Count() != r.Lines.Length ||
            r.Lines.Any(x => x.ServiceDate == default || string.IsNullOrWhiteSpace(x.ServiceType) || string.IsNullOrWhiteSpace(x.Description) ||
                x.Quantity is <= 0 or > 99999999.99999999m || decimal.Round(x.Quantity, 8) != x.Quantity ||
                x.UnitRate is < 0 or > 99999999.9999m || decimal.Round(x.UnitRate, 4) != x.UnitRate || x.RoundingMode is not ("ToEven" or "AwayFromZero")))
            return BadRequest(new { message = "Original source lines, explicit corrected service dates/types, quantities, rates, rounding, invoice date, terms and correction reference are required." });
        var invoice = await Invoice(r.OriginalInvoiceId, t); if (invoice is null) return NotFound();
        if (!invoice.Value.GetProperty("is_issued").GetBoolean()) return Conflict(new { message = "Only issued invoices can have linked replacements." });
        var snapshot = JsonSerializer.Serialize(r); var branch = invoice.Value.GetProperty("branch_id").GetGuid();
        var existing = await Rows("select request_snapshot=cast(@snapshot as jsonb) and created_by=@actor matches from finance_invoice_corrections where id=@id and organization_id=@org and (@wide or branch_id=@branch)",
            c => { Add(c, "id", r.RequestId); Add(c, "snapshot", snapshot); }, t);
        if (existing.Count != 0) return existing[0].GetProperty("matches").GetBoolean() ? Ok(new { id = r.RequestId, replayed = true }) : Conflict(new { message = "Correction ID already has different details or creator." });
        await Exec("""
            insert into finance_invoice_corrections(id,original_invoice_id,organization_id,branch_id,invoice_date,payment_terms_days,source_correction_reference,reason,request_snapshot,created_by)
            values(@id,@original,@org,@target,@day,@terms,@reference,@reason,cast(@snapshot as jsonb),@actor)
            """, c => { Add(c, "id", r.RequestId); Add(c, "original", r.OriginalInvoiceId); Add(c, "target", branch);
                Add(c, "day", r.InvoiceDate); Add(c, "terms", r.PaymentTermsDays); Add(c, "reference", r.SourceCorrectionReference.Trim());
                Add(c, "reason", r.Reason.Trim()); Add(c, "snapshot", snapshot); }, t);
        foreach (var line in r.Lines)
        {
            var net = Math.Round(line.Quantity * line.UnitRate, 2, line.RoundingMode == "ToEven" ? MidpointRounding.ToEven : MidpointRounding.AwayFromZero);
            if (net > 9999999999.99m) return BadRequest(new { message = "Corrected line exceeds the ledger's monetary limit." });
            var policies = await Rows("""
                select id,treatment,rate,rounding_mode,case tax_point when 'ServiceDate' then @serviceDay else @invoiceDay end tax_day
                from finance_vat_treatments where organization_id=@org and branch_id=@target and approved_by is not null
                  and lower(service_type)=lower(@service) and (case tax_point when 'ServiceDate' then @serviceDay else @invoiceDay end) between valid_from and valid_to
                """, c => { Add(c, "target", branch); Add(c, "service", line.ServiceType.Trim()); Add(c, "serviceDay", line.ServiceDate); Add(c, "invoiceDay", r.InvoiceDate); }, t);
            if (policies.Count != 1) return Conflict(new { message = "Exactly one adviser-approved VAT treatment must cover each corrected supply." });
            var policy = policies[0]; var rate = policy.GetProperty("rate").GetDecimal();
            var tax = Math.Round(net * rate / 100m, 2, policy.GetProperty("rounding_mode").GetString() == "ToEven" ? MidpointRounding.ToEven : MidpointRounding.AwayFromZero);
            await Exec("""
                insert into finance_invoice_correction_lines(id,correction_id,original_line_id,service_date,service_type,description,quantity,unit_rate,rounding_mode,net_amount,vat_treatment_id,vat_treatment,vat_rate,vat_amount,tax_point_date)
                values(@line,@id,@source,@day,@service,@description,@quantity,@rate,@rounding,@net,@treatmentId,@treatment,@vatRate,@tax,@taxDay)
                """, c => { Add(c, "line", Guid.NewGuid()); Add(c, "id", r.RequestId); Add(c, "source", line.OriginalLineId);
                    Add(c, "day", line.ServiceDate); Add(c, "service", line.ServiceType.Trim()); Add(c, "description", line.Description.Trim());
                    Add(c, "quantity", line.Quantity); Add(c, "rate", line.UnitRate); Add(c, "rounding", line.RoundingMode); Add(c, "net", net);
                    Add(c, "treatmentId", policy.GetProperty("id").GetGuid()); Add(c, "treatment", policy.GetProperty("treatment").GetString());
                    Add(c, "vatRate", rate); Add(c, "tax", tax); Add(c, "taxDay", DateOnly.Parse(policy.GetProperty("tax_day").GetString()!)); }, t);
        }
        await Audit("INVOICE_SOURCE_CORRECTION_SUBMITTED", r.RequestId, branch, t);
        return Created("", new { id = r.RequestId, status = "Submitted" });
    }, t);

    [HttpPost("invoice-corrections/{id:guid}/review")]
    public Task<IActionResult> ReviewInvoiceCorrection(Guid id, AdjustmentReviewRequest r, CancellationToken t) => Tx(async () =>
    {
        if (r.Decision is not ("Approve" or "Reject") || string.IsNullOrWhiteSpace(r.Reason)) return BadRequest();
        var rows = await Rows("select * from finance_invoice_corrections where id=@id and organization_id=@org and (@wide or branch_id=@branch) for update", c => Add(c, "id", id), t);
        if (rows.Count != 1) return NotFound();
        var row = rows[0]; var status = r.Decision == "Approve" ? "Approved" : "Rejected";
        if (row.GetProperty("status").GetString() != "Submitted") return row.GetProperty("status").GetString() == status &&
            row.GetProperty("reviewed_by").GetGuid() == user.UserId && row.GetProperty("review_reason").GetString() == r.Reason.Trim()
            ? Ok(new { id, replacementInvoiceId = row.GetProperty("replacement_invoice_id"), replayed = true }) : Conflict(new { message = "Correction was already reviewed." });
        if (row.GetProperty("created_by").GetGuid() == user.UserId) return Conflict(new { message = "A different finance reviewer must review the source correction." });
        var branch = row.GetProperty("branch_id").GetGuid();
        var originalId = row.GetProperty("original_invoice_id").GetGuid(); var original = await Invoice(originalId, t);
        if (original is null) return NotFound();
        Guid? authority = null; Guid? replacement = null;
        if (r.Decision == "Approve")
        {
            var totals = await Rows("select coalesce(sum(net_amount),0) net,coalesce(sum(vat_amount),0) tax from finance_invoice_correction_lines where correction_id=@id", c => Add(c, "id", id), t);
            var net = totals[0].GetProperty("net").GetDecimal(); var tax = totals[0].GetProperty("tax").GetDecimal(); var gross = net + tax;
            if (gross > 9999999999.99m) return Conflict(new { message = "Replacement total exceeds the ledger monetary limit." });
            var grants = await Rows("""
                select id from finance_adjustment_authorities where organization_id=@org and branch_id=@target and reviewer_id=@actor
                  and approved_by is not null and currency=@currency and maximum_amount>=@amount
                  and (now() at time zone 'Europe/London')::date between valid_from and valid_to order by maximum_amount,id limit 1
                """, c => { Add(c, "target", branch); Add(c, "currency", original.Value.GetProperty("currency").GetString());
                    Add(c, "amount", Math.Max(gross, original.Value.GetProperty("gross_amount").GetDecimal())); }, t);
            if (grants.Count != 1) return Conflict(new { message = "Approved authority must cover both original and replacement totals." });
            authority = grants[0].GetProperty("id").GetGuid(); replacement = Guid.NewGuid();
            await Exec("""
                insert into "Invoices"("Id","ServiceUserId","Funder","Amount","Status","IssuedAt","OrganizationId","BranchId")
                  select @replacement,"ServiceUserId","Funder",@gross,'Generated',now(),"OrganizationId","BranchId" from "Invoices" where "Id"=@original and "OrganizationId"=@org;
                """, c => { Add(c, "replacement", replacement); Add(c, "original", originalId); Add(c, "gross", gross); }, t);
            var day = DateOnly.Parse(row.GetProperty("invoice_date").GetString()!);
            await using var sequence = await Command("insert into finance_invoice_sequences(organization_id,invoice_year,next_value) values(@org,@year,2) on conflict(organization_id,invoice_year) do update set next_value=finance_invoice_sequences.next_value+1 returning next_value-1", t);
            Add(sequence, "year", day.Year); var number = $"INV-{day.Year}-{Convert.ToInt64(await sequence.ExecuteScalarAsync(t)):000000}";
            await Exec("""
                insert into finance_invoice_details(invoice_id,organization_id,branch_id,invoice_number,invoice_date,due_date,payment_terms,provider_name,provider_address,provider_email,provider_phone,company_number,vat_number,customer_name,customer_address,funder_name,remittance_details,currency,net_amount,vat_rate,vat_amount,gross_amount,vat_exemption_reason,logo_content_type,logo_bytes,logo_sha256,care_recipient_id,care_recipient_name,payer_id)
                select @replacement,organization_id,branch_id,@number,@day,@due,@terms,provider_name,provider_address,provider_email,provider_phone,company_number,vat_number,customer_name,customer_address,funder_name,remittance_details,currency,@net,0,@tax,@gross,'See approved correction line VAT snapshots',logo_content_type,logo_bytes,logo_sha256,care_recipient_id,care_recipient_name,payer_id
                from finance_invoice_details where invoice_id=@original and organization_id=@org
                """, c => { Add(c, "replacement", replacement); Add(c, "original", originalId); Add(c, "number", number); Add(c, "day", day);
                    Add(c, "due", day.AddDays(row.GetProperty("payment_terms_days").GetInt32())); Add(c, "terms", $"Payment due within {row.GetProperty("payment_terms_days").GetInt32()} days");
                    Add(c, "net", net); Add(c, "tax", tax); Add(c, "gross", gross); }, t);
        }
        await Exec("""
            update finance_invoice_corrections set status=@status,reviewed_by=@actor,reviewed_at=now(),review_reason=@reason,
              authority_id=@authority,replacement_invoice_id=@replacement where id=@id and organization_id=@org
            """, c => { Add(c, "id", id); Add(c, "status", status); Add(c, "reason", r.Reason.Trim());
                Nullable(c, "authority", authority, System.Data.DbType.Guid); Nullable(c, "replacement", replacement, System.Data.DbType.Guid); }, t);
        if (replacement is Guid replacementId) await Exec("""
            insert into finance_invoice_lines(id,invoice_id,service_user_id,organization_id,branch_id,description,quantity,unit_rate,amount,funding_source,correction_line_id)
            select gen_random_uuid(),@replacement,d.care_recipient_id,c.organization_id,c.branch_id,l.description,l.quantity,l.unit_rate,l.net_amount,d.funder_name,l.id
            from finance_invoice_correction_lines l join finance_invoice_corrections c on c.id=l.correction_id
              join finance_invoice_details d on d.invoice_id=@replacement where c.id=@id and c.organization_id=@org
            """, c => { Add(c, "replacement", replacementId); Add(c, "id", id); }, t);
        await Audit("INVOICE_SOURCE_CORRECTION_" + status.ToUpperInvariant(), id, branch, t);
        return Ok(new { id, replacementInvoiceId = replacement, status, originalCashTransferred = false });
    }, t);
}

public sealed record InvoiceCorrectionLineRequest(Guid OriginalLineId, DateOnly ServiceDate, string ServiceType, string Description,
    decimal Quantity, decimal UnitRate, string RoundingMode);
public sealed record InvoiceSourceCorrectionRequest(Guid RequestId, Guid OriginalInvoiceId, DateOnly InvoiceDate, int PaymentTermsDays,
    string SourceCorrectionReference, string Reason, InvoiceCorrectionLineRequest[] Lines);
