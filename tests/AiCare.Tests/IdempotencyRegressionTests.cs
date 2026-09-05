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
public sealed class IdempotencyRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task ReplayedVisitCreateWithSameIdempotencyKeyReturnsOriginalVisit()
    {
        await factory.EnsureClinicalSeedAsync();
        var workerId = Guid.NewGuid();
        var personId = Guid.NewGuid();
        var startsAt = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(10).AddHours(10), TimeSpan.Zero);
        using (var scope = factory.Services.CreateScope())
        {
            var seedDb = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            seedDb.CareWorkers.Add(new CareWorker(workerId, "Idempotent Worker", "Personal care", "Flexible", 0, 0, "Valid", "Compliant", "10 miles", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            seedDb.ServiceUsers.Add(new ServiceUser(personId, "Idempotent Person", new DateOnly(1980, 1, 1), "+10000002000", "Personal care", "Contact", "Worker", RiskLevel.Low, "Active", "Address", "None", "None", "Private", "Not specified", "", "Independent", "None", "None", "None", "None", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await seedDb.SaveChangesAsync();

            await seedDb.Database.ExecuteSqlInterpolatedAsync($"""
                insert into worker_availability_rules
                  (id,care_worker_id,organization_id,branch_id,day_of_week,start_time,end_time,is_available,effective_from,effective_to,notes,created_at)
                values ({Guid.NewGuid()},{workerId},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},{(int)startsAt.DayOfWeek},{new TimeOnly(8, 0)},{new TimeOnly(18, 0)},true,{DateOnly.FromDateTime(startsAt.Date.AddDays(-1))},null,'Idempotency regression',now())
                """);
        }

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString());
        client.DefaultRequestHeaders.Add("Idempotency-Key", $"visit-create-{Guid.NewGuid():N}");

        var body = new
        {
            serviceUserId = personId,
            careWorkerId = workerId,
            startsAt,
            visitType = "Idempotent visit",
            durationMinutes = 30,
            requiredSkills = "Personal care",
            changeReason = "Regression idempotency"
        };

        var first = await client.PostAsJsonAsync("/api/phase1/visits", body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstId = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var replay = await client.PostAsJsonAsync("/api/phase1/visits", body);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayId = JsonDocument.Parse(await replay.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        Assert.Equal(firstId, replayId);
        using var verify = factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal(1, await db.Visits.CountAsync(visit => visit.ServiceUserId == personId && visit.CareWorkerId == workerId && visit.VisitType == "Idempotent visit"));
        var idempotencyRows = await db.Database.SqlQueryRaw<int>("select count(*)::int as \"Value\" from api_idempotency_keys where resource_id={0}", firstId).SingleAsync();
        Assert.Equal(1, idempotencyRows);
    }
}
