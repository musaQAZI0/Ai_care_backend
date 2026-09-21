using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using System.Data.Common;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[ApiController]
[Authorize(Roles="Administrator,BackOffice")]
[Route("api/phase1/finance")]
public sealed class FinanceGovernanceController(CareDbContext db,ITenantContext tenant,ICurrentUserContext user):ControllerBase
{
    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard(CancellationToken t)=>Ok(new{
        invoiceCount=await Scalar("select count(*) from \"Invoices\" where \"OrganizationId\"=@organization",t),
        generatedInvoices=await Scalar("select count(*) from \"Invoices\" where \"OrganizationId\"=@organization and \"Status\"='Generated'",t),
        approvedInvoices=await Scalar("select count(*) from \"Invoices\" where \"OrganizationId\"=@organization and \"Status\"='Approved'",t),
        paidInvoices=await Scalar("select count(*) from \"Invoices\" where \"OrganizationId\"=@organization and \"Status\"='Paid'",t),
        invoiceTotal=await Money("select coalesce(sum(\"Amount\"),0) from \"Invoices\" where \"OrganizationId\"=@organization and \"Status\"<>'Void'",t),
        paymentsReceived=await Money("select coalesce(sum(amount),0) from finance_payments where organization_id=@organization",t),
        openReconciliations=await Scalar("select count(*) from finance_funding_reconciliations where organization_id=@organization and status='Open'",t)});

