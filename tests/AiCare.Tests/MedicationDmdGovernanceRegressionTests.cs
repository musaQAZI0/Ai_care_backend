using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed class MedicationDmdGovernanceRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task DmdCreateEditValidationFailureAndTenantBoundariesAreGoverned()
    {
        await factory.EnsureClinicalSeedAsync();
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/phase1/medication-terminology/search?q=paracetamol")).StatusCode);

        var admin = await Login("admin");
        var valid = Request("123456", "Paracetamol 500mg tablets");
        var createdResponse = await admin.PostAsJsonAsync("/api/phase1/medications", valid);
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = (await createdResponse.Content.ReadFromJsonAsync<MedicationDto>())!;
        Assert.Equal("123456", created.DmdCode);
        Assert.Equal("Paracetamol 500mg tablets", created.DmdDisplay);
        Assert.Equal("https://dmd.nhs.uk", created.DmdSystem);

        var editedResponse = await admin.PutAsJsonAsync("/api/phase1/medications/" + created.Id, Request("654321", "Amoxicillin 250mg capsules") with { Dosage = "250mg" });
        Assert.True(editedResponse.StatusCode == HttpStatusCode.OK, $"Expected OK but received {editedResponse.StatusCode}: {await editedResponse.Content.ReadAsStringAsync()}");
        var edited = (await editedResponse.Content.ReadFromJsonAsync<MedicationDto>())!;
        Assert.Equal("654321", edited.DmdCode);
        Assert.Equal("250mg", edited.Dosage);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/phase1/medications/" + created.Id, Request("fake", "Invented medicine"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/phase1/medications/" + created.Id, Request("inactive", "Inactive medicine"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/phase1/medications/" + created.Id, Request("654321", "Forged display"))).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await admin.PutAsJsonAsync("/api/phase1/medications/" + created.Id, Request("unavailable", "Unavailable medicine"))).StatusCode);

        var unchanged = (await admin.GetFromJsonAsync<MedicationDto>("/api/phase1/medications/" + created.Id))!;
        Assert.Equal("654321", unchanged.DmdCode);
        Assert.Equal("250mg", unchanged.Dosage);

        Guid foreignMedicationId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            foreignMedicationId = Guid.NewGuid();
            var foreignOrganizationId = Guid.NewGuid();
            var foreignBranchId = Guid.NewGuid();
            db.Organizations.Add(new Organization(foreignOrganizationId, "Foreign provider", "Test", "Active"));
            db.Branches.Add(new Branch(foreignBranchId, foreignOrganizationId, "Foreign branch", "Test", "Active"));
            db.Medications.Add(new Medication(foreignMedicationId, RegressionIds.ServiceUserId, "Foreign medicine", "1", "Oral", "Daily", false, "", "", foreignOrganizationId, foreignBranchId, "123456", "Paracetamol 500mg tablets", "https://dmd.nhs.uk"));
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/phase1/medications/" + foreignMedicationId)).StatusCode);
    }

    private async Task<HttpClient> Login(string userName)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { userName, password = "Admin123!", mfaCode = (string?)null });
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString());
        return client;
    }

    private static MedicationRequest Request(string code, string display) =>
        new(RegressionIds.ServiceUserId, display, "500mg", "Oral", "Morning", false, "Local pharmacy", "None", code, display, "https://dmd.nhs.uk");

    private sealed record MedicationRequest(Guid ServiceUserId, string Name, string Dosage, string Route, string Schedule, bool IsPrn, string Pharmacy, string AllergyWarning, string? DmdCode, string? DmdDisplay, string? DmdSystem);
    private sealed record MedicationDto(Guid Id, string Name, string Dosage, string? DmdCode, string? DmdDisplay, string? DmdSystem);
}

public sealed class RegressionMedicationTerminologyService : IMedicationTerminologyService
{
    public Task<IReadOnlyList<MedicationTerminologyResult>> SearchAsync(string query, int count = 20, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MedicationTerminologyResult>>([
            new("123456", "Paracetamol 500mg tablets", "https://dmd.nhs.uk", false)
        ]);

    public Task<MedicationTerminologyConcept?> LookupAsync(string code, CancellationToken cancellationToken = default) =>
        code switch
        {
            "123456" => Task.FromResult<MedicationTerminologyConcept?>(Concept(code, "Paracetamol 500mg tablets")),
            "654321" => Task.FromResult<MedicationTerminologyConcept?>(Concept(code, "Amoxicillin 250mg capsules")),
            "inactive" => Task.FromResult<MedicationTerminologyConcept?>(Concept(code, "Inactive medicine", true)),
            "unavailable" => throw new HttpRequestException("Simulated terminology outage."),
            _ => Task.FromResult<MedicationTerminologyConcept?>(null)
        };

    private static MedicationTerminologyConcept Concept(string code, string display, bool inactive = false) =>
        new(code, display, "https://dmd.nhs.uk", inactive, new Dictionary<string, string>());
}
