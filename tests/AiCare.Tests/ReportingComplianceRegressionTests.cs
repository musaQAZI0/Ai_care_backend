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
public sealed class ReportingComplianceRegressionTests(PostgresRegressionFactory factory):IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task GovernedReportsEvidencePacksActionsAndDashboardAreAudited()
    {
        await factory.EnsureClinicalSeedAsync();
        var adminName=$"report.admin.{Guid.NewGuid():N}";var workerName=$"report.worker.{Guid.NewGuid():N}";var personId=Guid.NewGuid();var workerId=Guid.NewGuid();var visitId=Guid.NewGuid();
        using(var scope=factory.Services.CreateScope())
        {
            var seedDb=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            seedDb.CareWorkers.Add(new CareWorker(workerId,"Reporting Worker","Personal care","Flexible",0,0,"Valid","Compliant","10 miles",TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            seedDb.ServiceUsers.Add(new ServiceUser(personId,"Reporting Person",new DateOnly(1948,7,7),"+10000000007","Care","Contact","",RiskLevel.Low,"Active","Address","None","None","Private","","","Independent","Independent","Verbal","Routine","Standard",TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            seedDb.Visits.Add(new Visit(visitId,personId,workerId,DateTimeOffset.UtcNow.AddDays(-1),"Reporting visit",30,"Personal care",VisitStatus.Completed,null,null,null,null,null,null,TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            seedDb.Invoices.Add(new Invoice(Guid.NewGuid(),personId,"Private",55m,"Approved",DateTimeOffset.UtcNow,TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            seedDb.AppUsers.AddRange(User(adminName,UserRole.CareManager,null),User(workerName,UserRole.CareWorker,workerId));
            await seedDb.SaveChangesAsync();
        }

        var admin=await Client(adminName);var worker=await Client(workerName);
        Assert.Equal(HttpStatusCode.Forbidden,(await worker.GetAsync("/api/phase1/reporting-compliance/dashboard")).StatusCode);

        var report=await admin.PostAsJsonAsync("/api/phase1/reporting-compliance/report-runs",new{name="Governed operations report",category="Operational",format="CSV",metrics=new[]{"Service users","Completed visits","Open incidents","Invoice total","Audit events"},filters=new Dictionary<string,string>{{"period","Last 7 days"}}});
        Assert.Equal(HttpStatusCode.Created,report.StatusCode);
        var reportId=(await report.Content.ReadFromJsonAsync<Created>())!.Id;
        var csv=await admin.GetAsync($"/api/phase1/reporting-compliance/report-runs/{reportId}/csv");
        Assert.Equal(HttpStatusCode.OK,csv.StatusCode);
        Assert.Contains("completed visits",await csv.Content.ReadAsStringAsync(),StringComparison.OrdinalIgnoreCase);

        var evidence=await admin.PostAsJsonAsync("/api/phase1/reporting-compliance/evidence",new{domain="Safe",requirement="Medication governance evidence",evidenceType="Report",evidenceReference="MAR-EXPORT-1",status="Ready",owner="Quality lead",reviewDueAt=DateTimeOffset.UtcNow.AddDays(-1),notes="Regression evidence"});
        Assert.Equal(HttpStatusCode.Created,evidence.StatusCode);
        var evidenceId=(await evidence.Content.ReadFromJsonAsync<Created>())!.Id;
        var action=await admin.PostAsJsonAsync("/api/phase1/reporting-compliance/actions",new{evidenceId,actionType="Evidence review",detail="Review expired evidence pack item",owner="Quality lead",dueAt=DateTimeOffset.UtcNow.AddDays(-1)});
        Assert.Equal(HttpStatusCode.Created,action.StatusCode);
        var actionId=(await action.Content.ReadFromJsonAsync<Created>())!.Id;
        Assert.Equal(HttpStatusCode.NoContent,(await admin.PatchAsJsonAsync($"/api/phase1/reporting-compliance/actions/{actionId}",new{outcome="Evidence reviewed and retained"})).StatusCode);

        var pack=await admin.GetAsync("/api/phase1/reporting-compliance/evidence-pack.csv");
        Assert.Equal(HttpStatusCode.OK,pack.StatusCode);
        Assert.Contains("Medication governance evidence",await pack.Content.ReadAsStringAsync());
        var dashboard=await admin.GetStringAsync("/api/phase1/reporting-compliance/dashboard");
        Assert.Contains("generatedReports",dashboard);Assert.Contains("openEvidence",dashboard);

        using var verify=factory.Services.CreateScope();var verifyDb=verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="report_run.generated"&&x.EntityId==reportId));
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="report_run.exported"&&x.EntityId==reportId));
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="compliance_evidence.created"&&x.EntityId==evidenceId));
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="compliance_action.completed"&&x.EntityId==actionId));
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="compliance_evidence_pack.exported"));
    }

    private static AppUser User(string name,UserRole role,Guid? worker)=>new(Guid.NewGuid(),name,$"{name}@aicare.local",PasswordHasher.HashPassword("Admin123!"),role,true,TenantDefaults.OrganizationId,TenantDefaults.BranchId,worker,null);
    private async Task<HttpClient> Client(string name){var client=factory.CreateClient();var response=await client.PostAsJsonAsync("/api/auth/login",new{userName=name,password="Admin123!",mfaCode=(string?)null});response.EnsureSuccessStatusCode();client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await response.Content.ReadFromJsonAsync<Login>())!.Token);return client;}
    private sealed record Login(string Token);private sealed record Created(Guid Id);
}
