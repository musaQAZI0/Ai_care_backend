using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace AiCare.Api;

public sealed partial class FinancialAdjustmentsController
{
    [HttpGet("credit-service-attributions")]
    public async Task<IActionResult> CreditServiceAttributions(CancellationToken t) => Ok(await Rows("""
        select a.*,coalesce((select jsonb_agg(to_jsonb(l)) from finance_credit_service_lines l where l.attribution_id=a.id),'[]'::jsonb) lines
        from finance_credit_service_attributions a where a.organization_id=@org and (@wide or a.branch_id=@branch) order by a.created_at desc
        """, _ => { }, t));

    [HttpPost("credit-service-attributions")]
    public Task<IActionResult> CreateCreditServiceAttribution(CreditServiceAttributionRequest r, CancellationToken t) => Tx(async () =>
    {
        if (user.UserId is null) return Unauthorized();
        if (r.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(r.Reference) || string.IsNullOrWhiteSpace(r.Reason) ||
            r.Lines is null || r.Lines.Length is < 1 or > 500 || r.Lines.Select(x => x.InvoiceLineId).Distinct().Count() != r.Lines.Length ||
            r.Lines.Any(x => x.ServiceDate == default || x.NetAmount < 0 || x.TaxAmount < 0 || x.NetAmount + x.TaxAmount <= 0 ||
                decimal.Round(x.NetAmount, 2) != x.NetAmount || decimal.Round(x.TaxAmount, 2) != x.TaxAmount))
            return BadRequest(new { message = "Distinct original lines, exact net/tax allocations, service dates, reference and reason are required." });
        var credits = await Rows("select branch_id from finance_credit_notes where id=@credit and organization_id=@org and (@wide or branch_id=@branch) and status='Issued'", c => Add(c, "credit", r.CreditId), t);
        if (credits.Count != 1) return NotFound();
        var snapshot = JsonSerializer.Serialize(r); var branch = credits[0].GetProperty("branch_id").GetGuid();
        var existing = await Rows("select request_snapshot=cast(@snapshot as jsonb) and created_by=@actor matches from finance_credit_service_attributions where id=@id and organization_id=@org and (@wide or branch_id=@branch)",
            c => { Add(c, "snapshot", snapshot); Add(c, "id", r.RequestId); }, t);
        if (existing.Count != 0) return existing[0].GetProperty("matches").GetBoolean() ? Ok(new { id = r.RequestId, replayed = true }) : Conflict(new { message = "Attribution ID already has different details or creator." });
        await Exec("""
            insert into finance_credit_service_attributions(id,credit_id,organization_id,branch_id,reference,reason,created_by,request_snapshot)
            values(@id,@credit,@org,@target,@reference,@reason,@actor,cast(@snapshot as jsonb))
            """, c => { Add(c, "id", r.RequestId); Add(c, "credit", r.CreditId); Add(c, "target", branch); Add(c, "reference", r.Reference.Trim());
                Add(c, "reason", r.Reason.Trim()); Add(c, "snapshot", snapshot); }, t);
        foreach (var line in r.Lines) await Exec("""
            insert into finance_credit_service_lines(id,attribution_id,invoice_line_id,service_date,net_amount,tax_amount)
            values(@line,@id,@source,@day,@net,@tax)
            """, c => { Add(c, "line", Guid.NewGuid()); Add(c, "id", r.RequestId); Add(c, "source", line.InvoiceLineId);
                Add(c, "day", line.ServiceDate); Add(c, "net", line.NetAmount); Add(c, "tax", line.TaxAmount); }, t);
        await Audit("CREDIT_SERVICE_ATTRIBUTION_SUBMITTED", r.RequestId, branch, t);
        return Created("", new { id = r.RequestId });
    }, t);

    [HttpPost("credit-service-attributions/{id:guid}/approve")]
    public Task<IActionResult> ApproveCreditServiceAttribution(Guid id, CancellationToken t) => Tx(async () =>
    {
        var rows = await Rows("""
            select a.*,c.amount,l.currency from finance_credit_service_attributions a join finance_credit_notes c on c.id=a.credit_id
              join finance_invoice_ledger l on l.invoice_id=c.invoice_id and l.organization_id=c.organization_id
            where a.id=@id and a.organization_id=@org and (@wide or a.branch_id=@branch) for update of a
            """, c => Add(c, "id", id), t);
        if (rows.Count != 1) return NotFound();
        var row = rows[0]; var branch = row.GetProperty("branch_id").GetGuid();
        if (row.GetProperty("approved_by").ValueKind != JsonValueKind.Null)
            return row.GetProperty("approved_by").GetGuid() == user.UserId ? Ok(new { id, replayed = true }) : Conflict(new { message = "Attribution already approved." });
        if (row.GetProperty("created_by").GetGuid() == user.UserId) return Conflict(new { message = "Independent attribution approval required." });
        var grants = await Rows("""
            select id from finance_adjustment_authorities where organization_id=@org and branch_id=@target and reviewer_id=@actor
              and approved_by is not null and currency=@currency and maximum_amount>=@amount
              and (now() at time zone 'Europe/London')::date between valid_from and valid_to order by maximum_amount,id limit 1
            """, c => { Add(c, "target", branch); Add(c, "currency", row.GetProperty("currency").GetString()); Add(c, "amount", row.GetProperty("amount").GetDecimal()); }, t);
        if (grants.Count != 1) return Conflict(new { message = "Approved authority must cover this credit." });
        await Exec("update finance_credit_service_attributions set approved_by=@actor,approved_at=now(),authority_id=@authority where id=@id and organization_id=@org",
            c => { Add(c, "id", id); Add(c, "authority", grants[0].GetProperty("id").GetGuid()); }, t);
        await Audit("CREDIT_SERVICE_ATTRIBUTION_APPROVED", id, branch, t);
        return Ok(new { id, balanceChanged = false });
    }, t);
}

public sealed record CreditServiceLineRequest(Guid InvoiceLineId, DateOnly ServiceDate, decimal NetAmount, decimal TaxAmount);
public sealed record CreditServiceAttributionRequest(Guid RequestId, Guid CreditId, string Reference, string Reason, CreditServiceLineRequest[] Lines);
