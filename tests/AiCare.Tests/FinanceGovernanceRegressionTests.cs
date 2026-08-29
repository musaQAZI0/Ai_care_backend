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
public sealed class FinanceGovernanceRegressionTests(PostgresRegressionFactory factory):IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task FinanceBatchesPaymentsFundingReconciliationAndDashboardAreAudited()
    {
        await factory.EnsureClinicalSeedAsync();
        var personId=Guid.NewGuid();var workerId=Guid.NewGuid();var visitOne=Guid.NewGuid();var visitTwo=Guid.NewGuid();var financeName=$"finance.backoffice.{Guid.NewGuid():N}";var workerName=$"finance.worker.{Guid.NewGuid():N}";
        var start=new DateTimeOffset(2032,3,1,9,0,0,TimeSpan.Zero);var end=start.AddDays(7);
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.CareWorkers.Add(new CareWorker(workerId,"Finance Worker","Personal care","Flexible",0,0,"Valid","Compliant","10 miles",TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            db.ServiceUsers.Add(new ServiceUser(personId,"Finance Person",new DateOnly(1950,6,6),"+10000000006","Personal care","Contact","",RiskLevel.Low,"Active","Address","None","None","Local Authority","","","Independent","Independent","Verbal","Routine","Standard",TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            db.Visits.AddRange(new Visit(visitOne,personId,workerId,start,"Morning care",60,"Personal care",VisitStatus.Completed,null,null,null,null,null,null,TenantDefaults.OrganizationId,TenantDefaults.BranchId),new Visit(visitTwo,personId,workerId,start.AddDays(1),"Evening care",30,"Personal care",VisitStatus.Completed,null,null,null,null,null,null,TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            db.AppUsers.AddRange(User(financeName,UserRole.BackOffice,null),User(workerName,UserRole.CareWorker,workerId));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("insert into funding_arrangements(id,service_user_id,organization_id,branch_id,funding_source,funder_name,contract_reference,care_package_type,authorized_hours_per_week,hourly_rate,valid_from,valid_to,status,notes,created_at,updated_at) values({0},{1},{2},{3},'Local Authority','Council','FIN-1','Domiciliary care',1.00,30.00,{4},null,'Active','Regression funding',now(),now())",Guid.NewGuid(),personId,TenantDefaults.OrganizationId,TenantDefaults.BranchId,start.UtcDateTime);
        }

        var finance=await Client(financeName);var worker=await Client(workerName);
        Assert.Equal(HttpStatusCode.Forbidden,(await worker.GetAsync("/api/phase1/finance/dashboard")).StatusCode);

        var invoiceBatch=await finance.PostAsJsonAsync("/api/phase1/finance/invoice-batches",new{periodStart=start,periodEnd=end,defaultHourlyRate=25m,mileageRate=0m});
        Assert.Equal(HttpStatusCode.Created,invoiceBatch.StatusCode);
        var invoicePayload=(await invoiceBatch.Content.ReadFromJsonAsync<InvoiceBatch>())!;
        Assert.True(invoicePayload.Count >= 1);
        var generatedInvoice=invoicePayload.Invoices.Single(x=>x.ServiceUserId==personId);
        var invoiceId=generatedInvoice.Id;
        Assert.Equal(45m,generatedInvoice.Amount);

        var lines=await finance.GetFromJsonAsync<List<InvoiceLine>>($"/api/phase1/finance/invoices/{invoiceId}/lines");
        Assert.Equal(2,lines!.Count);
        Assert.Equal(45m,lines.Sum(x=>x.Amount));

        var payment=await finance.PostAsJsonAsync($"/api/phase1/finance/invoices/{invoiceId}/payments",new{amount=45m,reference="PAY-FIN-1",receivedAt=(DateTimeOffset?)null});
        Assert.Equal(HttpStatusCode.Created,payment.StatusCode);
        Assert.Contains("Paid",await payment.Content.ReadAsStringAsync());

        var payroll=await finance.PostAsJsonAsync("/api/phase1/finance/payroll-batches",new{periodStart=start,periodEnd=end,defaultHourlyRate=18m,mileageRate=2m});
        Assert.Equal(HttpStatusCode.Created,payroll.StatusCode);
        var payrollRun=(await payroll.Content.ReadFromJsonAsync<PayrollRunRow>())!;
        Assert.True(payrollRun.GrossPay >= 31m);
        var payrollLines=await finance.GetFromJsonAsync<List<PayrollLine>>($"/api/phase1/finance/payroll-runs/{payrollRun.Id}/lines");
        var seededPayrollLines=payrollLines!.Where(x=>x.CareWorkerId==workerId).ToList();
        Assert.Equal(2,seededPayrollLines.Count);
        Assert.Equal(31m,seededPayrollLines.Sum(x=>x.GrossPay));

        var reconciliation=await finance.PostAsJsonAsync("/api/phase1/finance/funding-reconciliations",new{serviceUserId=personId,periodStart=start,periodEnd=end,allowedVarianceHours=0.1m,notes="Regression reconciliation"});
        Assert.Equal(HttpStatusCode.Created,reconciliation.StatusCode);
        Assert.Contains("varianceHours",await reconciliation.Content.ReadAsStringAsync());
        var dashboard=await finance.GetStringAsync("/api/phase1/finance/dashboard");
        Assert.Contains("paymentsReceived",dashboard);Assert.Contains("payrollTotal",dashboard);

        using var verify=factory.Services.CreateScope();var verifyDb=verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="finance.invoice_batch_generated"));
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="finance.payment_recorded"));
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="finance.payroll_batch_generated"));
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="finance.funding_reconciled"));
    }

    private static AppUser User(string name,UserRole role,Guid? worker)=>new(Guid.NewGuid(),name,$"{name}@aicare.local",PasswordHasher.HashPassword("Admin123!"),role,true,TenantDefaults.OrganizationId,TenantDefaults.BranchId,worker,null);
    private async Task<HttpClient> Client(string name){var client=factory.CreateClient();var response=await client.PostAsJsonAsync("/api/auth/login",new{userName=name,password="Admin123!",mfaCode=(string?)null});response.EnsureSuccessStatusCode();client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await response.Content.ReadFromJsonAsync<Login>())!.Token);return client;}
    private sealed record Login(string Token);private sealed record InvoiceBatch(int Count,List<InvoiceRow> Invoices);private sealed record InvoiceRow(Guid Id,Guid ServiceUserId,decimal Amount);private sealed record InvoiceLine(decimal Amount);private sealed record PayrollRunRow(Guid Id,decimal GrossPay);private sealed record PayrollLine(Guid Id,Guid CareWorkerId,decimal GrossPay);
}





