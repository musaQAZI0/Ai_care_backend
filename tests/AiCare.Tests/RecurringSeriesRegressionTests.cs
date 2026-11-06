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
public sealed class RecurringSeriesRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task EditingSeriesUpdatesOnlyItsFutureScheduledVisits()
    {
        await factory.EnsureClinicalSeedAsync();
        var personId = Guid.NewGuid();
        var firstWorkerId = Guid.NewGuid();
        var nextWorkerId = Guid.NewGuid();
        var additionalWorkerId = Guid.NewGuid();
        var managerName = $"series.manager.{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.ServiceUsers.Add(new ServiceUser(personId, "Series Person", new DateOnly(1970, 1, 1), "+10000000024", "Personal care", "Contact", "", RiskLevel.Low, "Onboarded", "Address", "None", "None", "Private", "Regression", "", "Independent", "Independent", "Verbal", "None", "Standard", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.CareWorkers.AddRange(
                new CareWorker(firstWorkerId, "First Series Worker", "Personal care", "Flexible", 0, 0, "Valid", "Compliant", "10 miles", TenantDefaults.OrganizationId, TenantDefaults.BranchId),
                new CareWorker(nextWorkerId, "Next Series Worker", "Personal care", "Flexible", 0, 0, "Valid", "Compliant", "10 miles", TenantDefaults.OrganizationId, TenantDefaults.BranchId),
                new CareWorker(additionalWorkerId, "Additional Series Worker", "Personal care", "Flexible", 0, 0, "Valid", "Compliant", "10 miles", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.AppUsers.Add(new AppUser(Guid.NewGuid(), managerName, $"{managerName}@aicare.local", PasswordHasher.HashPassword("Admin123!"), UserRole.CareCoordinator, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId, null, null));
            await db.SaveChangesAsync();
        }
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = managerName, password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<LoginToken>())!.Token);
        var created = await client.PostAsJsonAsync("/api/phase1/visits/recurring", new { serviceUserId = personId, careWorkerId = firstWorkerId, additionalCareWorkerIds = new[] { additionalWorkerId }, startsAt = DateTimeOffset.UtcNow.AddDays(28), visitType = "Morning care", durationMinutes = 30, requiredSkills = "", frequency = "Weekly", occurrences = 3 });
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"Create returned {(int)created.StatusCode}: {await created.Content.ReadAsStringAsync()}\n{factory.LastServerException}");
        var body = (await created.Content.ReadFromJsonAsync<JsonElement>());
        var seriesId = body.GetProperty("seriesId").GetGuid();
        var visitIds = body.GetProperty("visits").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToArray();
        Assert.Contains(seriesId.ToString(), await client.GetStringAsync("/api/phase1/visits/recurring-series"), StringComparison.OrdinalIgnoreCase);
        var restrictionId = Guid.NewGuid();
        using (var restrictionScope = factory.Services.CreateScope())
        {
            var db = restrictionScope.ServiceProvider.GetRequiredService<CareDbContext>();
            var last = await db.Visits.AsNoTracking().Where(v => visitIds.Contains(v.Id)).OrderByDescending(v => v.StartsAt).FirstAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                insert into worker_assignment_restrictions(id,care_worker_id,organization_id,branch_id,restriction_type,reason,evidence_reference,starts_at,ends_at,approved_by)
                values({restrictionId},{nextWorkerId},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},{"Assignment"},{"Synthetic restriction on final series visit"},{"TEST"},{last.StartsAt.AddMinutes(15)},{last.StartsAt.AddHours(1)},{managerName})
                """);
        }
        var blockedEdit = await client.PutAsJsonAsync($"/api/phase1/visits/recurring-series/{seriesId}", new {
            careWorkerId = nextWorkerId, visitType = "Blocked series edit", durationMinutes = 45, requiredSkills = "", changeReason = "Verify all members before saving"
        });
        Assert.Equal(HttpStatusCode.Conflict, blockedEdit.StatusCode);
        Assert.Contains("assignment-restricted", await blockedEdit.Content.ReadAsStringAsync());
        using (var blockedScope = factory.Services.CreateScope())
        {
            var db = blockedScope.ServiceProvider.GetRequiredService<CareDbContext>();
            var unchanged = await db.Visits.AsNoTracking().Where(v => visitIds.Contains(v.Id)).ToListAsync();
            Assert.Equal(3, unchanged.Count);
            Assert.All(unchanged, v => { Assert.Equal(firstWorkerId, v.CareWorkerId); Assert.Equal(30, v.DurationMinutes); Assert.Equal("Morning care", v.VisitType); });
            Assert.False(await db.AuditEvents.AnyAsync(a => a.Actor == managerName && a.Action == "visit.recurring_series_updated"));
            await db.Database.ExecuteSqlInterpolatedAsync($"update worker_assignment_restrictions set status={"Lifted"} where id={restrictionId}");
        }
        var edited = await client.PutAsJsonAsync($"/api/phase1/visits/recurring-series/{seriesId}", new { careWorkerId = nextWorkerId, visitType = "Evening care", durationMinutes = 45, requiredSkills = "", startTime = "14:30", changeReason = "Care plan changed" });
        Assert.True(edited.StatusCode == HttpStatusCode.OK, $"Edit returned {(int)edited.StatusCode}: {await edited.Content.ReadAsStringAsync()}\n{factory.LastServerException}");
        Assert.Equal(3, (await edited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("updatedCount").GetInt32());
        using var verifyScope = factory.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<CareDbContext>();
        var visits = await verify.Visits.AsNoTracking().Where(visit => visitIds.Contains(visit.Id)).ToListAsync();
        Assert.Equal(3, visits.Count);
        var ukZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        Assert.All(visits, visit => { Assert.Equal(nextWorkerId, visit.CareWorkerId); Assert.Equal("Evening care", visit.VisitType); Assert.Equal(45, visit.DurationMinutes); Assert.Equal(new TimeOnly(14, 30), TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(visit.StartsAt, ukZone).DateTime)); });
        var additionalAssignments = await verify.Database.SqlQuery<Guid>($"select visit_id as \"Value\" from visit_care_worker_assignments where organization_id={TenantDefaults.OrganizationId} and care_worker_id={additionalWorkerId}").ToListAsync();
        Assert.All(visitIds, visitId => Assert.Contains(visitId, additionalAssignments));
    }

    private sealed record LoginToken(string Token);
}
