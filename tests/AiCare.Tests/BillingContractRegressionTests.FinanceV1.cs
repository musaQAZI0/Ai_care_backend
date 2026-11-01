using System.Net;
using System.Net.Http.Headers;
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
    private const string Claims = "/api/phase1/finance/workforce-claims";
    private const string Adjustments = "/api/phase1/finance/adjustments";

    [Fact]
    public async Task EvidenceIsPrivateImmutableAndDeduplicatedForItsWorker()
    {
        var f = await Setup();
        var worker = await WorkerId(f);
        var staff = await Login(f.WorkerName);
        var bytes = FinanceExportDocument.Pdf("Receipt", ["Reference"], [["EVIDENCE-1"]]);
        var evidence = await Upload(staff, worker, bytes);
        Assert.Equal(evidence, await Upload(staff, worker, bytes));
        var download = await staff.GetAsync($"{Claims}/evidence/{evidence}");
        download.EnsureSuccessStatusCode();
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
        Assert.True(download.Headers.CacheControl?.NoStore);
        Assert.Equal("nosniff", Assert.Single(download.Headers.GetValues("X-Content-Type-Options")));
        (await f.Reviewer.GetAsync($"{Claims}/evidence/{evidence}")).EnsureSuccessStatusCode();

        var otherWorker = await AddWorker(f);
        var otherStaff = await AddActor(f, UserRole.CareWorker, otherWorker);
        Assert.Equal(HttpStatusCode.NotFound, (await otherStaff.GetAsync($"{Claims}/evidence/{evidence}")).StatusCode);
        using var deniedForm = EvidenceForm(worker, bytes);
        Assert.Equal(HttpStatusCode.Forbidden, (await otherStaff.PostAsync(Claims + "/evidence", deniedForm)).StatusCode);
        var foreign = await Setup();
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.Author.GetAsync($"{Claims}/evidence/{evidence}")).StatusCode);
        var branchName = $"evidence.branch.{Guid.NewGuid():N}";
        using (var branchScope = factory.Services.CreateScope())
        {
            var branchDb = branchScope.ServiceProvider.GetRequiredService<CareDbContext>(); var branch = Guid.NewGuid();
            branchDb.Branches.Add(new Branch(branch,f.Org,"Other branch","Test","Active"));
            branchDb.AppUsers.Add(new AppUser(Guid.NewGuid(),branchName,branchName+"@test.invalid",PasswordHasher.HashPassword("Admin123!"),UserRole.BackOffice,true,f.Org,branch));
            await branchDb.SaveChangesAsync();
        }
        var otherBranch = await Login(branchName);
        Assert.Equal(HttpStatusCode.NotFound, (await otherBranch.GetAsync($"{Claims}/evidence/{evidence}")).StatusCode);
        using var invalid = EvidenceForm(worker, "MZ-not-a-pdf"u8.ToArray());
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await staff.PostAsync(Claims + "/evidence", invalid)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("select count(*)::int as \"Value\" from finance_claim_evidence where care_worker_id={0}", worker).SingleAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("update finance_claim_evidence set file_name='changed.pdf' where id={0}", evidence));
    }

    [Fact]
    public async Task DuplicateEvidenceAndNormalisedReceiptsCannotCreateAnotherActiveClaim()
    {
        var f = await Setup();
        var policy = await ExpensePolicy(f);
        var worker = await WorkerId(f);
        var evidence = await Upload(f.Author, worker);
        var request = Expense(f, worker, policy, evidence, "TRIP-1", " Shop  Name ", " inv-01 ");
        var attempts = await Task.WhenAll(f.Author.PostAsJsonAsync(Claims, request), f.Author.PostAsJsonAsync(Claims, request));
        Assert.All(attempts, x => x.EnsureSuccessStatusCode());
        Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.Created);
        var id = (await attempts.Single(x => x.StatusCode == HttpStatusCode.Created).Content.ReadFromJsonAsync<CreatedId>())!.Id;
        Assert.Equal(HttpStatusCode.Conflict, (await f.Author.PostAsJsonAsync(Claims, request with { Quantity = 11 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Reviewer.PostAsJsonAsync(Claims, request)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Author.PostAsJsonAsync(Claims, request with { SourceReference = "OTHER-SOURCE", ReceiptNumber = "OTHER" })).StatusCode);
        var anotherEvidence = await Upload(f.Author, worker);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Author.PostAsJsonAsync(Claims, request with { SourceReference = "OTHER-RECEIPT", EvidenceId = anotherEvidence, ReceiptIssuer = "shop name", ReceiptNumber = "INV-01" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Author.PostAsJsonAsync(Claims, request with { SourceReference = "NO-EVIDENCE", EvidenceId = null })).StatusCode);
        var otherWorker = await AddWorker(f);
        var otherEvidence = await Upload(f.Author, otherWorker);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Author.PostAsJsonAsync(Claims, request with { SourceReference = "WRONG-WORKER", EvidenceId = otherEvidence })).StatusCode);
        (await f.Reviewer.PostAsJsonAsync($"{Claims}/{id}/review", new ReviewClaimRequest("Reject", "Duplicate receipt corrected"))).EnsureSuccessStatusCode();
        var replacement = request with { SourceReference = "REPLACEMENT" };
        (await f.Author.PostAsJsonAsync(Claims, replacement)).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("select count(*)::int as \"Value\" from finance_workforce_claims where organization_id={0} and status<>'Rejected'", f.Org).SingleAsync());
    }

    [Fact]
    public async Task RechargeApprovalLimitsAndSeparateVatControlExactlyOneInvoiceSupply()
    {
        var f = await Setup();
        var contract = await ApprovedContract(f);
        var claimPolicy = await ExpensePolicy(f);
        var worker = await WorkerId(f);
        var claim = await SubmitExpense(f, worker, claimPolicy, "EXPENSE-1");
        var policy = new ExpenseRechargePolicyRequest(Guid.NewGuid(), contract, claimPolicy, "Travel expense", "APPROVED-RECHARGE", new(2049,4,1), new(2049,4,30), "ClaimAmountAsNet", 1.25m, 15m, 15m);
        (await f.Author.PostAsJsonAsync(Claims + "/recharge-policies", policy)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await f.Author.PostAsync($"{Claims}/recharge-policies/{policy.RequestId}/approve", null)).StatusCode);
        var recharge = new ExpenseRechargeRequest(Guid.NewGuid(), claim, policy.RequestId, "Contract permits actual expense recharge");
        Assert.Equal(HttpStatusCode.Conflict, (await f.Author.PostAsJsonAsync(Claims + "/recharges", recharge)).StatusCode);
        (await f.Reviewer.PostAsync($"{Claims}/recharge-policies/{policy.RequestId}/approve", null)).EnsureSuccessStatusCode();
        // An unapproved claim cannot reserve an authorisation limit.
        Assert.Equal(HttpStatusCode.Conflict, (await f.Author.PostAsJsonAsync(Claims + "/recharges", recharge)).StatusCode);
        (await f.Reviewer.PostAsJsonAsync($"{Claims}/{claim}/review", new ReviewClaimRequest("Approve", "Evidence verified"))).EnsureSuccessStatusCode();
        var attempts = await Task.WhenAll(f.Author.PostAsJsonAsync(Claims + "/recharges", recharge), f.Author.PostAsJsonAsync(Claims + "/recharges", recharge));
        Assert.All(attempts, x => x.EnsureSuccessStatusCode());
        Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Author.PostAsJsonAsync(Claims + "/recharges", recharge with { Reason = "Changed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Author.PostAsJsonAsync(Claims + "/recharges", recharge with { RequestId = Guid.NewGuid() })).StatusCode);
        var secondClaim = await SubmitExpense(f, worker, claimPolicy, "EXPENSE-2");
        (await f.Reviewer.PostAsJsonAsync($"{Claims}/{secondClaim}/review", new ReviewClaimRequest("Approve", "Separate receipt verified"))).EnsureSuccessStatusCode();
        var second = recharge with { RequestId = Guid.NewGuid(), ClaimId = secondClaim };
        Assert.Equal(HttpStatusCode.Conflict, (await f.Author.PostAsJsonAsync(Claims + "/recharges", second)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Author.PostAsJsonAsync($"{Claims}/recharges/{recharge.RequestId}/review", new ReviewClaimRequest("Approve", "Self"))).StatusCode);
        (await f.Reviewer.PostAsJsonAsync($"{Claims}/recharges/{recharge.RequestId}/review", new ReviewClaimRequest("Reject", "Release reservation"))).EnsureSuccessStatusCode();
        (await f.Author.PostAsJsonAsync(Claims + "/recharges", second)).EnsureSuccessStatusCode();
        (await f.Reviewer.PostAsJsonAsync($"{Claims}/recharges/{second.RequestId}/review", new ReviewClaimRequest("Approve", "Independent recharge review"))).EnsureSuccessStatusCode();
        var run = new InvoiceRunRequest(new(2049,4,1), new(2049,4,30), f.Funder, $"expense-{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Conflict, (await f.Author.PostAsJsonAsync("/api/phase1/finance/invoice-runs", run)).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            Assert.Empty(await db.Invoices.Where(x => x.OrganizationId == f.Org).ToListAsync());
            await ApprovedVatTestData.Ensure(db, f.Org, f.Branch, "Travel expense", 20);
        }
        (await f.Author.PostAsJsonAsync("/api/phase1/finance/invoice-runs", run)).EnsureSuccessStatusCode();
        (await f.Author.PostAsJsonAsync("/api/phase1/finance/invoice-runs", run)).EnsureSuccessStatusCode();
        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        var line = await verifyDb.Database.SqlQueryRaw<RechargeLine>("select amount as \"Net\",vat_amount as \"Tax\",visit_id is null and billable_event_id is null as \"Separate\" from finance_invoice_lines where recharge_id={0}", second.RequestId).SingleAsync();
        Assert.Equal(12.50m, line.Net); Assert.Equal(2.50m, line.Tax); Assert.True(line.Separate);
        Assert.Equal(15m, (await verifyDb.Invoices.SingleAsync(x => x.OrganizationId == f.Org)).Amount);
        Assert.Equal("Approved", await verifyDb.Database.SqlQueryRaw<string>("select status as \"Value\" from finance_workforce_claims where id={0}", secondClaim).SingleAsync());
        var staff = await Login(f.WorkerName);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync(Claims + "/recharges")).StatusCode);
    }

    [Fact]
    public async Task MultiWorkerVisitsRequireIndependentPerVisitPolicyAndPreserveCrewLineage()
    {
        var f = await Setup();
        var contract = await ApprovedContract(f);
        var additional = await AddWorker(f);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CareDbContext>().Database.ExecuteSqlRawAsync("insert into visit_care_worker_assignments(visit_id,care_worker_id,organization_id,branch_id) values({0},{1},{2},{3})", f.Visit, additional, f.Org, f.Branch);
        Assert.Equal(0, (await Generate(f)).Generated);
        var policy = new MultiWorkerBillingPolicyRequest(Guid.NewGuid(), contract, "PerWorker", "CREW-AUTHORITY");
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Author.PostAsJsonAsync(Contracts + "/multi-worker-policies", policy)).StatusCode);
        policy = policy with { BillingUnit = "PerVisit" };
        (await f.Author.PostAsJsonAsync(Contracts + "/multi-worker-policies", policy)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await f.Author.PostAsJsonAsync(Contracts + "/multi-worker-policies", policy)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Author.PostAsync($"{Contracts}/multi-worker-policies/{policy.RequestId}/approve", null)).StatusCode);
        Assert.Equal(0, (await Generate(f)).Generated);
        (await f.Reviewer.PostAsync($"{Contracts}/multi-worker-policies/{policy.RequestId}/approve", null)).EnsureSuccessStatusCode();
        Assert.Equal(1, (await Generate(f)).Generated);
        var billable = Assert.Single((await f.Author.GetFromJsonAsync<List<Event>>(Events))!);
        Assert.Equal(15m, billable.FinalAmount);
        (await f.Reviewer.PostAsync($"{Events}/{billable.Id}/approve", null)).EnsureSuccessStatusCode();
        using var verify = factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.True(await db.Database.SqlQueryRaw<bool>("select source_snapshot->'additionalWorkers' @> jsonb_build_array(jsonb_build_object('worker',cast({0} as uuid))) as \"Value\" from finance_billable_events where id={1}", additional, billable.Id).SingleAsync());
        Assert.True(await db.Database.SqlQueryRaw<bool>("select finance_source_current({0}) as \"Value\"", billable.Id).SingleAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("delete from visit_care_worker_assignments where visit_id={0}", f.Visit));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("update finance_multi_worker_policies set reference='changed' where id={0}", policy.RequestId));
        (await f.Author.PostAsJsonAsync("/api/phase1/finance/invoice-runs", new InvoiceRunRequest(new(2049,4,1),new(2049,4,30),f.Funder,$"crew-{Guid.NewGuid()}"))).EnsureSuccessStatusCode();
        Assert.Equal(15m, await db.Database.SqlQueryRaw<decimal>("select sum(amount) as \"Value\" from finance_invoice_lines where billable_event_id={0}", billable.Id).SingleAsync());
    }

    private async Task<Guid> ApprovedContract(Fixture f)
    {
        var request = Request(f);
        (await f.Author.PostAsJsonAsync(Contracts, request)).EnsureSuccessStatusCode();
        (await f.Reviewer.PostAsJsonAsync($"{Contracts}/{request.RequestId}/approve", new { reason = "Explicit synthetic contract approval" })).EnsureSuccessStatusCode();
        return request.RequestId;
    }
    private async Task<Guid> WorkerId(Fixture f)
    {
        using var scope = factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<CareDbContext>().Visits.AsNoTracking().SingleAsync(v => v.Id == f.Visit)).CareWorkerId;
    }
    private async Task<Guid> AddWorker(Fixture f)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>(); var id = Guid.NewGuid();
        db.CareWorkers.Add(new CareWorker(id,"Additional worker","Care","Flexible",0,0,"Valid","Compliant","10",f.Org,f.Branch));
        await db.SaveChangesAsync(); return id;
    }
    private async Task<HttpClient> AddActor(Fixture f, UserRole role, Guid? worker = null)
    {
        var name = $"finance.actor.{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.AppUsers.Add(new AppUser(Guid.NewGuid(),name,name+"@test.invalid",PasswordHasher.HashPassword("Admin123!"),role,true,f.Org,f.Branch,worker));
            await db.SaveChangesAsync();
        }
        return await Login(name);
    }
    private static MultipartFormDataContent EvidenceForm(Guid worker, byte[] bytes)
    {
        var form = new MultipartFormDataContent(); form.Add(new StringContent(worker.ToString()), "careWorkerId");
        var file = new ByteArrayContent(bytes); file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file,"file","receipt.pdf"); return form;
    }
    private static async Task<Guid> Upload(HttpClient client, Guid worker, byte[]? bytes = null)
    {
        using var form = EvidenceForm(worker, bytes ?? FinanceExportDocument.Pdf("Receipt", ["Reference"], [[Guid.NewGuid().ToString()]]));
        var result = await client.PostAsync(Claims + "/evidence", form); result.EnsureSuccessStatusCode();
        return (await result.Content.ReadFromJsonAsync<CreatedId>())!.Id;
    }
    private static async Task<Guid> ExpensePolicy(Fixture f)
    {
        var result = await f.Author.PostAsJsonAsync(Claims + "/policies", new ClaimPolicyRequest(f.Branch,"TEST-EXPENSE","Expense","Travel",new(2049,4,1),new(2049,4,30),"GBP",1,100,true,true,"AwayFromZero"));
        result.EnsureSuccessStatusCode(); var id = (await result.Content.ReadFromJsonAsync<CreatedId>())!.Id;
        (await f.Reviewer.PostAsync($"{Claims}/policies/{id}/approve",null)).EnsureSuccessStatusCode(); return id;
    }
    private static WorkforceClaimRequest Expense(Fixture f, Guid worker, Guid policy, Guid evidence, string source, string issuer = "Test shop", string? receipt = null) =>
        new(worker,f.Visit,"Expense",new(2049,4,7),10,999,"Travel","Business expense","Receipt verified",policy,source,evidence,issuer,receipt??source);
    private static async Task<Guid> SubmitExpense(Fixture f, Guid worker, Guid policy, string source)
    {
        var evidence = await Upload(f.Author,worker);
        var result = await f.Author.PostAsJsonAsync(Claims,Expense(f,worker,policy,evidence,source)); result.EnsureSuccessStatusCode();
        return (await result.Content.ReadFromJsonAsync<CreatedId>())!.Id;
    }
    private sealed record CreatedId(Guid Id);
    private sealed record RechargeLine(decimal Net, decimal Tax, bool Separate);
}
