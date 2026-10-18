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
public sealed class ClientAndAppointmentSettingsRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task ConfiguredClientLeavingReasonControlsDischargeAndIsRecorded()
    {
        await factory.EnsureClinicalSeedAsync();
        var personId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.ServiceUsers.Add(Person(personId));
            await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient();
        await Authenticate(client);
        var reason = $"Completed package {Guid.NewGuid():N}";
        var added = await client.PostAsJsonAsync("/api/phase1/care-operations/options", new { kind = "ClientLeavingReason", value = reason, active = true });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var optionId = (await added.Content.ReadFromJsonAsync<CreatedId>())!.Id;
        try
        {
            var path = $"/api/phase1/person-lifecycle/service-users/{personId}/status";
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { status = "Discharged", reason = "Other" })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(path, new { status = "Discharged", reason })).StatusCode);
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            Assert.Equal("Discharged", (await db.ServiceUsers.SingleAsync(item => item.Id == personId)).Status);
            Assert.Contains(reason, await client.GetStringAsync($"/api/phase1/person-lifecycle/service-users/{personId}"));
        }
        finally
        {
            await client.PutAsJsonAsync($"/api/phase1/care-operations/options/{optionId}", new { kind = "ClientLeavingReason", value = reason, active = false });
        }
    }

    [Fact]
    public async Task StartVarianceSettingBlocksRescheduleOutsideConfiguredWindow()
    {
        await factory.EnsureClinicalSeedAsync();
        var personId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var visitId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddDays(40);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.ServiceUsers.Add(Person(personId));
            db.CareWorkers.Add(new CareWorker(workerId, "Variance worker", "Care", "Flexible", 0, 0, "Valid", "Compliant", "Local", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.Visits.Add(new Visit(visitId, personId, workerId, start, "Care", 30, "", VisitStatus.Scheduled, null, null, null, null, null, null, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient();
        await Authenticate(client);
        const string policyPath = "/api/phase1/admin/appointment-start-variance";
        var previous = await client.GetFromJsonAsync<AppointmentPolicy>(policyPath);
        try
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(policyPath, new { maxChangeMinutes = 15 })).StatusCode);
            Assert.Contains("15", await client.GetStringAsync(policyPath));
            var change = new { serviceUserId = personId, careWorkerId = workerId, startsAt = start.AddHours(1), visitType = "Care", durationMinutes = 30, requiredSkills = "", changeReason = "Family requested later visit" };
            var response = await client.PutAsJsonAsync($"/api/phase1/visits/{visitId}", change);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("15 minutes", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await client.PutAsJsonAsync(policyPath, new { maxChangeMinutes = previous?.MaxChangeMinutes });
        }
    }

    private static ServiceUser Person(Guid id) => new(id, "Settings regression client", new DateOnly(1950, 1, 1), "+10000000000", "Care", "Contact", "", RiskLevel.Low, "Active", "Address", "None", "None", "Private", "", "", "Independent", "Independent", "Verbal", "None", "Standard", TenantDefaults.OrganizationId, TenantDefaults.BranchId);

    private static async Task Authenticate(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "Admin123!", mfaCode = (string?)null });
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<Login>())!.Token);
    }

    private sealed record Login(string Token);
    private sealed record CreatedId(Guid Id);
    private sealed record AppointmentPolicy(int? MaxChangeMinutes);
}