    [HttpPost("invoice-batches")]
    public Task<IActionResult> GenerateInvoiceBatch(GenerateFinanceBatchRequest request,CancellationToken t) => InTransaction(()=>GenerateInvoiceBatchCore(request,t),t);
    private async Task<IActionResult> GenerateInvoiceBatchCore(GenerateFinanceBatchRequest request,CancellationToken t)
    {
        if(request.PeriodStart>=request.PeriodEnd)return BadRequest(new{message="Period start must be before period end."});
        if(request.DefaultHourlyRate<0)return BadRequest(new{message="Rates must not be negative."});
        var visits=await db.Visits.AsNoTracking().Where(x=>x.OrganizationId==tenant.OrganizationId&&(tenant.IsOrganizationWide||x.BranchId==tenant.BranchId)&&x.Status==VisitStatus.Completed&&x.StartsAt>=request.PeriodStart&&x.StartsAt<request.PeriodEnd).ToListAsync(t);
        var claimed=await Query("select visit_id from finance_invoice_lines where organization_id=@organization and visit_id is not null",_=>{},r=>r.GetGuid(0),t);visits=visits.Where(v=>!claimed.Contains(v.Id)).ToList();
        if(visits.Count==0)return Conflict(new{message="No unbilled completed visits are available."});
        var profile=await BillingProfile(t);var created=new List<object>();
        foreach(var group in visits.GroupBy(x=>x.ServiceUserId))
        {
            var person=await db.ServiceUsers.AsNoTracking().SingleAsync(x=>x.Id==group.Key&&x.OrganizationId==tenant.OrganizationId,t);var funding=await Funding(group.Key,t);var rate=funding?.HourlyRate>0?funding.HourlyRate:request.DefaultHourlyRate;
            var net=group.Sum(v=>Math.Round(Math.Round(v.DurationMinutes/60m,2)*rate,2));var vat=Math.Round(net*profile.VatRate/100m,2);var gross=net+vat;var invoiceDate=DateOnly.FromDateTime(DateTime.UtcNow);var due=invoiceDate.AddDays(profile.TermsDays);var number=await NextInvoiceNumber(invoiceDate.Year,t);
            var invoice=new Invoice(Guid.NewGuid(),group.Key,funding?.FundingSource??person.FundingSource,gross,"Generated",DateTimeOffset.UtcNow,tenant.OrganizationId,tenant.BranchId??TenantDefaults.BranchId);db.Invoices.Add(invoice);await db.SaveChangesAsync(t);
            await Exec("insert into finance_invoice_details(invoice_id,organization_id,branch_id,invoice_number,invoice_date,due_date,payment_terms,provider_name,provider_address,provider_email,provider_phone,company_number,vat_number,customer_name,customer_address,funder_name,remittance_details,net_amount,vat_rate,vat_amount,gross_amount,vat_exemption_reason) values(@invoice,@organization,@branch,@number,@invoiceDate,@due,@terms,@provider,@providerAddress,@providerEmail,@providerPhone,@company,@vatNumber,@customer,@customerAddress,@funder,@remittance,@net,@vatRate,@vat,@gross,@exemption)",c=>{Add(c,"invoice",invoice.Id);Add(c,"number",number);Add(c,"invoiceDate",invoiceDate);Add(c,"due",due);Add(c,"terms",$"Payment due within {profile.TermsDays} days");Add(c,"provider",profile.ProviderName);Add(c,"providerAddress",profile.ProviderAddress);Add(c,"providerEmail",profile.ProviderEmail);Add(c,"providerPhone",profile.ProviderPhone);Add(c,"company",profile.CompanyNumber);Add(c,"vatNumber",profile.VatNumber);Add(c,"customer",person.FullName);Add(c,"customerAddress",person.Address);Add(c,"funder",funding?.FunderName??invoice.Funder);Add(c,"remittance",profile.Remittance);Add(c,"net",net);Add(c,"vatRate",profile.VatRate);Add(c,"vat",vat);Add(c,"gross",gross);Add(c,"exemption",profile.Exemption);},t);
            foreach(var visit in group)await Exec("insert into finance_invoice_lines(id,invoice_id,visit_id,service_user_id,organization_id,branch_id,description,quantity,unit_rate,amount,funding_source) values(@id,@invoice,@visit,@person,@organization,@branch,@description,@quantity,@rate,@amount,@funding)",c=>{var q=Math.Round(visit.DurationMinutes/60m,2);Add(c,"id",Guid.NewGuid());Add(c,"invoice",invoice.Id);Add(c,"visit",visit.Id);Add(c,"person",group.Key);Add(c,"description",visit.VisitType);Add(c,"quantity",q);Add(c,"rate",rate);Add(c,"amount",Math.Round(q*rate,2));Add(c,"funding",invoice.Funder);},t);
            created.Add(new{id=invoice.Id,serviceUserId=invoice.ServiceUserId,invoiceNumber=number,netAmount=net,vatAmount=vat,amount=gross,dueDate=due,status=invoice.Status});
        }
        Audit("finance.invoice_batch_generated","Invoice",null);await db.SaveChangesAsync(t);return Created("/api/phase1/finance/invoice-batches",new{count=created.Count,invoices=created});
    }
    [HttpGet("invoices/{id:guid}/lines")]
    public async Task<IActionResult> InvoiceLines(Guid id,CancellationToken t)
    {
        var visible=await db.Invoices.AsNoTracking().AnyAsync(x=>x.Id==id&&x.OrganizationId==tenant.OrganizationId&&(tenant.IsOrganizationWide||x.BranchId==tenant.BranchId),t);
        if(!visible)return NotFound();
        var lines=await Query("select id,invoice_id,visit_id,service_user_id,description,quantity,unit_rate,amount,funding_source,created_at from finance_invoice_lines where invoice_id=@invoice and organization_id=@organization order by created_at",c=>Add(c,"invoice",id),r=>new InvoiceLineResponse(r.GetGuid(0),r.GetGuid(1),r.IsDBNull(2)?null:r.GetGuid(2),r.GetGuid(3),r.GetString(4),r.GetDecimal(5),r.GetDecimal(6),r.GetDecimal(7),r.GetString(8),r.GetFieldValue<DateTimeOffset>(9)),t);
        return Ok(lines);
    }

