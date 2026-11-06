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
public sealed class RotaPublicationRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task PublishAcknowledgeAndRepublishAfterVisitChanges()
    {
        await factory.EnsureClinicalSeedAsync();
        var monday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(35));
        monday = monday.AddDays(-((int)monday.DayOfWeek + 6) % 7);
        var startsAt = new DateTimeOffset(monday.ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);
        var personId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var additionalWorkerId = Guid.NewGuid();
        var visitId = Guid.NewGuid();
        var managerName = $"rota.manager.{Guid.NewGuid():N}";
        var workerName = $"rota.worker.{Guid.NewGuid():N}";
        var additionalWorkerName = $"rota.additional.{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.ServiceUsers.Add(new ServiceUser(personId, "Rota Person", new DateOnly(1970, 1, 1), "+10000000023", "Personal care", "Contact", "", RiskLevel.Low, "Onboarded", "Address", "None", "None", "Private", "Regression", "", "Independent", "Independent", "Verbal", "None", "Standard", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.CareWorkers.Add(new CareWorker(workerId, "Rota Worker", "Personal care", "Flexible", 0, 0, "Valid", "Compliant", "10 miles", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.CareWorkers.Add(new CareWorker(additionalWorkerId, "Additional Rota Worker", "Personal care", "Flexible", 0, 0, "Valid", "Compliant", "10 miles", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.Visits.Add(new Visit(visitId, personId, workerId, startsAt, "Morning care", 30, "", VisitStatus.Scheduled, null, null, null, null, null, null, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.AppUsers.AddRange(User(managerName, UserRole.CareCoordinator), User(workerName, UserRole.CareWorker, workerId), User(additionalWorkerName, UserRole.CareWorker, additionalWorkerId));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"insert into visit_care_worker_assignments(visit_id,care_worker_id,organization_id,branch_id,assignment_role) values({visitId},{additionalWorkerId},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},{"Additional"})");
        }
        var manager = await Client(managerName);
        var worker = await Client(workerName);
        var additionalWorker = await Client(additionalWorkerName);
        Assert.DoesNotContain(visitId.ToString(), await worker.GetStringAsync("/api/phase1/visits"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(visitId.ToString(), await additionalWorker.GetStringAsync("/api/phase1/visits"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NotFound, (await worker.GetAsync($"/api/phase1/visits/{visitId}")).StatusCode);
        await AssertDeliveryAccess(worker, visitId, false);
        await AssertDeliveryAccess(additionalWorker, visitId, false);
        await AssertTaskAccess(worker, visitId, false);
        var first = await manager.PostAsJsonAsync("/api/phase1/rota-publications", new { branchId = TenantDefaults.BranchId, weekStart = monday });
        Assert.True(first.StatusCode == HttpStatusCode.Created, $"Publish returned {(int)first.StatusCode}: {await first.Content.ReadAsStringAsync()}\n{factory.LastServerException}");
        var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Contains(visitId.ToString(), await worker.GetStringAsync("/api/phase1/visits"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(visitId.ToString(), await additionalWorker.GetStringAsync("/api/phase1/visits"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(personId.ToString(), await additionalWorker.GetStringAsync("/api/phase1/service-users"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await worker.GetAsync($"/api/phase1/visits/{visitId}")).StatusCode);
        await AssertDeliveryAccess(worker, visitId, true);
        await AssertDeliveryAccess(additionalWorker, visitId, true);
        await AssertTaskAccess(worker, visitId, true);
        Assert.Contains(firstId.ToString(), await worker.GetStringAsync("/api/phase1/rota-publications/mine"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NoContent, (await worker.PostAsync($"/api/phase1/rota-publications/{firstId}/acknowledge", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await worker.PostAsync($"/api/phase1/rota-publications/{firstId}/acknowledge", null)).StatusCode);

        var moved = await manager.PostAsJsonAsync($"/api/phase1/visits/{visitId}/move", new { expectedStartsAt = startsAt, startsAt = startsAt.AddMinutes(30) });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        await AssertTaskAccess(worker, visitId, false);
        await AssertDeliveryAccess(worker, visitId, false);
        await AssertDeliveryAccess(additionalWorker, visitId, false);
        var summary = await manager.GetStringAsync("/api/phase1/rota-publications");
        Assert.Contains("\"needsRepublish\":true", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(visitId.ToString(), await worker.GetStringAsync("/api/phase1/visits"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(visitId.ToString(), await additionalWorker.GetStringAsync("/api/phase1/visits"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(firstId.ToString(), await worker.GetStringAsync("/api/phase1/rota-publications/mine"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Conflict, (await worker.PostAsync($"/api/phase1/rota-publications/{firstId}/acknowledge", null)).StatusCode);

        var second = await manager.PostAsJsonAsync("/api/phase1/rota-publications", new { branchId = TenantDefaults.BranchId, weekStart = monday });
        Assert.True(second.StatusCode == HttpStatusCode.Created, $"Republish returned {(int)second.StatusCode}: {await second.Content.ReadAsStringAsync()}\n{factory.LastServerException}");
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, secondBody.GetProperty("revision").GetInt32());
        var secondId = secondBody.GetProperty("id").GetGuid();
        Assert.Contains(visitId.ToString(), await worker.GetStringAsync("/api/phase1/visits"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(visitId.ToString(), await additionalWorker.GetStringAsync("/api/phase1/visits"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NoContent, (await worker.PostAsync($"/api/phase1/rota-publications/{secondId}/acknowledge", null)).StatusCode);
        await AssertDeliveryAccess(worker, visitId, true);
        await AssertDeliveryAccess(additionalWorker, visitId, true);
        await AssertTaskAccess(worker, visitId, true);
        using (var changeScope = factory.Services.CreateScope())
        {
            var db = changeScope.ServiceProvider.GetRequiredService<CareDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"delete from visit_care_worker_assignments where visit_id={visitId} and care_worker_id={additionalWorkerId}");
        }
        Assert.DoesNotContain(visitId.ToString(), await worker.GetStringAsync("/api/phase1/visits"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secondId.ToString(), await worker.GetStringAsync("/api/phase1/rota-publications/mine"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Conflict, (await additionalWorker.PostAsync($"/api/phase1/rota-publications/{secondId}/acknowledge", null)).StatusCode);
        await AssertTaskAccess(worker, visitId, false);
        await AssertDeliveryAccess(worker, visitId, false);
        await AssertDeliveryAccess(additionalWorker, visitId, false);
        var reassigned = await manager.PostAsJsonAsync($"/api/phase1/visits/{visitId}/move", new { expectedStartsAt = startsAt.AddMinutes(30), startsAt = startsAt.AddMinutes(45), careWorkerId = additionalWorkerId });
        Assert.True(reassigned.StatusCode == HttpStatusCode.OK, $"Reassign returned {(int)reassigned.StatusCode}: {await reassigned.Content.ReadAsStringAsync()}\n{factory.LastServerException}");
        using var verifyScope = factory.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal(additionalWorkerId, (await verify.Visits.AsNoTracking().SingleAsync(visit => visit.Id == visitId)).CareWorkerId);
    }

    private async Task AssertTaskAccess(HttpClient client, Guid visitId, bool allowed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        var visit = await db.Visits.AsNoTracking().SingleAsync(v => v.Id == visitId);
        var task = Guid.NewGuid();
        var title = $"Synthetic publication task {task:N}";
        // A legacy unlinked task isolates publication access from plan activation.
        // Plan-linked lifecycle/supersession behavior has its own regression suite.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into visit_tasks(id,visit_id,service_user_id,care_worker_id,organization_id,branch_id,title,instructions,status)
            values({task},{visit.Id},{visit.ServiceUserId},{visit.CareWorkerId},{visit.OrganizationId},{visit.BranchId},{title},{"Synthetic instructions"},{"Pending"})
            """);
        var path = $"/api/phase1/visits/{visitId}/tasks";
        var read = await client.GetAsync(path);
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, read.StatusCode);
        if (!allowed) Assert.DoesNotContain(title, await read.Content.ReadAsStringAsync());
        var outcome = await client.PostAsJsonAsync($"{path}/{task}/outcome", new { outcome = "Completed", exceptionReason = "" });
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, outcome.StatusCode);
        Assert.Equal(allowed ? "Completed" : "Pending", await db.Database.SqlQuery<string>($"""
            select status as "Value" from visit_tasks where id={task}
            """).SingleAsync());
        Assert.Equal(allowed ? 1 : 0, await db.AuditEvents.CountAsync(a => a.EntityId == task && a.Action == "visit_task.outcome_recorded"));
    }

    private static async Task AssertDeliveryAccess(HttpClient client, Guid visitId, bool allowed)
    {
        var root=$"/api/phase1/visits/{visitId}/delivery";
        foreach(var suffix in new[]{"observations","location"})
            Assert.Equal(allowed?HttpStatusCode.OK:HttpStatusCode.Forbidden,(await client.GetAsync($"{root}/{suffix}")).StatusCode);
        var observation=await client.PostAsJsonAsync($"{root}/observations",new{observationType="Mood",value="Settled",unit="",notes="Synthetic publication access check",recordedAt=(DateTimeOffset?)null});
        Assert.Equal(allowed?HttpStatusCode.Created:HttpStatusCode.Forbidden,observation.StatusCode);
        var location=await client.PostAsJsonAsync($"{root}/location",new{eventType="Evidence",latitude=51.5m,longitude=-0.1m,accuracyMeters=5m,capturedAt=DateTimeOffset.UtcNow,source="Regression test",notes="Synthetic publication access check"});
        Assert.Equal(allowed?HttpStatusCode.Created:HttpStatusCode.Forbidden,location.StatusCode);
    }

    private static AppUser User(string name, UserRole role, Guid? workerId = null) =>
        new(Guid.NewGuid(), name, $"{name}@aicare.local", PasswordHasher.HashPassword("Admin123!"), role, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId, workerId, null);

    private async Task<HttpClient> Client(string name)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = name, password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<LoginToken>())!.Token);
        return client;
    }

    private sealed record LoginToken(string Token);
}
