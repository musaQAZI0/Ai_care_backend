using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed class ReceiptAllocationRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task MixedSourcesRetriesCreditsAndConcurrentRefundsConserveMoney()
    {
        var (client, invoice) = await Setup();
        var receipt = Guid.NewGuid(); var allocation = Guid.NewGuid();
        var request = new { receiptId=receipt, amount=60m, currency="GBP", reference=$"BANK-{receipt}" };
        var creations = await Task.WhenAll(client.PostAsJsonAsync(Root,request),client.PostAsJsonAsync(Root,request));
        Assert.All(creations,r=>r.EnsureSuccessStatusCode());
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync(Root,new {receiptId=receipt,amount=61m,currency="GBP",request.reference})).StatusCode);
        var allocations = await Task.WhenAll(Allocate(client,receipt,invoice,60m,allocation),Allocate(client,receipt,invoice,60m,allocation));
        Assert.All(allocations,r=>r.EnsureSuccessStatusCode());
        Assert.Equal(1,allocations.Count(r=>r.StatusCode==HttpStatusCode.Created));
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync(Invoice(invoice)+"/payments",new{amount=41m,reference="OVER"})).StatusCode);
        await ExpectStatus(await client.PostAsJsonAsync(Invoice(invoice)+"/payments",new{amount=40m,reference="DIRECT"}),"Paid");
        await ExpectStatus(await ControlledAdjustmentTestData.Post(factory,client,invoice,"Credit",20m,"CREDIT-20","Correction"),"Paid");
        await ExpectStatus(await ControlledAdjustmentTestData.Post(factory,client,invoice,"Refund",20m,"REF-1","Return credit"),"Paid");
        var refunds=await Task.WhenAll(
            ControlledAdjustmentTestData.Post(factory,client,invoice,"Refund",50m,"RACE-A","Return payment"),
            ControlledAdjustmentTestData.Post(factory,client,invoice,"Refund",50m,"RACE-B","Return payment"));
        Assert.Equal(1,refunds.Count(r=>r.StatusCode==HttpStatusCode.Created));
        Assert.Equal(1,refunds.Count(r=>r.StatusCode==HttpStatusCode.Conflict));
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync($"{Root}/{receipt}/allocations/{allocation}/reverse",new{reason="Cannot release refunded money"})).StatusCode);
        await ExpectStatus(await ControlledAdjustmentTestData.Post(factory,client,invoice,"Refund",30m,"REF-FINAL","Return remainder"),"Issued");
        Assert.Equal(HttpStatusCode.Conflict,(await ControlledAdjustmentTestData.Post(factory,client,invoice,"Refund",0.01m,"OVER-REFUND","Over")).StatusCode);
        await ExpectStatus(await ControlledAdjustmentTestData.Post(factory,client,invoice,"Credit",80m,"CREDIT-80","Full correction"),"Credited");
        using var scope=factory.Services.CreateScope(); var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal(1,await db.Database.SqlQueryRaw<int>("select count(*)::int as \"Value\" from finance_receipts where id={0}",receipt).SingleAsync());
        Assert.Equal(1,await db.Database.SqlQueryRaw<int>("select count(*)::int as \"Value\" from finance_payment_allocations where receipt_id={0}",receipt).SingleAsync());
    }

    [Fact]
    public async Task DirectPaymentAndReceiptAllocationShareTheSameInvoiceLock()
    {
        var (client,invoice)=await Setup(); var receipt=await Receipt(client,70m);
        var attempts=await Task.WhenAll(
            Allocate(client,receipt,invoice,70m,Guid.NewGuid()),
            client.PostAsJsonAsync(Invoice(invoice)+"/payments",new{amount=70m,reference="DIRECT-RACE"}));
        Assert.Equal(1,attempts.Count(r=>r.StatusCode==HttpStatusCode.Created));
        Assert.Equal(1,attempts.Count(r=>r.StatusCode==HttpStatusCode.Conflict));
        using var scope=factory.Services.CreateScope(); var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
        var received=await db.Database.SqlQueryRaw<decimal>("""
            select coalesce((select sum(amount) from finance_payments where invoice_id={0}),0)
                 + coalesce((select sum(amount) from finance_payment_allocations where invoice_id={0} and reversed_at is null),0) as "Value"
            """,invoice).SingleAsync();
        Assert.Equal(70m,received);
    }

    [Fact]
    public async Task CompetingAllocationsCannotOverpayAndReversalsAreRecoverable()
    {
        var (client, invoice)=await Setup();
        var first=await Receipt(client,70m); var second=await Receipt(client,70m);
        var results=await Task.WhenAll(Allocate(client,first,invoice,70m,Guid.NewGuid()),Allocate(client,second,invoice,70m,Guid.NewGuid()));
        Assert.Equal(1,results.Count(r=>r.StatusCode==HttpStatusCode.Created));
        Assert.Equal(1,results.Count(r=>r.StatusCode==HttpStatusCode.Conflict));
        var winner=results[0].StatusCode==HttpStatusCode.Created?0:1;
        var body=await results[winner].Content.ReadFromJsonAsync<Allocation>();
        var receipt=winner==0?first:second;
        var reverse=$"{Root}/{receipt}/allocations/{body!.Id}/reverse";
        await ExpectStatus(await client.PostAsJsonAsync(reverse,new{reason="Wrong invoice"}),"Issued");
        (await client.PostAsJsonAsync(reverse,new{reason="Retry after response lost"})).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict,(await Allocate(client,receipt,invoice,70m,body.Id)).StatusCode);
        (await Allocate(client,receipt,invoice,70m,Guid.NewGuid())).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync(Root,new{amount=0.001m,currency="GBP",reference="FRACTION"})).StatusCode);
        var usd=await Receipt(client,10m,"USD");
        Assert.Equal(HttpStatusCode.Conflict,(await Allocate(client,usd,invoice,10m,Guid.NewGuid())).StatusCode);
    }

    [Fact]
    public async Task ReceiptBalanceSerializesAcrossInvoicesAndScopeIsEnforced()
    {
        var (client, invoice)=await Setup(); var (_,otherInvoice)=await Setup();
        var receipt=await Receipt(client,100m);
        var attempts=await Task.WhenAll(Allocate(client,receipt,invoice,70m,Guid.NewGuid()),Allocate(client,receipt,otherInvoice,70m,Guid.NewGuid()));
        Assert.Equal(1,attempts.Count(r=>r.StatusCode==HttpStatusCode.Created));
        Assert.Equal(1,attempts.Count(r=>r.StatusCode==HttpStatusCode.Conflict));
        var (otherBranch,_)=await Setup(branch:Guid.NewGuid());
        var crossBranchReceipt=await Receipt(otherBranch,10m);
        Assert.Equal(HttpStatusCode.NotFound,(await Allocate(otherBranch,crossBranchReceipt,invoice,1m,Guid.NewGuid())).StatusCode);
        var (otherTenant,_)=await Setup(organization:Guid.NewGuid(),branch:Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound,(await Allocate(otherTenant,receipt,invoice,1m,Guid.NewGuid())).StatusCode);
        var (worker,_)=await Setup(role:UserRole.CareWorker);
        Assert.Equal(HttpStatusCode.Forbidden,(await worker.GetAsync(Root)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await Allocate(worker,receipt,invoice,1m,Guid.NewGuid())).StatusCode);
    }

    private const string Root="/api/phase1/finance/receipts";
    private static string Invoice(Guid id)=>$"/api/phase1/finance/invoices/{id}";
    private static Task<HttpResponseMessage> Allocate(HttpClient client,Guid receipt,Guid invoice,decimal amount,Guid allocation)=>client.PostAsJsonAsync($"{Root}/{receipt}/allocate",new{invoiceId=invoice,amount,allocationId=allocation});
    private static async Task<Guid> Receipt(HttpClient client,decimal amount,string currency="GBP")
    {
        var id=Guid.NewGuid(); var response=await client.PostAsJsonAsync(Root,new{receiptId=id,amount,currency,reference=$"BANK-{id}"}); response.EnsureSuccessStatusCode(); return id;
    }
    private static async Task ExpectStatus(HttpResponseMessage response,string status)
    {
        response.EnsureSuccessStatusCode(); Assert.Equal(status,(await response.Content.ReadFromJsonAsync<Transition>())!.Status);
    }
    private async Task<(HttpClient Client,Guid Invoice)> Setup(Guid? organization=null,Guid? branch=null,UserRole role=UserRole.BackOffice)
    {
        await factory.EnsureClinicalSeedAsync(); var org=organization??TenantDefaults.OrganizationId; var b=branch??TenantDefaults.BranchId;
        var person=Guid.NewGuid(); var invoice=Guid.NewGuid(); var name=$"receipt.{Guid.NewGuid():N}";
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            if(organization.HasValue) db.Organizations.Add(new Organization(org,"Receipt tenant","Trial","Active"));
            if(branch.HasValue) db.Branches.Add(new Branch(b,org,"Receipt branch","Test","Active"));
            db.AppUsers.Add(new AppUser(Guid.NewGuid(),name,$"{name}@aicare.local",PasswordHasher.HashPassword("Admin123!"),role,true,org,b,null,null));
            db.ServiceUsers.Add(new ServiceUser(person,"Receipt recipient",new DateOnly(1950,1,1),"1","Care","Contact","",RiskLevel.Low,"Active","Care address","None","None","Private","","","Independent","Independent","Verbal","Routine","Standard",org,b));
            db.Invoices.Add(new Invoice(invoice,person,"Payer",100m,"Issued",DateTimeOffset.UtcNow,org,b)); await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("""
                insert into finance_invoice_details(invoice_id,organization_id,branch_id,invoice_number,invoice_date,due_date,payment_terms,provider_name,customer_name,net_amount,vat_rate,vat_amount,gross_amount)
                values({0},{1},{2},{3},current_date,current_date+30,'30 days','Test provider','Test payer',100,0,0,100)
                """,invoice,org,b,$"RECEIPT-TEST-{invoice}");
        }
        var client=factory.CreateClient(); var login=await client.PostAsJsonAsync("/api/auth/login",new{userName=name,password="Admin123!",mfaCode=(string?)null}); login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await login.Content.ReadFromJsonAsync<Login>())!.Token);
        return (client,invoice);
    }
    private sealed record Login(string Token);
    private sealed record Allocation(Guid Id);
    private sealed record Transition(string Status);
}
