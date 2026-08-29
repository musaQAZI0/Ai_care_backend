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
public sealed class SchedulingAvailabilityRegressionTests : IClassFixture<PostgresRegressionFactory>
{
    private readonly PostgresRegressionFactory _factory;

    public SchedulingAvailabilityRegressionTests(PostgresRegressionFactory factory) => _factory = factory;

    [Fact]
    public async Task StructuredAvailabilityIsExplainedAndEnforcedForVisitCreation()
    {
        await _factory.EnsureClinicalSeedAsync();
        var workerId = Guid.NewGuid();
        var personId = Guid.NewGuid();
        var adminUserName = $"scheduling.admin.{Guid.NewGuid():N}";
        var visitDate = DateTime.UtcNow.Date.AddDays(14);
        var availableStart = new DateTimeOffset(visitDate.AddHours(10), TimeSpan.Zero);
        var unavailableStart = new DateTimeOffset(visitDate.AddHours(18), TimeSpan.Zero);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.CareWorkers.Add(new CareWorker(workerId, "Availability Regression Worker", "Personal care", "Structured rules", 0, 0, "Valid", "Compliant", "10 miles", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.AppUsers.Add(new AppUser(Guid.NewGuid(), adminUserName, $"{adminUserName}@aicare.local", PasswordHasher.HashPassword("Admin123!"), UserRole.Administrator, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId, null, null));
            db.ServiceUsers.Add(new ServiceUser(
                personId, "Availability Regression Person", new DateOnly(1970, 1, 1), "+10000000000",
                "Personal care", "Regression contact", "", RiskLevel.Low, "Onboarded", "Regression address",
                "None", "None", "Private", "Regression", "", "Independent", "Independent", "Verbal", "None", "Standard",
                TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await db.SaveChangesAsync();

            var effectiveFrom = DateOnly.FromDateTime(visitDate.AddDays(-1));
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                insert into worker_availability_rules
                  (id,care_worker_id,organization_id,branch_id,day_of_week,start_time,end_time,is_available,effective_from,effective_to,notes,created_at)
                values ({Guid.NewGuid()},{workerId},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},{(int)visitDate.DayOfWeek},{new TimeOnly(9, 0)},{new TimeOnly(17, 0)},true,{effectiveFrom},null,'Regression availability',now())
                """);
        }

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = adminUserName, password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var unavailableRequest = new
        {
            serviceUserId = personId,
            careWorkerId = workerId,
            startsAt = unavailableStart,
            visitType = "Availability regression",
            durationMinutes = 30,
            requiredSkills = "Personal care"
        };
        var conflict = await client.PostAsJsonAsync("/api/phase1/visits/conflicts", unavailableRequest);
        Assert.True(conflict.StatusCode == HttpStatusCode.OK,
            $"Expected authenticated conflict check to succeed, got {(int)conflict.StatusCode}. WWW-Authenticate: {string.Join(';', conflict.Headers.WwwAuthenticate)}. Body: {await conflict.Content.ReadAsStringAsync()}");
        var conflictBody = await conflict.Content.ReadAsStringAsync();
        Assert.Contains("availabilityConflicts", conflictBody);
        Assert.Contains("outside the worker's available hours", conflictBody);

        var blocked = await client.PostAsJsonAsync("/api/phase1/visits", unavailableRequest);
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.Contains("unavailable", await blocked.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var allowed = await client.PostAsJsonAsync("/api/phase1/visits", new
        {
            serviceUserId = personId,
            careWorkerId = workerId,
            startsAt = availableStart,
            visitType = "Availability regression",
            durationMinutes = 30,
            requiredSkills = "Personal care"
        });
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    [Fact]
    public async Task ExpiredComplianceAndMissingSkillsAreExplainedAndBlockAssignment()
    {
        await _factory.EnsureClinicalSeedAsync();
        var workerId = Guid.NewGuid();
        var personId = Guid.NewGuid();
        var adminUserName = $"safety.admin.{Guid.NewGuid():N}";
        var startsAt = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(21).AddHours(11), TimeSpan.Zero);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.CareWorkers.Add(new CareWorker(workerId, "Safety Regression Worker", "Personal care", "Structured rules", 0, 0, "Expired", "Compliant", "10 miles", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.AppUsers.Add(new AppUser(Guid.NewGuid(), adminUserName, $"{adminUserName}@aicare.local", PasswordHasher.HashPassword("Admin123!"), UserRole.Administrator, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId, null, null));
            db.ServiceUsers.Add(new ServiceUser(
                personId, "Safety Regression Person", new DateOnly(1970, 1, 1), "+10000000001",
                "Medication support", "Regression contact", "", RiskLevel.Low, "Onboarded", "Regression address",
                "None", "None", "Private", "Regression", "", "Independent", "Independent", "Verbal", "None", "Standard",
                TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await db.SaveChangesAsync();

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                insert into worker_availability_rules
                  (id,care_worker_id,organization_id,branch_id,day_of_week,start_time,end_time,is_available,effective_from,effective_to,notes,created_at)
                values ({Guid.NewGuid()},{workerId},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},{(int)startsAt.DayOfWeek},{new TimeOnly(8, 0)},{new TimeOnly(18, 0)},true,{DateOnly.FromDateTime(startsAt.Date.AddDays(-1))},null,'Safety regression',now());
                insert into worker_compliance_records
                  (id,care_worker_id,organization_id,branch_id,compliance_type,reference,status,issued_at,expires_at,verified_by,notes,created_at,updated_at)
                values ({Guid.NewGuid()},{workerId},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},'DBS','DBS-EXPIRED','Valid',{startsAt.AddYears(-2).UtcDateTime},{startsAt.AddDays(-1).UtcDateTime},'Regression','Expired DBS',now(),now())
                """);
        }

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = adminUserName, password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<LoginResponse>())!.Token);
        var request = new
        {
            serviceUserId = personId,
            careWorkerId = workerId,
            startsAt,
            visitType = "Medication support",
            durationMinutes = 30,
            requiredSkills = "Medication administration"
        };

        var conflict = await client.PostAsJsonAsync("/api/phase1/visits/conflicts", request);
        Assert.Equal(HttpStatusCode.OK, conflict.StatusCode);
        var conflictBody = await conflict.Content.ReadAsStringAsync();
        Assert.Contains("safetyConflicts", conflictBody);
        Assert.Contains("dbs-invalid", conflictBody);
        Assert.Contains("skill-gap", conflictBody);

        var blocked = await client.PostAsJsonAsync("/api/phase1/visits", request);
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.Contains("compliance or skill", await blocked.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                update worker_compliance_records set expires_at={startsAt.AddYears(1).UtcDateTime} where care_worker_id={workerId} and compliance_type='DBS';
                insert into worker_competency_records
                  (id,care_worker_id,organization_id,branch_id,competency,level,status,assessed_by,assessed_at,expires_at,notes,created_at)
                values ({Guid.NewGuid()},{workerId},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},'Medication administration','Competent','Valid','Regression',{startsAt.AddMonths(-1).UtcDateTime},{startsAt.AddYears(1).UtcDateTime},'',now())
                """);
        }

        var allowed = await client.PostAsJsonAsync("/api/phase1/visits", request);
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    private sealed record LoginResponse(string Token);
}
