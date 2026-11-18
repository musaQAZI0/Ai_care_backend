using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AiCare.Api;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace AiCare.Tests;
[Collection("Postgres regression")]
public sealed class VisitAttendanceRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task AttendanceEvidenceAndAuditAreSavedOnceAndInvalidTransitionsAreRejected()
    {
        await factory.EnsureClinicalSeedAsync();
        var id = Guid.NewGuid();
        using(var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Visits.Add(new Visit(id, RegressionIds.ServiceUserId, RegressionIds.WorkerId, DateTimeOffset.UtcNow,
                "Synthetic attendance reconciliation", 30, "Personal care", VisitStatus.Scheduled,
                null, null, null, null, null, null, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<Login>())!.Token);
        var url = $"/api/phase1/visits/{id}/delivery/attendance";
        var request = new RecordVisitLocationRequest("CheckIn", 51.5m, -0.1m, 10, DateTimeOffset.UtcNow, "Synthetic device", "");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url, request with { EventType = "CheckOut" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, request with { AccuracyMeters = null })).StatusCode);
        var submissions = await Task.WhenAll(client.PostAsJsonAsync(url, request), client.PostAsJsonAsync(url, request));
        foreach (var response in submissions)
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        DateTimeOffset? original;
        using(var scope = factory.Services.CreateScope())
            original = (await scope.ServiceProvider.GetRequiredService<CareDbContext>().Visits.SingleAsync(x => x.Id == id)).CheckedInAt;
        var duplicate = await client.PostAsJsonAsync(url, request);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Contains("\"replayed\":true", await duplicate.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(url, request with { EventType = "CheckOut" })).StatusCode);
        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        var visit = await verifyDb.Visits.SingleAsync(x => x.Id == id);
        Assert.Equal(original, visit.CheckedInAt);
        Assert.Equal(VisitStatus.Completed, visit.Status);
        Assert.Equal(1, await verifyDb.AuditEvents.CountAsync(x => x.EntityId == id && x.Action == "visit.checked_in"));
        Assert.Equal(1, await verifyDb.AuditEvents.CountAsync(x => x.EntityId == id && x.Action == "visit.checked_out"));
        Assert.Equal(2, await verifyDb.Database.SqlQueryRaw<int>("select count(*)::int as \"Value\" from visit_location_events where visit_id={0}", id).SingleAsync());
    }
    private sealed record Login(string Token);
}
