using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiCare.Api;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiCare.Tests;

public sealed partial class BillingContractRegressionTests
{
    [Fact]
    public async Task MixedServiceCreditRemainsUnattributedUntilAnIndependentLineAllocationIsApproved()
    {
        var f=await Setup();var contract=await ApprovedContract(f);var claimPolicy=await ExpensePolicy(f);
        var claim=await SubmitExpense(f,await WorkerId(f),claimPolicy,"MIXED-EXPENSE");
        (await f.Reviewer.PostAsJsonAsync($"{Claims}/{claim}/review",new ReviewClaimRequest("Approve","Receipt checked"))).EnsureSuccessStatusCode();
        var policy=new ExpenseRechargePolicyRequest(Guid.NewGuid(),contract,claimPolicy,"Travel expense","MIXED-SERVICE-AUTH",new(2049,4,1),new(2049,4,30),"ClaimAmountAsNet",1,10,10);
        (await f.Author.PostAsJsonAsync(Claims+"/recharge-policies",policy)).EnsureSuccessStatusCode();
        (await f.Reviewer.PostAsync($"{Claims}/recharge-policies/{policy.RequestId}/approve",null)).EnsureSuccessStatusCode();
        var recharge=new ExpenseRechargeRequest(Guid.NewGuid(),claim,policy.RequestId,"Approved expense supply");
        (await f.Author.PostAsJsonAsync(Claims+"/recharges",recharge)).EnsureSuccessStatusCode();
        (await f.Reviewer.PostAsJsonAsync($"{Claims}/recharges/{recharge.RequestId}/review",new ReviewClaimRequest("Approve","Independent recharge review"))).EnsureSuccessStatusCode();
        using(var scope=factory.Services.CreateScope())await ApprovedVatTestData.Ensure(scope.ServiceProvider.GetRequiredService<CareDbContext>(),f.Org,f.Branch,"Travel expense");
        Assert.Equal(1,(await Generate(f)).Generated);var billable=Assert.Single((await f.Author.GetFromJsonAsync<List<Event>>(Events))!);
        (await f.Reviewer.PostAsync($"{Events}/{billable.Id}/approve",null)).EnsureSuccessStatusCode();
        (await f.Author.PostAsJsonAsync("/api/phase1/finance/invoice-runs",new InvoiceRunRequest(new(2049,4,1),new(2049,4,30),f.Funder,$"mixed-{Guid.NewGuid()}"))).EnsureSuccessStatusCode();
        Guid invoice,line;using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();invoice=(await db.Invoices.AsNoTracking().SingleAsync(x=>x.OrganizationId==f.Org)).Id;
            line=await db.Database.SqlQueryRaw<Guid>("select id as \"Value\" from finance_invoice_lines where billable_event_id={0}",billable.Id).SingleAsync();
        }
        foreach(var action in new[]{"review","approve","issue"})(await f.Author.PostAsync($"/api/phase1/finance/invoices/{invoice}/{action}",null)).EnsureSuccessStatusCode();
        await AuthoriseReviewer(f);
        var adjustment=new FinancialAdjustmentRequest(Guid.NewGuid(),invoice,"Credit",5,"Care service correction","MIXED-CREDIT",5,0,new(2049,4,7));
        (await f.Author.PostAsJsonAsync(Adjustments,adjustment)).EnsureSuccessStatusCode();
        (await f.Reviewer.PostAsJsonAsync($"{Adjustments}/{adjustment.RequestId}/review",new AdjustmentReviewRequest("Approve","Credit checked"))).EnsureSuccessStatusCode();
        var reportUrl="/api/phase1/finance/reports/revenue?from=2049-04-07&to=2049-04-07&serviceType=Care";
        var before=await f.Author.GetFromJsonAsync<JsonElement>(reportUrl);
        Assert.Equal("SERVICE_TYPE_ATTRIBUTION_REQUIRED",Assert.Single(before.GetProperty("attributionExceptions").EnumerateArray()).GetProperty("exception").GetString());
        Assert.Equal(0m,Assert.Single(before.GetProperty("groups").EnumerateArray()).GetProperty("credited_net").GetDecimal());
        Guid credit;using(var scope=factory.Services.CreateScope())credit=await scope.ServiceProvider.GetRequiredService<CareDbContext>().Database.SqlQueryRaw<Guid>("select id as \"Value\" from finance_credit_notes where invoice_id={0}",invoice).SingleAsync();
        var allocation=new CreditServiceAttributionRequest(Guid.NewGuid(),credit,"MIXED-ALLOCATION","Evidence identifies the care line",[new(line,new(2049,4,7),5,0)]);
        (await f.Author.PostAsJsonAsync(Adjustments+"/credit-service-attributions",allocation)).EnsureSuccessStatusCode();
        (await f.Reviewer.PostAsync($"{Adjustments}/credit-service-attributions/{allocation.RequestId}/approve",null)).EnsureSuccessStatusCode();
        var after=await f.Author.GetFromJsonAsync<JsonElement>(reportUrl);Assert.Empty(after.GetProperty("attributionExceptions").EnumerateArray());
        Assert.Equal(10m,Assert.Single(after.GetProperty("groups").EnumerateArray()).GetProperty("revenue_net").GetDecimal());
        Assert.Equal(0m,Assert.Single(after.GetProperty("reconciliationCurrencyTotals").EnumerateArray()).GetProperty("credit_gross_difference").GetDecimal());
    }

    [Fact]
    public async Task CreditServiceAllocationConservesNetAndTaxWithoutChangingCashOrDoubleCountingRevenue()
    {
        var f = await Setup(false);
        var invoice = await IssuedFinanceInvoice(f);
        await AuthoriseReviewer(f);
        var credit = await FullCredit(f, invoice);
        var line = await OriginalLine(invoice);
        var request = new CreditServiceAttributionRequest(Guid.NewGuid(),credit,"SERVICE-ALLOCATION","Reviewed original service",[new(line,new(2049,4,7),14,3)]);
        (await f.Author.PostAsJsonAsync(Adjustments + "/credit-service-attributions",request)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict,(await f.Reviewer.PostAsync($"{Adjustments}/credit-service-attributions/{request.RequestId}/approve",null)).StatusCode);
        // Draft allocations are append-only. Submit a separate, conserving attribution.
        var valid = request with { RequestId=Guid.NewGuid(), Lines=[new(line,new(2049,4,7),15,3)] };
        Assert.Equal(HttpStatusCode.BadRequest,(await f.Author.PostAsJsonAsync(Adjustments + "/credit-service-attributions",valid with { Lines=[valid.Lines[0],valid.Lines[0]] })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await f.Author.PostAsJsonAsync(Adjustments + "/credit-service-attributions",valid with { RequestId=Guid.NewGuid(),Lines=[valid.Lines[0] with { InvoiceLineId=Guid.NewGuid() }] })).StatusCode);
        (await f.Author.PostAsJsonAsync(Adjustments + "/credit-service-attributions",valid)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK,(await f.Author.PostAsJsonAsync(Adjustments + "/credit-service-attributions",valid)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await f.Author.PostAsJsonAsync(Adjustments + "/credit-service-attributions",valid with { Reason="Changed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await f.Author.PostAsync($"{Adjustments}/credit-service-attributions/{valid.RequestId}/approve",null)).StatusCode);
        var unauthorisedReviewer=await AddActor(f,UserRole.BackOffice);
        Assert.Equal(HttpStatusCode.Conflict,(await unauthorisedReviewer.PostAsync($"{Adjustments}/credit-service-attributions/{valid.RequestId}/approve",null)).StatusCode);
        var approvals = await Task.WhenAll(f.Reviewer.PostAsync($"{Adjustments}/credit-service-attributions/{valid.RequestId}/approve",null),f.Reviewer.PostAsync($"{Adjustments}/credit-service-attributions/{valid.RequestId}/approve",null));
        Assert.All(approvals,x=>x.EnsureSuccessStatusCode());
        var competing = valid with { RequestId=Guid.NewGuid() };
        (await f.Author.PostAsJsonAsync(Adjustments + "/credit-service-attributions",competing)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict,(await f.Reviewer.PostAsync($"{Adjustments}/credit-service-attributions/{competing.RequestId}/approve",null)).StatusCode);
        var foreign=await Setup();
        Assert.Equal(HttpStatusCode.NotFound,(await foreign.Author.PostAsJsonAsync(Adjustments + "/credit-service-attributions",valid with { RequestId=Guid.NewGuid() })).StatusCode);
        using var scope=factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal(1,await db.Database.SqlQueryRaw<int>("select count(*)::int as \"Value\" from finance_revenue_sources where source_id={0} and record_kind='CreditAllocation'",credit).SingleAsync());
        Assert.Equal(0,await db.Database.SqlQueryRaw<int>("select count(*)::int as \"Value\" from finance_revenue_sources where source_id={0} and record_kind='Credit'",credit).SingleAsync());
        Assert.Equal(0m,await db.Database.SqlQueryRaw<decimal>("select sum(net_amount) as \"Value\" from finance_revenue_sources where invoice_id={0}",invoice).SingleAsync());
        Assert.Equal(18m,await db.Database.SqlQueryRaw<decimal>("select credits as \"Value\" from finance_invoice_ledger where invoice_id={0}",invoice).SingleAsync());
        await Assert.ThrowsAnyAsync<Exception>(()=>db.Database.ExecuteSqlRawAsync("update finance_credit_service_lines set net_amount=1 where attribution_id={0}",valid.RequestId));
        await Assert.ThrowsAnyAsync<Exception>(()=>db.Database.ExecuteSqlRawAsync("delete from finance_credit_service_attributions where id={0}",valid.RequestId));
    }

    [Fact]
    public async Task LinkedCorrectionRollsBackFailuresAndCreatesOneDraftWithoutTransferringOriginalCash()
    {
        var f=await Setup(false);var invoice=await IssuedFinanceInvoice(f);var line=await OriginalLine(invoice);
        await AuthoriseReviewer(f);
        (await f.Author.PostAsJsonAsync($"/api/phase1/finance/invoices/{invoice}/payments",new{amount=5m,reference="ORIGINAL-CASH"})).EnsureSuccessStatusCode();
        var request=new InvoiceSourceCorrectionRequest(Guid.NewGuid(),invoice,new(2049,4,8),30,"CORRECTION-AUTHORITY","Corrected service quantity",[new(line,new(2049,4,7),"Missing VAT policy","Corrected care",0.5m,20,"AwayFromZero")]);
        Assert.Equal(HttpStatusCode.Conflict,(await f.Author.PostAsJsonAsync(Adjustments+"/invoice-corrections",request)).StatusCode);
        using(var scope=factory.Services.CreateScope())
            Assert.Equal(0,await scope.ServiceProvider.GetRequiredService<CareDbContext>().Database.SqlQueryRaw<int>("select count(*)::int as \"Value\" from finance_invoice_corrections where id={0}",request.RequestId).SingleAsync());
        request=request with { Lines=[request.Lines[0] with { ServiceType="Care" }] };
        (await f.Author.PostAsJsonAsync(Adjustments+"/invoice-corrections",request)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK,(await f.Author.PostAsJsonAsync(Adjustments+"/invoice-corrections",request)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await f.Author.PostAsJsonAsync(Adjustments+"/invoice-corrections",request with { Reason="Different" })).StatusCode);
        var review=new AdjustmentReviewRequest("Approve","Independent source correction approval");
        Assert.Equal(HttpStatusCode.Conflict,(await f.Author.PostAsJsonAsync($"{Adjustments}/invoice-corrections/{request.RequestId}/review",review)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await f.Reviewer.PostAsJsonAsync($"{Adjustments}/invoice-corrections/{request.RequestId}/review",review)).StatusCode);
        using(var scope=factory.Services.CreateScope())
            Assert.Equal(1,await scope.ServiceProvider.GetRequiredService<CareDbContext>().Invoices.CountAsync(x=>x.OrganizationId==f.Org));
        await FullCredit(f,invoice);
        var approvals=await Task.WhenAll(f.Reviewer.PostAsJsonAsync($"{Adjustments}/invoice-corrections/{request.RequestId}/review",review),f.Reviewer.PostAsJsonAsync($"{Adjustments}/invoice-corrections/{request.RequestId}/review",review));
        Assert.All(approvals,x=>x.EnsureSuccessStatusCode());
        using var verify=factory.Services.CreateScope();var db=verify.ServiceProvider.GetRequiredService<CareDbContext>();
        var replacement=await db.Database.SqlQueryRaw<Guid>("select replacement_invoice_id as \"Value\" from finance_invoice_corrections where id={0}",request.RequestId).SingleAsync();
        var draft=await db.Invoices.AsNoTracking().SingleAsync(x=>x.Id==replacement);Assert.Equal("Generated",draft.Status);Assert.Equal(12m,draft.Amount);
        Assert.Equal(2,await db.Invoices.CountAsync(x=>x.OrganizationId==f.Org));
        Assert.Equal(5m,await db.Database.SqlQueryRaw<decimal>("select net_cash as \"Value\" from finance_invoice_ledger where invoice_id={0}",invoice).SingleAsync());
        Assert.Equal(0m,await db.Database.SqlQueryRaw<decimal>("select net_cash as \"Value\" from finance_invoice_ledger where invoice_id={0}",replacement).SingleAsync());
        Assert.Equal(1,await db.Database.SqlQueryRaw<int>("select count(*)::int as \"Value\" from finance_invoice_lines where invoice_id={0} and correction_line_id is not null and billable_event_id is null",replacement).SingleAsync());
        Assert.Equal(0,await db.Database.SqlQueryRaw<int>("select count(*)::int as \"Value\" from finance_revenue_sources where invoice_id={0}",replacement).SingleAsync());
        await Assert.ThrowsAnyAsync<Exception>(()=>db.Database.ExecuteSqlRawAsync("update finance_invoice_correction_lines set quantity=2 where correction_id={0}",request.RequestId));
    }

    [Fact]
    public async Task RevenueReportUsesServiceDatesFiltersPaginationAndCurrencyReconciliation()
    {
        var f=await Setup(false);var invoice=await IssuedFinanceInvoice(f);await AuthoriseReviewer(f);await FullCredit(f,invoice);
        var root=$"/api/phase1/finance/reports/revenue?from=2049-04-07&to=2049-04-07&funderId={f.Funder}&serviceType=Care&currency=gbp&pageSize=1";
        var report=await f.Author.GetFromJsonAsync<JsonElement>(root);
        var group=Assert.Single(report.GetProperty("groups").EnumerateArray());
        Assert.Equal(15m,group.GetProperty("billed_net").GetDecimal());Assert.Equal(15m,group.GetProperty("credited_net").GetDecimal());Assert.Equal(0m,group.GetProperty("revenue_net").GetDecimal());
        var first=Assert.Single(report.GetProperty("sources").EnumerateArray());Assert.Equal(invoice,first.GetProperty("invoice_id").GetGuid());Assert.Equal(2,first.GetProperty("source_count").GetInt32());
        var second=await f.Author.GetFromJsonAsync<JsonElement>(root+"&page=2");
        Assert.NotEqual(first.GetProperty("record_kind").GetString(),Assert.Single(second.GetProperty("sources").EnumerateArray()).GetProperty("record_kind").GetString());
        Assert.Empty(report.GetProperty("attributionExceptions").EnumerateArray());
        var reconciliation=Assert.Single(report.GetProperty("reconciliationCurrencyTotals").EnumerateArray());
        Assert.Equal(0m,reconciliation.GetProperty("invoice_net_difference").GetDecimal());Assert.Equal(0m,reconciliation.GetProperty("invoice_tax_difference").GetDecimal());Assert.Equal(0m,reconciliation.GetProperty("credit_gross_difference").GetDecimal());
        foreach(var filter in new[]{"currency=USD","serviceType=Other","from=2049-04-08&to=2049-04-08"})
        {
            var query=filter.StartsWith("from=")?filter:"from=2049-04-07&to=2049-04-07&"+filter;
            var empty=await f.Author.GetFromJsonAsync<JsonElement>("/api/phase1/finance/reports/revenue?"+query);
            Assert.Empty(empty.GetProperty("groups").EnumerateArray());Assert.Empty(empty.GetProperty("sources").EnumerateArray());
        }
        Assert.Equal(HttpStatusCode.Forbidden,(await f.Author.GetAsync(root+$"&branchId={Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await f.Author.GetAsync(root+"&page=0")).StatusCode);
        var foreign=await Setup();var scoped=await foreign.Author.GetFromJsonAsync<JsonElement>(root);Assert.Empty(scoped.GetProperty("sources").EnumerateArray());
        var staff=await Login(f.WorkerName);Assert.Equal(HttpStatusCode.Forbidden,(await staff.GetAsync(root)).StatusCode);
    }

    private async Task<Guid> IssuedFinanceInvoice(Fixture f)
    {
        using(var scope=factory.Services.CreateScope())await ApprovedVatTestData.Ensure(scope.ServiceProvider.GetRequiredService<CareDbContext>(),f.Org,f.Branch,"Care",20);
        await ApprovedContract(f);Assert.Equal(1,(await Generate(f)).Generated);
        var billable=Assert.Single((await f.Author.GetFromJsonAsync<List<Event>>(Events))!);
        (await f.Reviewer.PostAsync($"{Events}/{billable.Id}/approve",null)).EnsureSuccessStatusCode();
        (await f.Author.PostAsJsonAsync("/api/phase1/finance/invoice-runs",new InvoiceRunRequest(new(2049,4,1),new(2049,4,30),f.Funder,$"correction-{Guid.NewGuid()}"))).EnsureSuccessStatusCode();
        Guid invoice;using(var scope=factory.Services.CreateScope())invoice=(await scope.ServiceProvider.GetRequiredService<CareDbContext>().Invoices.AsNoTracking().SingleAsync(x=>x.OrganizationId==f.Org)).Id;
        foreach(var action in new[]{"review","approve","issue"})(await f.Author.PostAsync($"/api/phase1/finance/invoices/{invoice}/{action}",null)).EnsureSuccessStatusCode();
        return invoice;
    }
    private async Task AuthoriseReviewer(Fixture f)
    {
        Guid reviewer;using(var scope=factory.Services.CreateScope())reviewer=(await scope.ServiceProvider.GetRequiredService<CareDbContext>().AppUsers.SingleAsync(x=>x.UserName==f.WorkerName.Replace(".worker",".reviewer"))).Id;
        var authority=new AdjustmentAuthorityRequest(Guid.NewGuid(),f.Branch,reviewer,"GBP",100,new(2026,1,1),new(2099,12,31),"SYNTHETIC-FINANCE-AUTHORITY");
        (await f.Author.PostAsJsonAsync(Adjustments+"/authorities",authority)).EnsureSuccessStatusCode();
        var grantor=await AddActor(f,UserRole.BackOffice);(await grantor.PostAsync($"{Adjustments}/authorities/{authority.RequestId}/approve",null)).EnsureSuccessStatusCode();
    }
    private async Task<Guid> FullCredit(Fixture f,Guid invoice)
    {
        var request=new FinancialAdjustmentRequest(Guid.NewGuid(),invoice,"Credit",18,"Full correction credit","FULL-CREDIT",15,3,new(2049,4,7));
        (await f.Author.PostAsJsonAsync(Adjustments,request)).EnsureSuccessStatusCode();
        (await f.Reviewer.PostAsJsonAsync($"{Adjustments}/{request.RequestId}/review",new AdjustmentReviewRequest("Approve","Independent full credit"))).EnsureSuccessStatusCode();
        using var scope=factory.Services.CreateScope();return await scope.ServiceProvider.GetRequiredService<CareDbContext>().Database.SqlQueryRaw<Guid>("select id as \"Value\" from finance_credit_notes where invoice_id={0}",invoice).SingleAsync();
    }
    private async Task<Guid> OriginalLine(Guid invoice)
    {
        using var scope=factory.Services.CreateScope();return await scope.ServiceProvider.GetRequiredService<CareDbContext>().Database.SqlQueryRaw<Guid>("select id as \"Value\" from finance_invoice_lines where invoice_id={0}",invoice).SingleAsync();
    }
}
