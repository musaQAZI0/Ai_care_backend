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
public sealed class EmarRecoveryReviewRegressionTests(PostgresRegressionFactory factory):IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task RecoveryRequiresManagerDecisionPreservesStockAndIsAuditedIdempotentAndExportable()
    {
        await factory.EnsureClinicalSeedAsync();
        var workerName=$"recovery.worker.{Guid.NewGuid():N}";
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.AppUsers.Add(new AppUser(Guid.NewGuid(),workerName,$"{workerName}@aicare.local",PasswordHasher.HashPassword("Worker123!"),UserRole.CareWorker,true,TenantDefaults.OrganizationId,TenantDefaults.BranchId,RegressionIds.WorkerId,null));
            await db.SaveChangesAsync();
        }
        using var worker=await Login(workerName,"Worker123!");using var admin=await Login("admin","Admin123!");
        var request=new {retryKey=$"recovery-{Guid.NewGuid():N}",submission=new {outcome="Omitted",occurredAt=DateTimeOffset.UtcNow,doseQuantity=0m,reasonCode="NotAvailable",reasonDetail="Synthetic outage evidence",prnIndication="",witnessUserId=(Guid?)null,overrideReason=""}};
        var url=$"/api/phase1/emar-safety/mar/{RegressionIds.MarId}/recovery-review";
        var first=await worker.PostAsJsonAsync(url,request);Assert.Equal(HttpStatusCode.OK,first.StatusCode);
        var result=await first.Content.ReadFromJsonAsync<JsonElement>();var id=result.GetProperty("id").GetGuid();
        Assert.Equal("Pending",result.GetProperty("status").GetString());
        var repeat=await worker.PostAsJsonAsync(url,request);Assert.Equal(id,(await repeat.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden,(await worker.GetAsync("/api/phase1/emar-safety/recovery-reviews")).StatusCode);
        var decisionUrl=$"/api/phase1/emar-safety/recovery-reviews/{id}/resolve";
        await StepUpTestGrants.GrantAsync(factory,admin,"medication");
        var invalid=await admin.PostAsJsonAsync(decisionUrl,new {resolution="AlreadyRecorded",evidence="Reviewed",terminalLedgerId=Guid.NewGuid()});
        Assert.Equal(HttpStatusCode.Conflict,invalid.StatusCode);
        await StepUpTestGrants.GrantAsync(factory,admin,"medication");
        var decision=new {resolution="NotApplied",evidence="Paper MAR and authoritative ledger checked; submission was not applied",terminalLedgerId=(Guid?)null};
        Assert.Equal(HttpStatusCode.OK,(await admin.PostAsJsonAsync(decisionUrl,decision)).StatusCode);
        await StepUpTestGrants.GrantAsync(factory,admin,"medication");
        Assert.Equal(HttpStatusCode.OK,(await admin.PostAsJsonAsync(decisionUrl,decision)).StatusCode);
        result=await (await worker.PostAsJsonAsync(url,request)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Resolved",result.GetProperty("status").GetString());
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            Assert.Equal("Scheduled",(await db.MedicationAdministrationRecords.SingleAsync(x=>x.Id==RegressionIds.MarId)).Outcome);
            var count=await db.Database.SqlQueryRaw<int>("select count(*)::int as \"Value\" from emar_ledger where corrects_ledger_id={0} and event_type='RecoveryDecision'",id).SingleAsync();
            Assert.Equal(1,count);
            Assert.True(await db.AuditEvents.AnyAsync(x=>x.Action=="emar.recovery_review_resolved"&&x.EntityId==RegressionIds.MarId));
        }
        var from=Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-1).ToString("O"));var to=Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("O"));
        var export=$"/api/phase1/emar-safety/people/{RegressionIds.ServiceUserId}/timeline.json?from={from}&to={to}";
        Assert.Equal(HttpStatusCode.Forbidden,(await worker.GetAsync(export)).StatusCode);
        var document=await admin.GetAsync(export);Assert.Equal(HttpStatusCode.OK,document.StatusCode);
        var timeline=await document.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(timeline.GetProperty("entries").EnumerateArray(),x=>x.GetProperty("event_type").GetString()=="RecoveryDecision");
    }

    [Fact]
    public async Task PhysicalStockCountIsIdempotentRaisesOneDiscrepancyAndDoesNotRewriteTheBalance()
    {
        await factory.EnsureClinicalSeedAsync();var medication=Guid.NewGuid();
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Medications.Add(new Medication(medication,RegressionIds.ServiceUserId,"Count regression medicine","Approved dose","Oral","Morning",false,"Pharmacy","None",TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("insert into medication_safety_profiles(medication_id,organization_id,branch_id,stock_on_hand) values({0},{1},{2},10)",medication,TenantDefaults.OrganizationId,TenantDefaults.BranchId);
        }
        using var admin=await Login("admin","Admin123!");
        var url=$"/api/phase1/emar-safety/medications/{medication}/stock-count";
        var key=$"stock-count-{Guid.NewGuid():N}";admin.DefaultRequestHeaders.Add("Idempotency-Key",key);
        var body=new {expectedBalance=10m,countedBalance=8m,reference="COUNT-1",reason="Synthetic physical count; investigate discrepancy"};
        await StepUpTestGrants.GrantAsync(factory,admin,"medication");
        var first=await admin.PostAsJsonAsync(url,body);Assert.Equal(HttpStatusCode.OK,first.StatusCode);
        var id=(await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await StepUpTestGrants.GrantAsync(factory,admin,"medication");
        var repeat=await admin.PostAsJsonAsync(url,body);Assert.Equal(HttpStatusCode.OK,repeat.StatusCode);
        Assert.Equal(id,(await repeat.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        await StepUpTestGrants.GrantAsync(factory,admin,"medication");
        var changed=await admin.PostAsJsonAsync(url,new {expectedBalance=10m,countedBalance=9m,reference="COUNT-1",reason=body.reason});
        Assert.Equal(HttpStatusCode.Conflict,changed.StatusCode);
        var alerts=await admin.GetFromJsonAsync<JsonElement[]>($"/api/phase1/emar-safety/medications/{medication}/alerts");
        Assert.Single(alerts!,x=>x.GetProperty("alertType").GetString()=="StockDiscrepancy");
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            Assert.Equal(10m,await db.Database.SqlQueryRaw<decimal>("select stock_on_hand as \"Value\" from medication_safety_profiles where medication_id={0}",medication).SingleAsync());
            Assert.Equal(1,await db.Database.SqlQueryRaw<int>("select count(*)::int as \"Value\" from medication_stock_transactions where medication_id={0}",medication).SingleAsync());
        }
    }

    [Fact]
    public async Task WitnessMustUseTheDesignatedIndependentAccountAndDecisionsAreImmutable()
    {
        await factory.EnsureClinicalSeedAsync();
        var operatorName=$"witness.operator.{Guid.NewGuid():N}";var witnessName=$"witness.signer.{Guid.NewGuid():N}";
        var witnessWorker=Guid.NewGuid();var witnessId=Guid.NewGuid();
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.CareWorkers.Add(new CareWorker(witnessWorker,"Independent witness","Medication administration","Available",0,0,"Valid","Compliant","10",TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            db.AppUsers.AddRange(new AppUser(Guid.NewGuid(),operatorName,$"{operatorName}@aicare.local",PasswordHasher.HashPassword("Worker123!"),UserRole.CareWorker,true,TenantDefaults.OrganizationId,TenantDefaults.BranchId,RegressionIds.WorkerId,null),
                new AppUser(witnessId,witnessName,$"{witnessName}@aicare.local",PasswordHasher.HashPassword("Worker123!"),UserRole.CareWorker,true,TenantDefaults.OrganizationId,TenantDefaults.BranchId,witnessWorker,null));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("insert into worker_competency_records(id,care_worker_id,organization_id,branch_id,competency,status,assessed_by,assessed_at) values({0},{1},{2},{3},'Medication administration','Valid','Regression',now())",Guid.NewGuid(),witnessWorker,TenantDefaults.OrganizationId,TenantDefaults.BranchId);
        }
        using var actor=await Login(operatorName,"Worker123!");using var witness=await Login(witnessName,"Worker123!");
        var packet=new {outcome="Administered",occurredAt=DateTimeOffset.UtcNow,doseQuantity=1m,reasonCode="",reasonDetail="",prnIndication="Synthetic indication",witnessUserId=witnessId,overrideReason=""};
        await StepUpTestGrants.GrantAsync(factory,actor,"medication");
        var requested=await actor.PostAsJsonAsync($"/api/phase1/emar-safety/mar/{RegressionIds.MarId}/witness-request",packet);
        Assert.Equal(HttpStatusCode.OK,requested.StatusCode);var id=(await requested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var url=$"/api/phase1/emar-safety/witness-requests/{id}/attest";
        var approval=new {decision="Approved",evidence="Independent prescription and person check recorded",independentlyChecked=true};
        await StepUpTestGrants.GrantAsync(factory,actor,"medication");
        Assert.Equal(HttpStatusCode.NotFound,(await actor.PostAsJsonAsync(url,approval)).StatusCode);
        await StepUpTestGrants.GrantAsync(factory,witness,"medication");
        Assert.Equal(HttpStatusCode.OK,(await witness.PostAsJsonAsync(url,approval)).StatusCode);
        await StepUpTestGrants.GrantAsync(factory,witness,"medication");
        Assert.Equal(HttpStatusCode.OK,(await witness.PostAsJsonAsync(url,approval)).StatusCode);
        await StepUpTestGrants.GrantAsync(factory,witness,"medication");
        Assert.Equal(HttpStatusCode.Conflict,(await witness.PostAsJsonAsync(url,new {decision="Rejected",evidence="Changed decision",independentlyChecked=false})).StatusCode);
    }

    private async Task<HttpClient> Login(string name,string password)
    {
        var client=factory.CreateClient();var response=await client.PostAsJsonAsync("/api/auth/login",new {userName=name,password,mfaCode=(string?)null});response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString());return client;
    }
}
