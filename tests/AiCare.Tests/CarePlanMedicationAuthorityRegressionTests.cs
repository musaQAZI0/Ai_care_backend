using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed class CarePlanMedicationAuthorityRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task DraftAuthorityRequiresCorrectPersonAndRevisionAndFreezesOnReview()
    {
        await factory.EnsureClinicalSeedAsync();
        var personId = Guid.NewGuid();
        var medicationId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.ServiceUsers.Add(new ServiceUser(personId, "Authority Person", new DateOnly(1950, 1, 1),
                "+10000000099", "Medication support", "Contact", "", RiskLevel.Medium, "Active",
                "Address", "None", "None", "Private", "Regression", "", "Independent",
                "Independent", "Verbal", "None", "Standard", TenantDefaults.OrganizationId,
                TenantDefaults.BranchId));
            db.Medications.Add(new Medication(medicationId, personId, "Authority medicine", "1 tablet",
                "Oral", "Morning", false, "Pharmacy", "None", TenantDefaults.OrganizationId,
                TenantDefaults.BranchId));
            db.CarePlans.Add(new CarePlan(planId, personId, "v1", "Draft", "Personal care",
                "Administer medicine", "Mobility", "Nutrition", DateTimeOffset.UtcNow.AddMonths(6),
                TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await login.Content.ReadFromJsonAsync<Login>())!.Token);
        var request = new
        {
            expectedRevision = 1, supportType = "Administer", instructions = "Give with food",
            reviewDueAt = DateTimeOffset.UtcNow.AddMonths(3), familyMedicineApplies = true,
            familyRecordingMethod = "Family care-plan note", familyResponsiblePerson = "Daughter",
            familyEscalationRoute = "Medication lead", priorDoseCheck = "Check MAR and family note"
        };
        var wrongMedication = await client.PutAsJsonAsync($"/api/phase1/care-plans/{planId}/medication-authorities/{RegressionIds.MedicationId}", request);
        Assert.Equal(HttpStatusCode.NotFound, wrongMedication.StatusCode);

        var saved = await client.PutAsJsonAsync($"/api/phase1/care-plans/{planId}/medication-authorities/{medicationId}", request);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var list = await client.GetStringAsync($"/api/phase1/care-plans/{planId}/medication-authorities");
        Assert.Contains("Family care-plan note", list);
        Assert.Contains("Administer", list);
        var stale = await client.PutAsJsonAsync($"/api/phase1/care-plans/{planId}/medication-authorities/{medicationId}", request);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                update care_plan_versions set status='InReview' where care_plan_id={planId}
                """);
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                update care_plan_medication_authorities set instructions='Changed after review'
                where care_plan_id={planId} and medication_id={medicationId}
                """));
        }
        var inReview = await client.PutAsJsonAsync($"/api/phase1/care-plans/{planId}/medication-authorities/{medicationId}", new
        {
            expectedRevision = 2, request.supportType, request.instructions, request.reviewDueAt,
            request.familyMedicineApplies, request.familyRecordingMethod,
            request.familyResponsiblePerson, request.familyEscalationRoute, request.priorDoseCheck
        });
        Assert.Equal(HttpStatusCode.Conflict, inReview.StatusCode);
    }

    private sealed record Login(string Token);
}
