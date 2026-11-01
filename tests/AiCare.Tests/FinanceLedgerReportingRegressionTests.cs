using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed class FinanceLedgerReportingRegressionTests(PostgresRegressionFactory factory):IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task DashboardAndAgeingSeparateCurrenciesReconcileCreditsRefundsAndExcludeDraftDebt()
    {
        await factory.EnsureClinicalSeedAsync();var org=Guid.NewGuid();var branch=Guid.NewGuid();var person=Guid.NewGuid();var invoice=Guid.NewGuid();var euro=Guid.NewGuid();var draft=Guid.NewGuid();var name=$"ledger.{Guid.NewGuid():N}";
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();db.Organizations.Add(new Organization(org,"Ledger test","Trial","Active"));db.Branches.Add(new Branch(branch,org,"Main","Test","Active"));
            db.ServiceUsers.Add(new ServiceUser(person,"Ledger recipient",new DateOnly(1950,1,1),"1","Care","Contact","",RiskLevel.Low,"Active","Address","None","None","Private","","","Independent","Independent","Verbal","Routine","Standard",org,branch));
            db.AppUsers.Add(new AppUser(Guid.NewGuid(),name,name+"@test.invalid",PasswordHasher.HashPassword("Admin123!"),UserRole.BackOffice,true,org,branch));
            db.Invoices.AddRange(new Invoice(invoice,person,"Payer",100,"Issued",DateTimeOffset.UtcNow,org,branch),new Invoice(euro,person,"Payer",200,"Issued",DateTimeOffset.UtcNow,org,branch),new Invoice(draft,person,"Payer",999,"Reviewed",DateTimeOffset.UtcNow,org,branch));await db.SaveChangesAsync();
            foreach(var item in new[]{(invoice,"GBP",100m),(euro,"EUR",200m),(draft,"GBP",999m)})
                await db.Database.ExecuteSqlRawAsync("""
                  insert into finance_invoice_details(invoice_id,organization_id,branch_id,invoice_number,invoice_date,due_date,payment_terms,provider_name,customer_name,currency,net_amount,vat_rate,vat_amount,gross_amount)
                  values({0},{1},{2},{3},current_date,current_date+30,'30 days','Provider','Payer',{4},{5},0,0,{5})
                  """,item.Item1,org,branch,$"LEDGER-{item.Item1}",item.Item2,item.Item3);
            var receipt=Guid.NewGuid();
            await db.Database.ExecuteSqlRawAsync("""
              insert into finance_receipts(id,organization_id,branch_id,amount,currency,reference,received_at,received_by) values({0},{1},{2},40,'GBP','BANK',now(),'test');
              insert into finance_payment_allocations(id,receipt_id,invoice_id,organization_id,branch_id,amount,allocated_by) values(gen_random_uuid(),{0},{3},{1},{2},40,'test');
              insert into finance_payments(id,invoice_id,organization_id,branch_id,amount,reference,received_at,received_by) values(gen_random_uuid(),{3},{1},{2},20,'DIRECT',now(),'test');
              """,receipt,org,branch,invoice);
        }
        var client=factory.CreateClient();var login=await client.PostAsJsonAsync("/api/auth/login",new{userName=name,password="Admin123!",mfaCode=(string?)null});login.EnsureSuccessStatusCode();client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString());
        (await ControlledAdjustmentTestData.Post(factory,client,invoice,"Credit",10m,"CREDIT","Correction")).EnsureSuccessStatusCode();
        (await ControlledAdjustmentTestData.Post(factory,client,invoice,"Refund",5m,"REFUND","Return")).EnsureSuccessStatusCode();
        using var dashboard=JsonDocument.Parse(await client.GetStringAsync("/api/phase1/finance/dashboard"));
        Assert.Equal(JsonValueKind.Null,dashboard.RootElement.GetProperty("invoiceTotal").ValueKind);
        var currencies=dashboard.RootElement.GetProperty("currencyTotals").EnumerateArray().ToList();Assert.Equal(2,currencies.Count);
        var gbp=currencies.Single(x=>x.GetProperty("currency").GetString()=="GBP");Assert.Equal(100,gbp.GetProperty("invoiceTotal").GetDecimal());Assert.Equal(55,gbp.GetProperty("paymentsReceived").GetDecimal());Assert.Equal(35,gbp.GetProperty("outstanding").GetDecimal());
        using var ageing=JsonDocument.Parse(await client.GetStringAsync("/api/phase1/finance/aged-receivables"));Assert.Equal(JsonValueKind.Null,ageing.RootElement.GetProperty("totals").ValueKind);
        var debts=ageing.RootElement.GetProperty("invoices").EnumerateArray().ToList();Assert.Equal(2,debts.Count);Assert.DoesNotContain(debts,x=>x.GetProperty("id").GetGuid()==draft);
        Assert.Equal(35,debts.Single(x=>x.GetProperty("id").GetGuid()==invoice).GetProperty("balance").GetDecimal());
        using var report=JsonDocument.Parse(await client.GetStringAsync("/api/phase1/finance/reports/operational-performance?from=2050-01-01&to=2050-01-31"));
        Assert.Equal(JsonValueKind.Null,report.RootElement.GetProperty("contribution").ValueKind);
        Assert.Contains("MISSING_SERVICE_LINEAGE",report.RootElement.GetProperty("attributionExceptions").ToString());
        var anonymous=factory.CreateClient();Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.GetAsync("/api/phase1/finance/dashboard")).StatusCode);
    }
}
