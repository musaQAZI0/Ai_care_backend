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

[Collection("Postgres regression")]
public sealed class EmarMonitoringRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task ScanCreatesIdempotentDoseAndMedicationDateAlerts()
    {
        await factory.EnsureClinicalSeedAsync();
        var medicationId=Guid.NewGuid(); var overdueId=Guid.NewGuid(); var upcomingId=Guid.NewGuid();
        using (var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Medications.Add(new Medication(medicationId,RegressionIds.ServiceUserId,"Monitoring medicine",
                "5 mg","Oral","Morning",false,"Pharmacy","None",TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            await db.SaveChangesAsync();
        }
        var admin=factory.CreateClient();
        var login=await admin.PostAsJsonAsync("/api/auth/login",new{userName="admin",password="Admin123!",mfaCode=(string?)null});
        login.EnsureSuccessStatusCode();
        using (var json=JsonDocument.Parse(await login.Content.ReadAsStringAsync()))
            admin.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",json.RootElement.GetProperty("token").GetString());
        await StepUpTestGrants.GrantAsync(factory,admin,"medication");
        var profile=await MedicationVerificationTestHelper.SaveAndVerifyAsync(factory,admin,medicationId,new {
            indication="Regression monitoring",prescriber="Dr Monitoring",form="Tablet",strength="5 mg",
            doseUnit="tablet",frequency="Morning",administrationInstructions="With water",
            startDate=DateTimeOffset.UtcNow.AddDays(-1),endDate=DateTimeOffset.UtcNow.AddDays(3),
            reviewDueAt=DateTimeOffset.UtcNow.AddDays(2),doseWindowMinutes=60,
            prnIndication="",stockOnHand=10m,reorderLevel=2m,requiresWitness=false,
            reconciliationStatus="Verified",sourceType="Prescription",sourceReference="RX-MONITOR",
            changeReason="Initial verification"
        });
        Assert.Equal(HttpStatusCode.OK,profile.StatusCode);
        using (var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.MedicationAdministrationRecords.AddRange(
                new MedicationAdministrationRecord(overdueId,medicationId,RegressionIds.VisitId,RegressionIds.WorkerId,
                    DateTimeOffset.UtcNow.AddHours(-3),null,"Scheduled","",TenantDefaults.OrganizationId,TenantDefaults.BranchId),
                new MedicationAdministrationRecord(upcomingId,medicationId,RegressionIds.VisitId,RegressionIds.WorkerId,
                    DateTimeOffset.UtcNow.AddHours(3),null,"Scheduled","",TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                insert into emar_escalations(id,mar_record_id,ledger_id,organization_id,branch_id,
                  escalation_type,severity,status,message,owner,due_at)
                values({Guid.NewGuid()},{upcomingId},null,{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},
                  'PRNEffectReview','Medium','Open','Effect review due','Medication lead',{DateTimeOffset.UtcNow.AddMinutes(-1)})
                """);
        }
        using (var scope=factory.Services.CreateScope())
        {
            var monitor=scope.ServiceProvider.GetRequiredService<EmarMonitoringService>();
            await monitor.ScanAsync(CancellationToken.None);
            await monitor.ScanAsync(CancellationToken.None);
        }
        using (var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            var connection=db.Database.GetDbConnection(); await connection.OpenAsync();
            await using var command=connection.CreateCommand();
            command.CommandText="""
                select (select count(*) from emar_escalations where mar_record_id=@overdue and escalation_type='OverdueDose'),
                       (select count(*) from emar_escalations where mar_record_id=@upcoming and escalation_type='OverdueDose'),
                       (select count(*) from medication_monitoring_alerts where medication_id=@medication),
                       (select severity from emar_escalations where mar_record_id=@upcoming and escalation_type='PRNEffectReview')
                """;
            var p=command.CreateParameter(); p.ParameterName="overdue"; p.Value=overdueId; command.Parameters.Add(p);
            p=command.CreateParameter(); p.ParameterName="upcoming"; p.Value=upcomingId; command.Parameters.Add(p);
            p=command.CreateParameter(); p.ParameterName="medication"; p.Value=medicationId; command.Parameters.Add(p);
            await using var reader=await command.ExecuteReaderAsync(); await reader.ReadAsync();
            Assert.Equal(1,reader.GetInt64(0)); Assert.Equal(0,reader.GetInt64(1));
            Assert.Equal(2,reader.GetInt64(2)); Assert.Equal("High",reader.GetString(3));
        }
        var alerts=await admin.GetStringAsync($"/api/phase1/emar-safety/medications/{medicationId}/alerts");
        Assert.Contains("ReviewDue",alerts); Assert.Contains("EndDateApproaching",alerts);
        using var alertJson=JsonDocument.Parse(alerts);
        var alertId=alertJson.RootElement[0].GetProperty("id").GetGuid();
        foreach(var action in new[]{"Acknowledge","Start","Resolve","Reopen"})
        {
            var response=await admin.PostAsJsonAsync($"/api/phase1/emar-safety/medications/{medicationId}/alerts/{alertId}/progress",
                new{action,comment="Medication lead reviewed evidence"});
            Assert.Equal(HttpStatusCode.NoContent,response.StatusCode);
        }
    }
}
