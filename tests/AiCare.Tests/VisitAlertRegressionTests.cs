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
public sealed class VisitAlertRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task MissedVisitCreatesOneAlertAndOverdueFollowUp()
    {
        await factory.EnsureClinicalSeedAsync();
        var personId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var visitId = Guid.NewGuid();
        var managerName = $"alert.manager.{Guid.NewGuid():N}";
        var startsAt = DateTimeOffset.UtcNow.AddHours(-2);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.ServiceUsers.Add(new ServiceUser(personId, "Alert Person", new DateOnly(1970, 1, 1), "+10000000025", "Personal care", "Contact", "", RiskLevel.Low, "Onboarded", "Address", "None", "None", "Private", "Regression", "", "Independent", "Independent", "Verbal", "None", "Standard", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.CareWorkers.Add(new CareWorker(workerId, "Alert Worker", "Personal care", "Flexible", 0, 0, "Valid", "Compliant", "10 miles", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.Visits.Add(new Visit(visitId, personId, workerId, startsAt, "Welfare check", 30, "", VisitStatus.Scheduled, null, null, null, null, null, null, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.AppUsers.Add(new AppUser(Guid.NewGuid(), managerName, $"{managerName}@aicare.local", PasswordHasher.HashPassword("Admin123!"), UserRole.CareCoordinator, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId, null, null));
            await db.SaveChangesAsync();
            var monitor = scope.ServiceProvider.GetRequiredService<VisitAlertMonitoringService>();
            await monitor.ScanAsync(CancellationToken.None);
            await monitor.ScanAsync(CancellationToken.None);
            var alertIds = await db.Database.SqlQuery<Guid>($"""
                select id as "Value" from visit_exceptions where visit_id={visitId} and exception_type={"Missed"}
                  and created_by={"System monitoring"}
                """).ToListAsync();
            Assert.Single(alertIds);
            await db.Database.ExecuteSqlInterpolatedAsync($"update visit_exceptions set escalation_due_at={DateTimeOffset.UtcNow.AddMinutes(-1)} where id={alertIds[0]}");
            await monitor.ScanAsync(CancellationToken.None);
            await monitor.ScanAsync(CancellationToken.None);
            var overdueCount = await db.Database.SqlQuery<int>($"""
                select count(*)::int as "Value" from visit_escalation_events
                where visit_exception_id={alertIds[0]} and action={"Overdue"}
                """).SingleAsync();
            Assert.Equal(1, overdueCount);
        }

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = managerName, password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<LoginToken>())!.Token);
        var from = Uri.EscapeDataString(startsAt.AddHours(-1).ToString("O"));
        var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("O"));
        var response = await client.GetAsync($"/api/phase1/visit-operations/alerts?from={from}&to={to}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Alerts returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}\n{factory.LastServerException}");
        var alerts = await response.Content.ReadFromJsonAsync<JsonElement>();
        var alert = alerts.EnumerateArray().Single(item => item.GetProperty("visitId").GetGuid() == visitId);
        Assert.True(alert.GetProperty("escalated").GetBoolean());
        var acknowledged = await client.PatchAsJsonAsync($"/api/phase1/visit-operations/exceptions/{alert.GetProperty("id").GetGuid()}", new { action = "Acknowledge", detail = "Called the worker and recorded follow-up" });
        Assert.True(acknowledged.StatusCode == HttpStatusCode.NoContent, $"Acknowledge returned {(int)acknowledged.StatusCode}: {await acknowledged.Content.ReadAsStringAsync()}\n{factory.LastServerException}");
    }

    private sealed record LoginToken(string Token);
}
