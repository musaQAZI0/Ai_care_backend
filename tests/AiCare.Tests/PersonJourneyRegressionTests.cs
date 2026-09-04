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
public sealed class PersonJourneyRegressionTests(PostgresRegressionFactory factory):IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task AdmissionTransferAndDischargeAreValidatedScopedAndAudited()
    {
        await factory.EnsureClinicalSeedAsync();
        var admittedPerson=Guid.NewGuid();var transferPerson=Guid.NewGuid();var destinationBranch=Guid.NewGuid();
        var adminName=$"journey.admin.{Guid.NewGuid():N}";var managerName=$"journey.manager.{Guid.NewGuid():N}";var workerName=$"journey.worker.{Guid.NewGuid():N}";
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Branches.Add(new Branch(destinationBranch,TenantDefaults.OrganizationId,"Journey destination","North","Active"));
            db.ServiceUsers.AddRange(Person(admittedPerson,"Admission Person","Prospect"),Person(transferPerson,"Transfer Person","Active"));
            db.PersonRecords.AddRange(Record(admittedPerson),Record(transferPerson));
            db.AppUsers.AddRange(User(adminName,UserRole.Administrator,null),User(managerName,UserRole.CareManager,null),User(workerName,UserRole.CareWorker,Guid.NewGuid()));
            await db.SaveChangesAsync();
        }

        var admin=await Client(adminName);var manager=await Client(managerName);var worker=await Client(workerName);
        Assert.Equal(HttpStatusCode.Forbidden,(await worker.GetAsync($"/api/phase1/person-lifecycle/service-users/{admittedPerson}/journey")).StatusCode);
        var referral=await admin.PostAsJsonAsync($"/api/phase1/person-lifecycle/service-users/{admittedPerson}/referrals",new{referralSource="Hospital",referrerName="Nurse",referrerContact="ward@example.test",referralReason="Discharge support",priority="Urgent",screeningDueAt=DateTimeOffset.UtcNow.AddDays(1),owner="Intake"});
        Assert.Equal(HttpStatusCode.Created,referral.StatusCode);var referralId=(await referral.Content.ReadFromJsonAsync<Created>())!.Id;
        Assert.Equal(HttpStatusCode.NoContent,(await admin.PatchAsJsonAsync($"/api/phase1/person-lifecycle/referrals/{referralId}",new{status="Accepted",outcome="Suitable",closureReason="",owner="Intake"})).StatusCode);

        var incomplete=await admin.PostAsJsonAsync($"/api/phase1/person-lifecycle/service-users/{admittedPerson}/admissions",new{referralId,admissionType="Domiciliary",fundingConfirmed=true,initialPlanConfirmed=false,medicationReconciled=true});
        Assert.Equal(HttpStatusCode.BadRequest,incomplete.StatusCode);
        var admission=await admin.PostAsJsonAsync($"/api/phase1/person-lifecycle/service-users/{admittedPerson}/admissions",new{referralId,admissionType="Domiciliary",plannedAt=DateTimeOffset.UtcNow,admittedAt=DateTimeOffset.UtcNow,fundingConfirmed=true,initialPlanConfirmed=true,medicationReconciled=true,notes="Safe start completed"});
        if(admission.StatusCode!=HttpStatusCode.Created)throw new Xunit.Sdk.XunitException(await admission.Content.ReadAsStringAsync());var admissionId=(await admission.Content.ReadFromJsonAsync<Created>())!.Id;
        Assert.Equal(HttpStatusCode.Conflict,(await admin.PostAsJsonAsync($"/api/phase1/person-lifecycle/service-users/{admittedPerson}/admissions",new{referralId,admissionType="Domiciliary",fundingConfirmed=true,initialPlanConfirmed=true,medicationReconciled=true})).StatusCode);

        var unsafeDischarge=await admin.PostAsJsonAsync($"/api/phase1/person-lifecycle/service-users/{admittedPerson}/discharges",new{reason="Package complete",destination="Home",summary="Stable",medicationHandover="MAR supplied",propertyReturned=false,followUpRequired=false,followUpDetails=""});
        Assert.Equal(HttpStatusCode.BadRequest,unsafeDischarge.StatusCode);
        var discharge=await admin.PostAsJsonAsync($"/api/phase1/person-lifecycle/service-users/{admittedPerson}/discharges",new{dischargedAt=DateTimeOffset.UtcNow,reason="Package complete",destination="Home",summary="Stable at discharge",medicationHandover="MAR and medicines supplied",propertyReturned=true,followUpRequired=true,followUpDetails="GP review in seven days"});
        Assert.Equal(HttpStatusCode.Created,discharge.StatusCode);var dischargeId=(await discharge.Content.ReadFromJsonAsync<Created>())!.Id;
        var journey=await admin.GetStringAsync($"/api/phase1/person-lifecycle/service-users/{admittedPerson}/journey");
        Assert.Contains(admissionId.ToString(),journey);Assert.Contains(dischargeId.ToString(),journey);Assert.Contains("GP review in seven days",journey);

        var transfer=await admin.PostAsJsonAsync($"/api/phase1/person-lifecycle/service-users/{transferPerson}/transfers",new{destinationBranchId=destinationBranch,effectiveAt=DateTimeOffset.UtcNow,reason="Move closer to family",handoverSummary="Current risks and care plan reviewed",receivingManager="Destination manager"});
        Assert.Equal(HttpStatusCode.Created,transfer.StatusCode);var transferId=(await transfer.Content.ReadFromJsonAsync<Created>())!.Id;
        Assert.Equal(HttpStatusCode.OK,(await admin.GetAsync($"/api/phase1/person-lifecycle/service-users/{transferPerson}/journey")).StatusCode);Assert.Equal(HttpStatusCode.NotFound,(await manager.GetAsync($"/api/phase1/person-lifecycle/service-users/{transferPerson}/journey")).StatusCode);

        using var verify=factory.Services.CreateScope();var verifyDb=verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal("Discharged",(await verifyDb.ServiceUsers.SingleAsync(x=>x.Id==admittedPerson)).Status);
        Assert.Equal(destinationBranch,(await verifyDb.ServiceUsers.SingleAsync(x=>x.Id==transferPerson)).BranchId);
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="person_admission.created"&&x.EntityId==admissionId));
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="person_discharge.created"&&x.EntityId==dischargeId));
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="person_transfer.completed"&&x.EntityId==transferId));
    }

    private static ServiceUser Person(Guid id,string name,string status)=>new(id,name,new DateOnly(1940,1,1),"+10000000006","Support","Contact","",RiskLevel.Medium,status,"Address","None","None","Private","Regression","","Independent","Independent","Verbal","None","Standard",TenantDefaults.OrganizationId,TenantDefaults.BranchId);
    private static PersonRecord Record(Guid id)=>new(Guid.NewGuid(),id,"","","NHS","GP","Pharmacy","Representative","Active consent","Has capacity","","History","What matters","Remain safe","",null,null,DateTimeOffset.UtcNow,TenantDefaults.OrganizationId,TenantDefaults.BranchId);
    private static AppUser User(string name,UserRole role,Guid? worker)=>new(Guid.NewGuid(),name,$"{name}@aicare.local",PasswordHasher.HashPassword("Admin123!"),role,true,TenantDefaults.OrganizationId,TenantDefaults.BranchId,worker,null);
    private async Task<HttpClient> Client(string name){var client=factory.CreateClient();var response=await client.PostAsJsonAsync("/api/auth/login",new{userName=name,password="Admin123!",mfaCode=(string?)null});response.EnsureSuccessStatusCode();client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await response.Content.ReadFromJsonAsync<Login>())!.Token);return client;}
    private sealed record Login(string Token);private sealed record Created(Guid Id);
}
