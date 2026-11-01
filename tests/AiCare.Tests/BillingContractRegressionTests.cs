using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AiCare.Api;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed partial class BillingContractRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]public async Task MissingVatBlocksTheWholeRunAndApprovedTreatmentSnapshotsReconcile()
    {
        var f=await Setup(false);var request=Request(f);(await f.Author.PostAsJsonAsync(Contracts,request)).EnsureSuccessStatusCode();(await f.Reviewer.PostAsJsonAsync($"{Contracts}/{request.RequestId}/approve",new{reason="Approved synthetic contract"})).EnsureSuccessStatusCode();await Generate(f);var row=Assert.Single((await f.Author.GetFromJsonAsync<List<Event>>(Events))!);(await f.Reviewer.PostAsync($"{Events}/{row.Id}/approve",null)).EnsureSuccessStatusCode();
        var run=new{periodStart="2049-04-01",periodEnd="2049-04-30",funderId=f.Funder,idempotencyKey=$"vat-{Guid.NewGuid()}"};Assert.Equal(HttpStatusCode.Conflict,(await f.Author.PostAsJsonAsync("/api/phase1/finance/invoice-runs",run)).StatusCode);
        using(var scope=factory.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();Assert.Empty(await db.Invoices.Where(x=>x.OrganizationId==f.Org).ToListAsync());await ApprovedVatTestData.Ensure(db,f.Org,f.Branch,"Care",20);}
        (await f.Author.PostAsJsonAsync("/api/phase1/finance/invoice-runs",run)).EnsureSuccessStatusCode();
        using var verify=factory.Services.CreateScope();var verifyDb=verify.ServiceProvider.GetRequiredService<CareDbContext>();var line=await verifyDb.Database.SqlQueryRaw<VatSnapshot>("select vat_treatment as \"Treatment\",vat_rate as \"Rate\",vat_amount as \"Tax\" from finance_invoice_lines where billable_event_id={0}",row.Id).SingleAsync();Assert.Equal("Taxable",line.Treatment);Assert.Equal(20,line.Rate);Assert.Equal(3,line.Tax);Assert.Equal(18,(await verifyDb.Invoices.SingleAsync(x=>x.OrganizationId==f.Org)).Amount);
        await Assert.ThrowsAnyAsync<Exception>(()=>verifyDb.Database.ExecuteSqlRawAsync("update finance_invoice_lines set vat_amount=0 where billable_event_id={0}",row.Id));await Assert.ThrowsAnyAsync<Exception>(()=>verifyDb.Database.ExecuteSqlRawAsync("update finance_vat_treatments set rate=0 where organization_id={0}",f.Org));
    }
    private sealed record VatSnapshot(string Treatment,decimal Rate,decimal Tax);
    [Fact]public async Task DimensionedRatesRequireExplicitContextAndPreserveItsBilledHistory()
    {
        var f=await Setup();using(var scope=factory.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();await db.Database.ExecuteSqlRawAsync("update finance_rate_rules set staff_level='SENIOR',location_code='ZONE-1',minimum_minutes=30,maximum_minutes=60 where rate_version_id={0}",f.Version);}
        var request=Request(f);(await f.Author.PostAsJsonAsync(Contracts,request)).EnsureSuccessStatusCode();(await f.Reviewer.PostAsJsonAsync($"{Contracts}/{request.RequestId}/approve",new{reason="Reviewed explicit dimensioned rate"})).EnsureSuccessStatusCode();Assert.Equal(0,(await Generate(f)).Generated);
        var context=new VisitBillingContextRequest("SENIOR","ZONE-1","EXPLICIT-STAFF-LOCATION-REFERENCE");var url=$"/api/phase1/finance/billing/visits/{f.Visit}/context";(await f.Author.PutAsJsonAsync(url,context)).EnsureSuccessStatusCode();(await f.Author.PutAsJsonAsync(url,context)).EnsureSuccessStatusCode();Assert.Equal(1,(await Generate(f)).Generated);var row=Assert.Single((await f.Author.GetFromJsonAsync<List<Event>>(Events))!);Assert.Equal(15,row.FinalAmount);(await f.Reviewer.PostAsync($"{Events}/{row.Id}/approve",null)).EnsureSuccessStatusCode();Assert.Equal(HttpStatusCode.Conflict,(await f.Author.PutAsJsonAsync(url,context with{StaffLevel="OTHER"})).StatusCode);
        var foreign=await Setup();Assert.Equal(HttpStatusCode.NotFound,(await foreign.Author.PutAsJsonAsync(url,context)).StatusCode);
    }
    [Theory]
    [InlineData("Actual",15)] [InlineData("Planned",20)]
    public async Task ExplicitPolicyUsesApprovedAttendanceAndIndependentReview(string basis,int expected)
    {
        var f=await Setup(); var request=Request(f,basis);
        (await f.Author.PostAsJsonAsync(Contracts,request)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict,(await f.Author.PostAsJsonAsync($"{Contracts}/{request.RequestId}/approve",new{reason="Self approval"})).StatusCode);
        (await f.Reviewer.PostAsJsonAsync($"{Contracts}/{request.RequestId}/approve",new{reason="Reviewed explicit test policy"})).EnsureSuccessStatusCode();
        Assert.Equal(1,(await Generate(f)).Generated);
        var row=Assert.Single((await f.Author.GetFromJsonAsync<List<Event>>(Events))!);
        Assert.Equal(expected,row.FinalAmount);
        Assert.Equal(0,(await Generate(f)).Generated);
        Assert.Equal(HttpStatusCode.Conflict,(await f.Author.PostAsync($"{Events}/{row.Id}/approve",null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await f.Reviewer.PostAsync($"{Events}/{row.Id}/approve",null)).StatusCode);
        using var scope=factory.Services.CreateScope(); var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.True(await db.Database.SqlQueryRaw<bool>("select finance_source_current({0}) as \"Value\"",row.Id).SingleAsync());
        Assert.Equal(request.RequestId,await db.Database.SqlQueryRaw<Guid>("select contract_version_id as \"Value\" from finance_billable_events where id={0}",row.Id).SingleAsync());
        await Assert.ThrowsAnyAsync<Exception>(()=>db.Database.ExecuteSqlRawAsync("update \"Visits\" set \"DurationMinutes\"=61 where \"Id\"={0}",f.Visit));
        await Assert.ThrowsAnyAsync<Exception>(()=>db.Database.ExecuteSqlRawAsync("update finance_contract_versions set billing_basis='Planned' where id={0}",request.RequestId));
        var invoice=await f.Reviewer.PostAsJsonAsync("/api/phase1/finance/invoice-runs",new{periodStart="2049-04-01",periodEnd="2049-04-30",funderId=f.Funder,idempotencyKey=$"test-{Guid.NewGuid()}"});
        Assert.Equal(HttpStatusCode.Created,invoice.StatusCode);
    }

    [Fact]
    public async Task LostCreationResponseReplaysAndOverlappingApprovalsSerialize()
    {
        var f=await Setup();var request=Request(f);
        var attempts=await Task.WhenAll(f.Author.PostAsJsonAsync(Contracts,request),f.Author.PostAsJsonAsync(Contracts,request));
        Assert.All(attempts,response=>response.EnsureSuccessStatusCode());Assert.Equal(1,attempts.Count(r=>r.StatusCode==HttpStatusCode.Created));
        Assert.Equal(HttpStatusCode.Conflict,(await f.Author.PostAsJsonAsync(Contracts,request with{BillingBasis="Planned"})).StatusCode);
        var second=request with{RequestId=Guid.NewGuid()};(await f.Author.PostAsJsonAsync(Contracts,second)).EnsureSuccessStatusCode();
        var approvals=await Task.WhenAll(f.Reviewer.PostAsJsonAsync($"{Contracts}/{request.RequestId}/approve",new{reason="First version"}),f.Reviewer.PostAsJsonAsync($"{Contracts}/{second.RequestId}/approve",new{reason="Competing version"}));
        Assert.Equal(1,approvals.Count(r=>r.StatusCode==HttpStatusCode.OK));Assert.Equal(1,approvals.Count(r=>r.StatusCode==HttpStatusCode.Conflict));
        var foreign=await Setup();
        Assert.Equal(HttpStatusCode.Conflict,(await foreign.Reviewer.PostAsJsonAsync($"{Contracts}/{request.RequestId}/approve",new{reason="Other tenant"})).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await f.Author.PostAsJsonAsync(Contracts,Request(f) with{RateVersionId=foreign.Version})).StatusCode);
        var worker=await Login(f.WorkerName);
        Assert.Equal(HttpStatusCode.Forbidden,(await worker.GetAsync(Contracts)).StatusCode);
    }

    [Fact]
    public async Task FractionalRatesReachInvoiceLinesWithoutDatabaseRoundingTheRate()
    {
        var f=await Setup();
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();await db.Database.ExecuteSqlRawAsync("update finance_rate_rules set unit_rate=20.1234 where rate_version_id={0}",f.Version);
        }
        var request=Request(f);(await f.Author.PostAsJsonAsync(Contracts,request)).EnsureSuccessStatusCode();(await f.Reviewer.PostAsJsonAsync($"{Contracts}/{request.RequestId}/approve",new{reason="Approved four-decimal rate"})).EnsureSuccessStatusCode();
        Assert.Equal(1,(await Generate(f)).Generated);var row=Assert.Single((await f.Author.GetFromJsonAsync<List<Event>>(Events))!);Assert.Equal(15.09m,row.FinalAmount);
        (await f.Reviewer.PostAsync($"{Events}/{row.Id}/approve",null)).EnsureSuccessStatusCode();
        (await f.Reviewer.PostAsJsonAsync("/api/phase1/finance/invoice-runs",new{periodStart="2049-04-01",periodEnd="2049-04-30",funderId=f.Funder,idempotencyKey=$"fractional-{Guid.NewGuid()}"})).EnsureSuccessStatusCode();
        using var verify=factory.Services.CreateScope();var verifyDb=verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal(20.1234m,await verifyDb.Database.SqlQueryRaw<decimal>("select unit_rate as \"Value\" from finance_invoice_lines where billable_event_id={0}",row.Id).SingleAsync());
    }

    [Fact]
    public async Task UnapprovedAndStaleSourcesCannotPostAndRejectedDraftsCanBeRegenerated()
    {
        var f=await Setup();Assert.Equal(0,(await Generate(f)).Generated);
        var request=Request(f);(await f.Author.PostAsJsonAsync(Contracts,request)).EnsureSuccessStatusCode();Assert.Equal(0,(await Generate(f)).Generated);
        (await f.Reviewer.PostAsJsonAsync($"{Contracts}/{request.RequestId}/approve",new{reason="Approved"})).EnsureSuccessStatusCode();
        Assert.Equal(1,(await Generate(f)).Generated);
        var original=Assert.Single((await f.Author.GetFromJsonAsync<List<Event>>(Events))!);
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            await db.Database.ExecuteSqlRawAsync("update timesheet_entries set status='Draft',revision=revision+1 where visit_id={0}",f.Visit);
        }
        Assert.Equal(HttpStatusCode.Conflict,(await f.Reviewer.PostAsync($"{Events}/{original.Id}/approve",null)).StatusCode);
        (await f.Author.PostAsJsonAsync($"{Events}/{original.Id}/reject",new{reason="Attendance returned for review"})).EnsureSuccessStatusCode();
        Assert.Equal(0,(await Generate(f)).Generated);
        Guid attendance;var managerName=$"attendance.{Guid.NewGuid():N}";
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();attendance=await db.Database.SqlQueryRaw<Guid>("select id as \"Value\" from timesheet_entries where visit_id={0}",f.Visit).SingleAsync();
            db.AppUsers.Add(new AppUser(Guid.NewGuid(),managerName,managerName+"@test.invalid",PasswordHasher.HashPassword("Admin123!"),UserRole.CareManager,true,f.Org,f.Branch));await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("update \"Visits\" set \"CheckedOutAt\"=\"CheckedInAt\"+interval '30 minutes' where \"Id\"={0}",f.Visit);
        }
        var manager=await Login(managerName);var correction=new{expectedRevision=2,reason="Corrected clock evidence; staff payable time reviewed separately"};
        Assert.Equal(HttpStatusCode.Forbidden,(await f.Author.PostAsJsonAsync($"/api/phase1/timesheets/entries/{attendance}/refresh-attendance",correction)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await manager.PostAsJsonAsync($"/api/phase1/timesheets/entries/{attendance}/refresh-attendance",correction)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await manager.PostAsJsonAsync($"/api/phase1/timesheets/entries/{attendance}/refresh-attendance",correction)).StatusCode);
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();Assert.Equal(55m,await db.Database.SqlQueryRaw<decimal>("select payable_minutes as \"Value\" from timesheet_entries where id={0}",attendance).SingleAsync());
        }
        var attendanceWorker=await Login(f.WorkerName);
        (await attendanceWorker.PostAsJsonAsync($"/api/phase1/timesheets/entries/{attendance}/transition",new{expectedRevision=3,action="Submit",reason="Corrected attendance reviewed"})).EnsureSuccessStatusCode();
        (await manager.PostAsJsonAsync($"/api/phase1/timesheets/entries/{attendance}/transition",new{expectedRevision=4,action="Approve",reason="Independent attendance approval"})).EnsureSuccessStatusCode();
        Assert.Equal(1,(await Generate(f)).Generated);
        var rows=(await f.Author.GetFromJsonAsync<List<Event>>(Events))!;Assert.Equal(2,rows.Count);Assert.Contains(rows,r=>r.Id==original.Id&&r.Status=="Rejected");
    }

    [Fact]
    public async Task CapsAndAmbiguousRulesProduceExceptionsWithoutInventingCharges()
    {
        var f=await Setup();var request=Request(f) with{CapMode="LifetimeAmount",CapAmount=10m};
        (await f.Author.PostAsJsonAsync(Contracts,request)).EnsureSuccessStatusCode();(await f.Reviewer.PostAsJsonAsync($"{Contracts}/{request.RequestId}/approve",new{reason="Ten pound authorisation"})).EnsureSuccessStatusCode();
        Assert.Equal(0,(await Generate(f)).Generated);
        Assert.Contains("AUTHORISATION_CAP_EXCEEDED",await f.Author.GetStringAsync("/api/phase1/finance/billing/exceptions"));
        var ambiguous=await Setup();
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            await db.Database.ExecuteSqlRawAsync("insert into finance_rate_rules(id,rate_version_id,organization_id,rule_type,day_type,unit,unit_rate,created_by) values({0},{1},{2},'Standard','Any','Hour',20,'test')",Guid.NewGuid(),ambiguous.Version,ambiguous.Org);
        }
        var other=Request(ambiguous);(await ambiguous.Author.PostAsJsonAsync(Contracts,other)).EnsureSuccessStatusCode();(await ambiguous.Reviewer.PostAsJsonAsync($"{Contracts}/{other.RequestId}/approve",new{reason="Test ambiguity"})).EnsureSuccessStatusCode();
        Assert.Equal(0,(await Generate(ambiguous)).Generated);
        Assert.Contains("AMBIGUOUS_RATE",await ambiguous.Author.GetStringAsync("/api/phase1/finance/billing/exceptions"));
    }

    [Fact]
    public async Task ConcurrentGenerationAndAdjustmentsCannotExceedAnAuthorisation()
    {
        var f=await Setup();var request=Request(f) with{CapMode="LifetimeAmount",CapAmount=20m};
        (await f.Author.PostAsJsonAsync(Contracts,request)).EnsureSuccessStatusCode();(await f.Reviewer.PostAsJsonAsync($"{Contracts}/{request.RequestId}/approve",new{reason="Explicit cap"})).EnsureSuccessStatusCode();
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();var visit=await db.Visits.AsNoTracking().SingleAsync(v=>v.Id==f.Visit);var second=Guid.NewGuid();
            db.Visits.Add(visit with{Id=second,StartsAt=visit.StartsAt.AddDays(1),CheckedInAt=visit.CheckedInAt!.Value.AddDays(1),CheckedOutAt=visit.CheckedInAt.Value.AddDays(1).AddMinutes(30)});await db.SaveChangesAsync();
            await ApprovedBillingTestData.Attendance(db,second);
        }
        var results=await Task.WhenAll(Generate(f),Generate(f));Assert.Equal(1,results.Sum(r=>r.Generated));
        var row=Assert.Single((await f.Author.GetFromJsonAsync<List<Event>>(Events))!);Assert.Equal(15m,row.FinalAmount);
        Assert.Equal(HttpStatusCode.Conflict,(await f.Author.PostAsJsonAsync($"{Events}/{row.Id}/adjust",new{adjustmentAmount=6m,reason="Would exceed authorisation"})).StatusCode);
        var unchanged=Assert.Single((await f.Author.GetFromJsonAsync<List<Event>>(Events))!);Assert.Equal(15m,unchanged.FinalAmount);
    }

    [Fact]
    public async Task RelatedBranchCurrencyAndConcurrentRateVersionsAreValidated()
    {
        var f=await Setup();Guid card;
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();card=await db.Database.SqlQueryRaw<Guid>("select rate_card_id as \"Value\" from finance_rate_versions where id={0}",f.Version).SingleAsync();
            await Assert.ThrowsAnyAsync<Exception>(()=>db.Database.ExecuteSqlRawAsync("update finance_rate_cards set currency='EUR' where id={0}",card));
        }
        // Referenced identities are protected before they can invalidate a contract.
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();await db.Database.ExecuteSqlRawAsync("update finance_rate_cards set currency='GBP' where id={0}",card);
            var otherBranch=Guid.NewGuid();db.Branches.Add(new Branch(otherBranch,f.Org,"Other","Test","Active"));await db.SaveChangesAsync();
            await Assert.ThrowsAnyAsync<Exception>(()=>db.Database.ExecuteSqlRawAsync("update finance_funders set branch_id={0} where id={1}",otherBranch,f.Funder));
        }
        // Referenced identities are protected before they can invalidate a contract.
        var rateRequest=new{effectiveFrom="2049-05-01",effectiveTo="2049-05-31",rules=new[]{new{ruleType="Standard",dayType="Any",unit="Hour",unitRate=20m,minimumQuantity=0m,priority=0}}};
        var rateResults=await Task.WhenAll(f.Author.PostAsJsonAsync($"/api/phase1/finance/rate-cards/{card}/versions",rateRequest),f.Author.PostAsJsonAsync($"/api/phase1/finance/rate-cards/{card}/versions",rateRequest));
        Assert.Equal(1,rateResults.Count(r=>r.StatusCode==HttpStatusCode.Created));Assert.Equal(1,rateResults.Count(r=>r.StatusCode==HttpStatusCode.Conflict));
        var dimensioned=await f.Author.PostAsJsonAsync($"/api/phase1/finance/rate-cards/{card}/versions",new{effectiveFrom="2049-06-01",effectiveTo="2049-06-30",rules=new[]{new{ruleType="Standard",dayType="Any",unit="Hour",unitRate=20m,minimumQuantity=0m,priority=0,staffLevel="Senior"}}});
        Assert.Equal(HttpStatusCode.Created,dimensioned.StatusCode);
        var unsupported=await f.Author.PostAsJsonAsync($"/api/phase1/finance/rate-cards/{card}/versions",new{effectiveFrom="2049-07-01",effectiveTo="2049-07-31",rules=new[]{new{ruleType="Standard",dayType="Any",unit="Hour",unitRate=20m,minimumQuantity=0m,priority=0,staffLevel=" "}}});
        Assert.Equal(HttpStatusCode.BadRequest,unsupported.StatusCode);
    }

    [Theory]
    [InlineData("NonChargeable","CANCELLATION_NON_CHARGEABLE")]
    [InlineData("ManualReview","CANCELLATION_REVIEW")]
    public async Task CancellationsFollowTheExplicitPolicyWithoutAnInventedCharge(string policy,string expectedCode)
    {
        var f=await Setup();var request=Request(f) with{CancellationPolicy=policy};
        (await f.Author.PostAsJsonAsync(Contracts,request)).EnsureSuccessStatusCode();(await f.Reviewer.PostAsJsonAsync($"{Contracts}/{request.RequestId}/approve",new{reason="Approved cancellation policy"})).EnsureSuccessStatusCode();
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();await db.Database.ExecuteSqlRawAsync("update \"Visits\" set \"Status\"='Cancelled' where \"Id\"={0}",f.Visit);
        }
        Assert.Equal(0,(await Generate(f)).Generated);Assert.Empty((await f.Author.GetFromJsonAsync<List<Event>>(Events))!);
        Assert.Contains(expectedCode,await f.Author.GetStringAsync("/api/phase1/finance/billing/exceptions"));
    }

    [Fact]
    public async Task ApprovedFundingSharesConserveTotalsAcrossPartialConcurrentInvoiceRuns()
    {
        var f=await Setup();var contract=Request(f);(await f.Author.PostAsJsonAsync(Contracts,contract)).EnsureSuccessStatusCode();(await f.Reviewer.PostAsJsonAsync($"{Contracts}/{contract.RequestId}/approve",new{reason="Authorised service"})).EnsureSuccessStatusCode();
        var fixedPayer=Guid.NewGuid();var remainderPayer=Guid.NewGuid();
        using(var scope=factory.Services.CreateScope())
        {
          var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
          foreach(var payer in new[]{fixedPayer,remainderPayer})await db.Database.ExecuteSqlRawAsync("insert into finance_funders(id,organization_id,branch_id,name,normalized_name,funder_type,currency,created_by,updated_by) values({0},{1},{2},{3},{3},'Private','GBP','test','test')",payer,f.Org,f.Branch,payer.ToString());
        }
        var allocation=new FundingAllocationRequest(Guid.NewGuid(),contract.RequestId,new(2049,4,1),new(2049,4,30),"APPROVED-TEST-SPLIT",FundingShareCalculation.Policy,[new(f.Funder,"Percentage",33.3333m,1),new(fixedPayer,"FixedAmount",5,2),new(remainderPayer,"Remainder",0,3)]);
        var root="/api/phase1/finance/funding-allocations";
        var allocationResponse=await f.Author.PostAsJsonAsync(root,allocation);
        Assert.True(allocationResponse.IsSuccessStatusCode,$"Allocation draft: {allocationResponse.StatusCode}; {await allocationResponse.Content.ReadAsStringAsync()}; {factory.LastServerException}");
        Assert.Equal(HttpStatusCode.Conflict,(await f.Author.PostAsJsonAsync($"{root}/{allocation.RequestId}/approve",new{reason="Self"})).StatusCode);
        (await f.Reviewer.PostAsJsonAsync($"{root}/{allocation.RequestId}/approve",new{reason="Funding agreement reviewed"})).EnsureSuccessStatusCode();
        Assert.Equal(1,(await Generate(f)).Generated);var billable=Assert.Single((await f.Author.GetFromJsonAsync<List<Event>>(Events))!);
        (await f.Reviewer.PostAsync($"{Events}/{billable.Id}/approve",null)).EnsureSuccessStatusCode();
        using(var scope=factory.Services.CreateScope())
        {
          var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();Assert.Equal(15m,await db.Database.SqlQueryRaw<decimal>("select sum(amount) as \"Value\" from finance_event_shares where billable_event_id={0}",billable.Id).SingleAsync());
          await Assert.ThrowsAnyAsync<Exception>(()=>db.Database.ExecuteSqlRawAsync("update finance_event_shares set amount=amount+1 where billable_event_id={0}",billable.Id));
        }
        var run=new{periodStart="2049-04-01",periodEnd="2049-04-30",funderId=remainderPayer,idempotencyKey=$"partial-{Guid.NewGuid()}"};
        var attempts=await Task.WhenAll(f.Reviewer.PostAsJsonAsync("/api/phase1/finance/invoice-runs",run),f.Reviewer.PostAsJsonAsync("/api/phase1/finance/invoice-runs",run));Assert.All(attempts,r=>r.EnsureSuccessStatusCode());Assert.Equal(1,attempts.Count(x=>x.StatusCode==HttpStatusCode.Created));
        var remainingRun=await f.Reviewer.PostAsJsonAsync("/api/phase1/finance/invoice-runs",new{periodStart="2049-04-01",periodEnd="2049-04-30",idempotencyKey=$"rest-{Guid.NewGuid()}"});
        Assert.True(remainingRun.IsSuccessStatusCode,$"Remaining funding shares: {remainingRun.StatusCode}; {await remainingRun.Content.ReadAsStringAsync()}; {factory.LastServerException}");
        using var verify=factory.Services.CreateScope();var verifyDb=verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal(15m,await verifyDb.Database.SqlQueryRaw<decimal>("select sum(amount) as \"Value\" from finance_invoice_lines where billable_event_id={0}",billable.Id).SingleAsync());
        Assert.Equal(3,await verifyDb.Database.SqlQueryRaw<int>("select count(*)::int as \"Value\" from finance_invoice_lines where billable_event_id={0}",billable.Id).SingleAsync());Assert.Equal("Invoiced",(await verifyDb.Database.SqlQueryRaw<string>("select status as \"Value\" from finance_billable_events where id={0}",billable.Id).SingleAsync()));
    }

    [Fact]
    public async Task PeriodAmountAndMinuteCapsReserveOnceAndRejectConcurrentExcess()
    {
        var f=await Setup();var contract=Request(f);(await f.Author.PostAsJsonAsync(Contracts,contract)).EnsureSuccessStatusCode();(await f.Reviewer.PostAsJsonAsync($"{Contracts}/{contract.RequestId}/approve",new{reason="Authorised"})).EnsureSuccessStatusCode();
        var cap=new PeriodAuthorisationRequest(Guid.NewGuid(),contract.RequestId,new(2049,4,1),new(2049,4,30),"APPROVED-PERIOD",20m,45m);var root="/api/phase1/finance/funding-allocations/period-caps";
        (await f.Author.PostAsJsonAsync(root,cap)).EnsureSuccessStatusCode();Assert.Equal(HttpStatusCode.Conflict,(await f.Author.PostAsJsonAsync($"{root}/{cap.RequestId}/approve",new{reason="Self"})).StatusCode);(await f.Reviewer.PostAsJsonAsync($"{root}/{cap.RequestId}/approve",new{reason="Period reviewed"})).EnsureSuccessStatusCode();
        var results=await Task.WhenAll(Generate(f),Generate(f));Assert.Equal(1,results.Sum(x=>x.Generated));
        var billable=Assert.Single((await f.Author.GetFromJsonAsync<List<Event>>(Events))!);Assert.Equal(HttpStatusCode.Conflict,(await f.Author.PostAsJsonAsync($"{Events}/{billable.Id}/adjust",new{adjustmentAmount=6m,reason="Over period cap"})).StatusCode);
        using var scope=factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal(cap.RequestId,await db.Database.SqlQueryRaw<Guid>("select period_authorisation_id as \"Value\" from finance_billable_events where id={0}",billable.Id).SingleAsync());
        var source=await db.Visits.AsNoTracking().SingleAsync(v=>v.Id==f.Visit);var second=Guid.NewGuid();db.Visits.Add(source with{Id=second,StartsAt=source.StartsAt.AddDays(1),CheckedInAt=source.CheckedInAt!.Value.AddDays(1),CheckedOutAt=source.CheckedOutAt!.Value.AddDays(1)});await db.SaveChangesAsync();await ApprovedBillingTestData.Attendance(db,second);
        Assert.Equal(HttpStatusCode.Conflict,(await f.Author.PostAsJsonAsync("/api/phase1/finance/billing/generate",new{serviceFrom="2049-04-01",serviceTo="2049-04-30"})).StatusCode);
        await Assert.ThrowsAnyAsync<Exception>(()=>db.Database.ExecuteSqlRawAsync("update finance_period_authorisations set minutes_limit=90 where id={0}",cap.RequestId));
    }

    private const string Contracts="/api/phase1/finance/billing-contracts";
    private const string Events="/api/phase1/finance/billing/events";
    private static BillingContractRequest Request(Fixture f,string basis="Actual")=>new(Guid.NewGuid(),f.Funding,f.Version,"Care",new(2049,4,1),new(2049,4,30),"TEST-AUTH",basis,"NonChargeable","AwayFromZero","EXPLICIT-TEST-POLICY","Uncapped",null);
    private static async Task<Summary> Generate(Fixture f)
    {
        var response=await f.Author.PostAsJsonAsync("/api/phase1/finance/billing/generate",new{periodStart="2049-04-01T00:00:00Z",periodEnd="2049-05-01T00:00:00Z"});response.EnsureSuccessStatusCode();return (await response.Content.ReadFromJsonAsync<Summary>())!;
    }
    private async Task<Fixture> Setup(bool configureVat=true)
    {
        await factory.EnsureClinicalSeedAsync();var org=Guid.NewGuid();var branch=Guid.NewGuid();var person=Guid.NewGuid();var worker=Guid.NewGuid();var visit=Guid.NewGuid();var funder=Guid.NewGuid();var card=Guid.NewGuid();var version=Guid.NewGuid();var funding=Guid.NewGuid();var author=Guid.NewGuid();var reviewer=Guid.NewGuid();var name=$"contract.{Guid.NewGuid():N}";
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();db.Organizations.Add(new Organization(org,"Contract test","Trial","Active"));db.Branches.Add(new Branch(branch,org,"Test","Test","Active"));
            db.CareWorkers.Add(new CareWorker(worker,"Test worker","Care","Flexible",0,0,"Valid","Compliant","10",org,branch));
            db.ServiceUsers.Add(new ServiceUser(person,"Test recipient",new DateOnly(1950,1,1),"1","Care","Contact","",RiskLevel.Low,"Active","Care address","None","None","Private","","","Independent","Independent","Verbal","Routine","Standard",org,branch));
            var start=new DateTimeOffset(2049,4,7,9,0,0,TimeSpan.Zero);db.Visits.Add(new Visit(visit,person,worker,start,"Care",60,"Care",VisitStatus.Completed,start,start.AddMinutes(45),null,null,null,null,org,branch));
            db.AppUsers.AddRange(new AppUser(author,name+".author",name+".author@test.invalid",PasswordHasher.HashPassword("Admin123!"),UserRole.BackOffice,true,org,branch),new AppUser(reviewer,name+".reviewer",name+".reviewer@test.invalid",PasswordHasher.HashPassword("Admin123!"),UserRole.BackOffice,true,org,branch),new AppUser(Guid.NewGuid(),name+".worker",name+".worker@test.invalid",PasswordHasher.HashPassword("Admin123!"),UserRole.CareWorker,true,org,branch,worker));await db.SaveChangesAsync();
            var period=Guid.NewGuid();
            await db.Database.ExecuteSqlRawAsync("""
                insert into finance_funders(id,organization_id,branch_id,name,normalized_name,funder_type,currency,created_by,updated_by) values({0},{1},{2},'Payer','PAYER','Private','GBP','test','test');
                insert into finance_rate_cards(id,organization_id,branch_id,name,service_type,currency,created_by,updated_by) values({3},{1},{2},'Care rate','Care','GBP','test','test');
                insert into finance_rate_versions(id,rate_card_id,organization_id,branch_id,version,effective_from,effective_to,status,created_by) values({4},{3},{1},{2},1,'2049-04-01','2049-04-30','Active','test');
                insert into finance_rate_rules(id,rate_version_id,organization_id,rule_type,day_type,unit,unit_rate,created_by) values({5},{4},{1},'Standard','Any','Hour',20,'test');
                insert into funding_arrangements(id,service_user_id,organization_id,branch_id,funding_source,funder_name,authorized_hours_per_week,hourly_rate,valid_from,status,funder_id,rate_card_id,created_by) values({6},{7},{1},{2},'Private','Payer',168,20,'2049-04-01','Active',{0},{3},'test');
                insert into timesheet_periods(id,organization_id,branch_id,period_start,period_end,hourly_rate,travel_rate,mileage_rate,created_by) values({8},{1},{2},'2049-04-01','2049-05-01',0,0,0,{9});
                insert into timesheet_entries(id,period_id,organization_id,branch_id,visit_id,care_worker_id,planned_minutes,actual_minutes,payable_minutes,hourly_rate,status,reason,changed_by,submitted_by,approved_by) values({10},{8},{1},{2},{11},{12},60,45,55,0,'Approved','Approved attendance differs from payable time',{13},{9},{13});
                insert into finance_invoice_profiles(organization_id,branch_id,provider_name,default_vat_rate,vat_exemption_reason) values({1},{2},'Test provider',0,'Explicit test configuration');
                """,funder,org,branch,card,version,Guid.NewGuid(),funding,person,period,author,Guid.NewGuid(),visit,worker,reviewer);
        }
        if(configureVat){using var vatScope=factory.Services.CreateScope();await ApprovedVatTestData.Ensure(vatScope.ServiceProvider.GetRequiredService<CareDbContext>(),org,branch,"Care");}
        return new(await Login(name+".author"),await Login(name+".reviewer"),org,branch,visit,funder,funding,version,name+".worker");
    }
    private async Task<HttpClient> Login(string name){var client=factory.CreateClient();var response=await client.PostAsJsonAsync("/api/auth/login",new{userName=name,password="Admin123!",mfaCode=(string?)null});response.EnsureSuccessStatusCode();client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await response.Content.ReadFromJsonAsync<LoginResponse>())!.Token);return client;}
    private sealed record Fixture(HttpClient Author,HttpClient Reviewer,Guid Org,Guid Branch,Guid Visit,Guid Funder,Guid Funding,Guid Version,string WorkerName);
    private sealed record LoginResponse(string Token);
    private sealed record Summary(int Generated,int Exceptions);
    private sealed record Event(Guid Id,decimal FinalAmount,string Status);
}