    [HttpPost("funding-reconciliations")]
    public Task<IActionResult> ReconcileFunding(FundingReconciliationRequest request,CancellationToken t) => InTransaction(()=>ReconcileFundingCore(request,t),t);
    private async Task<IActionResult> ReconcileFundingCore(FundingReconciliationRequest request,CancellationToken t){if(request.ServiceUserId==Guid.Empty||request.PeriodStart>=request.PeriodEnd)return BadRequest(new{message="Service user and valid period are required."});var person=await db.ServiceUsers.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==request.ServiceUserId&&x.OrganizationId==tenant.OrganizationId,t);if(person is null)return NotFound();var funding=await Funding(request.ServiceUserId,t);var delivered=await Money("select coalesce(sum(\"DurationMinutes\"),0)/60.0 from \"Visits\" where \"ServiceUserId\"=@person and \"OrganizationId\"=@organization and \"Status\"='Completed' and \"StartsAt\">=@start and \"StartsAt\"<@end",t,c=>{Add(c,"person",request.ServiceUserId);Add(c,"start",request.PeriodStart);Add(c,"end",request.PeriodEnd);});var invoiced=await Money("select coalesce(sum(\"Amount\"),0) from \"Invoices\" where \"ServiceUserId\"=@person and \"OrganizationId\"=@organization and \"IssuedAt\">=@start and \"IssuedAt\"<@end and \"Status\"<>'Void'",t,c=>{Add(c,"person",request.ServiceUserId);Add(c,"start",request.PeriodStart);Add(c,"end",request.PeriodEnd);});var authorized=Math.Round((funding?.AuthorizedHoursPerWeek??0)*(decimal)(request.PeriodEnd-request.PeriodStart).TotalDays/7m,2);var variance=Math.Round(delivered-authorized,2);var status=Math.Abs(variance)>request.AllowedVarianceHours?"Exception":"Matched";var id=Guid.NewGuid();await Exec("insert into finance_funding_reconciliations(id,service_user_id,organization_id,branch_id,period_start,period_end,authorized_hours,delivered_hours,invoiced_amount,variance_hours,status,notes,created_by) values(@id,@person,@organization,@branch,@start,@end,@authorized,@delivered,@invoiced,@variance,@status,@notes,@actor)",c=>{Add(c,"id",id);Add(c,"person",request.ServiceUserId);Add(c,"start",request.PeriodStart);Add(c,"end",request.PeriodEnd);Add(c,"authorized",authorized);Add(c,"delivered",delivered);Add(c,"invoiced",invoiced);Add(c,"variance",variance);Add(c,"status",status);Add(c,"notes",request.Notes??"");},t);Audit("finance.funding_reconciled","FundingReconciliation",id);await db.SaveChangesAsync(t);return Created($"/api/phase1/finance/funding-reconciliations/{id}",new{id,status,authorizedHours=authorized,deliveredHours=delivered,invoicedAmount=invoiced,varianceHours=variance});}

    private async Task<IActionResult> InTransaction(Func<Task<IActionResult>> operation,CancellationToken t)
    {
        try { return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () => {
            await using var tx=await db.Database.BeginTransactionAsync(t);
            // Serialize source claims and payments for this tenant, including overlapping periods.
            await Exec("select pg_advisory_xact_lock(hashtextextended(@key,0))",c=>Add(c,"key",$"finance:{tenant.OrganizationId}"),t);
            var result=await operation();
            await tx.CommitAsync(t);
            return result;
        }); }
        catch(PostgresException ex) when(ex.SqlState==PostgresErrorCodes.UniqueViolation && ex.ConstraintName is "ux_finance_invoice_visit" or "ux_finance_payroll_visit")
        { db.ChangeTracker.Clear(); return Conflict(new{message="A visit has already been claimed by a finance batch."}); }
    }

    private async Task<BillingProfile> BillingProfile(CancellationToken t)
    {
        var rows=await Query("select provider_name,provider_address,provider_email,provider_phone,company_number,vat_number,remittance_details,default_payment_terms_days,default_vat_rate,vat_exemption_reason from finance_invoice_profiles where organization_id=@organization and branch_id=@branch",_=>{},r=>new BillingProfile(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetInt32(7),r.GetDecimal(8),r.GetString(9)),t);if(rows.Count>0)return rows[0];
        var organization=await db.Organizations.AsNoTracking().SingleAsync(x=>x.Id==tenant.OrganizationId,t);return new BillingProfile(organization.Name,"","","","","","",30,0,"VAT not charged");
    }
    private async Task<string> NextInvoiceNumber(int year,CancellationToken t){await using var c=await Command("insert into finance_invoice_sequences(organization_id,invoice_year,next_value) values(@organization,@year,2) on conflict(organization_id,invoice_year) do update set next_value=finance_invoice_sequences.next_value+1 returning next_value-1",t);Add(c,"year",year);var value=Convert.ToInt64(await c.ExecuteScalarAsync(t));return $"INV-{year}-{value:000000}";}
    private async Task<Funding?> Funding(Guid person,CancellationToken t){var rows=await Query("select funding_source,funder_name,authorized_hours_per_week,hourly_rate from funding_arrangements where service_user_id=@person and organization_id=@organization and status='Active' order by valid_from desc limit 1",c=>Add(c,"person",person),r=>new Funding(r.GetString(0),r.GetString(1),r.GetDecimal(2),r.GetDecimal(3)),t);return rows.FirstOrDefault();}
    private async Task<int> Scalar(string sql,CancellationToken t,Action<DbCommand>? bind=null){await using var c=await Command(sql,t);bind?.Invoke(c);return Convert.ToInt32(await c.ExecuteScalarAsync(t));}
    private async Task<decimal> Money(string sql,CancellationToken t,Action<DbCommand>? bind=null){await using var c=await Command(sql,t);bind?.Invoke(c);return Convert.ToDecimal(await c.ExecuteScalarAsync(t));}
    private async Task<int> Exec(string sql,Action<DbCommand> bind,CancellationToken t){await using var c=await Command(sql,t);bind(c);return await c.ExecuteNonQueryAsync(t);}private async Task<List<T>> Query<T>(string sql,Action<DbCommand> bind,Func<DbDataReader,T> map,CancellationToken t){var a=new List<T>();await using var c=await Command(sql,t);bind(c);await using var r=await c.ExecuteReaderAsync(t);while(await r.ReadAsync(t))a.Add(map(r));return a;}
    private async Task<DbCommand> Command(string sql,CancellationToken t){var cn=db.Database.GetDbConnection();if(cn.State!=System.Data.ConnectionState.Open)await cn.OpenAsync(t);var c=cn.CreateCommand();c.Transaction=db.Database.CurrentTransaction?.GetDbTransaction();c.CommandText=sql;Add(c,"organization",tenant.OrganizationId);Add(c,"branch",tenant.BranchId??TenantDefaults.BranchId);Add(c,"actor",user.UserName);return c;}private static void Add(DbCommand c,string n,object? v){if(c.Parameters.Contains(n))return;var p=c.CreateParameter();p.ParameterName=n;p.Value=v??DBNull.Value;c.Parameters.Add(p);}private void Audit(string action,string entity,Guid? id)=>db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),action,user.UserName,entity,id,DateTimeOffset.UtcNow,tenant.OrganizationId,tenant.BranchId??TenantDefaults.BranchId));
}
public sealed record GenerateFinanceBatchRequest(DateTimeOffset PeriodStart,DateTimeOffset PeriodEnd,decimal DefaultHourlyRate,decimal MileageRate);
public sealed record RecordFinancePaymentRequest(decimal Amount,string Reference,DateTimeOffset? ReceivedAt);
public sealed record FundingReconciliationRequest(Guid ServiceUserId,DateTimeOffset PeriodStart,DateTimeOffset PeriodEnd,decimal AllowedVarianceHours,string? Notes);
public sealed record Funding(string FundingSource,string FunderName,decimal AuthorizedHoursPerWeek,decimal HourlyRate);
public sealed record BillingProfile(string ProviderName,string ProviderAddress,string ProviderEmail,string ProviderPhone,string CompanyNumber,string VatNumber,string Remittance,int TermsDays,decimal VatRate,string Exemption);
public sealed record InvoiceLineResponse(Guid Id,Guid InvoiceId,Guid? VisitId,Guid ServiceUserId,string Description,decimal Quantity,decimal UnitRate,decimal Amount,string FundingSource,DateTimeOffset CreatedAt);

