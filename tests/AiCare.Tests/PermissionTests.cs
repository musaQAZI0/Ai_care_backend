using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AiCare.Tests;

public sealed class PermissionTests : IClassFixture<AiCareApiFactory>
{
    private readonly AiCareApiFactory _factory;

    public PermissionTests(AiCareApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CareWorkerCannotCreateVisits()
    {
        var client = _factory.CreateClient();
        await Login(client, "worker", "WorkerPassword123!");

        var response = await client.PostAsJsonAsync("/api/phase1/visits", new
        {
            serviceUserId = TestIds.ServiceUserId,
            careWorkerId = TestIds.WorkerId,
            startsAt = DateTimeOffset.UtcNow.AddDays(1),
            visitType = "Scheduled visit",
            durationMinutes = 30,
            requiredSkills = "Personal care"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CareWorkerCanCreateCareNoteForOwnAssignedVisit()
    {
        var client = _factory.CreateClient();
        await Login(client, "worker", "WorkerPassword123!");

        var response = await client.PostAsJsonAsync("/api/phase1/care-notes", new
        {
            visitId = TestIds.VisitId,
            serviceUserId = TestIds.ServiceUserId,
            careWorkerId = TestIds.WorkerId,
            summary = "Visit completed well.",
            personalCare = "Completed",
            mealsAndHydration = "Supported",
            medication = "None",
            concerns = "None",
            requiresReview = false
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CareWorkerCannotCreateCareNoteForAnotherWorkersVisit()
    {
        var client = _factory.CreateClient();
        await Login(client, "other-worker", "OtherWorkerPassword123!");

        var response = await client.PostAsJsonAsync("/api/phase1/care-notes", new
        {
            visitId = TestIds.VisitId,
            serviceUserId = TestIds.ServiceUserId,
            careWorkerId = TestIds.WorkerId,
            summary = "Trying another worker visit.",
            personalCare = "N/a",
            mealsAndHydration = "N/a",
            medication = "N/a",
            concerns = "N/a",
            requiresReview = false
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CareWorkerCannotAccessAdminUsers()
    {
        var client = _factory.CreateClient();
        await Login(client, "worker", "WorkerPassword123!");

        var response = await client.GetAsync("/api/phase1/admin/users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminCanAccessAdminUsers()
    {
        var client = _factory.CreateClient();
        await Login(client, "admin", "AdminPassword123!");

        var response = await client.GetAsync("/api/phase1/admin/users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AssignedCareWorkerCanAdministerMedication()
    {
        var client = _factory.CreateClient();
        await Login(client, "worker", "WorkerPassword123!");

        var response = await client.PostAsJsonAsync($"/api/phase1/mar/{TestIds.MarId}/administer", new
        {
            administeredAt = DateTimeOffset.UtcNow,
            notes = "Taken with breakfast"
        });

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected OK but got {(int)response.StatusCode}: {body}");
    }

    [Fact]
    public async Task OtherCareWorkerCannotAdministerMedication()
    {
        var client = _factory.CreateClient();
        await Login(client, "other-worker", "OtherWorkerPassword123!");

        var response = await client.PostAsJsonAsync($"/api/phase1/mar/{TestIds.MarId}/administer", new
        {
            administeredAt = DateTimeOffset.UtcNow,
            notes = "Should be blocked"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NotificationCanBeQueuedAndMarkedRead()
    {
        var client = _factory.CreateClient();
        await Login(client, "admin", "AdminPassword123!");

        var send = await client.PostAsJsonAsync("/api/phase1/notifications/send", new
        {
            channel = "in-app",
            title = "Visit changed",
            detail = "Morning visit has been updated"
        });
        Assert.Equal(HttpStatusCode.Accepted, send.StatusCode);
        var notification = await send.Content.ReadFromJsonAsync<NotificationResponse>();
        Assert.NotNull(notification);
        Assert.False(notification.IsRead);

        var count = await client.GetFromJsonAsync<UnreadCountResponse>("/api/phase1/notifications/unread-count");
        Assert.True(count?.Unread >= 1);

        var read = await client.PostAsync($"/api/phase1/notifications/{notification.Id}/read", null);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var updated = await read.Content.ReadFromJsonAsync<NotificationResponse>();
        Assert.True(updated?.IsRead);
    }

    [Fact]
    public async Task NotificationsCanBeMarkedAllRead()
    {
        var client = _factory.CreateClient();
        await Login(client, "admin", "AdminPassword123!");

        await client.PostAsJsonAsync("/api/phase1/notifications/send", new { channel = "in-app", title = "One", detail = "First" });
        await client.PostAsJsonAsync("/api/phase1/notifications/send", new { channel = "in-app", title = "Two", detail = "Second" });

        var response = await client.PostAsync("/api/phase1/notifications/read-all", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var count = await client.GetFromJsonAsync<UnreadCountResponse>("/api/phase1/notifications/unread-count");
        Assert.Equal(0, count?.Unread);
    }

    [Fact]
    public async Task BackOfficeCanApprovePayrollRun()
    {
        var client = _factory.CreateClient();
        await Login(client, "backoffice", "BackOfficePassword123!");

        var response = await client.PostAsync($"/api/phase1/payroll-runs/{TestIds.PayrollRunId}/approve", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CareWorkerCannotAccessInvoices()
    {
        var client = _factory.CreateClient();
        await Login(client, "worker", "WorkerPassword123!");

        var response = await client.GetAsync("/api/phase1/invoices");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task BackOfficeCanRecordInvoicePayment()
    {
        var client = _factory.CreateClient();
        await Login(client, "backoffice", "BackOfficePassword123!");

        var response = await client.PostAsJsonAsync($"/api/phase1/invoices/{TestIds.InvoiceId}/record-payment", new
        {
            amount = 120m,
            reference = "TEST-PAY-001"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task BackOfficeCanVoidGeneratedInvoice()
    {
        var client = _factory.CreateClient();
        await Login(client, "backoffice", "BackOfficePassword123!");

        var response = await client.PostAsJsonAsync($"/api/phase1/invoices/{TestIds.VoidInvoiceId}/void", new
        {
            reason = "Incorrect funder"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RepositoryActionsAuditCurrentUser()
    {
        var client = _factory.CreateClient();
        await Login(client, "admin", "AdminPassword123!");

        var create = await client.PostAsJsonAsync("/api/phase1/care-workers", new
        {
            fullName = "Audited Worker",
            specialization = "Medication support",
            availability = "Weekdays"
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var audits = await client.GetFromJsonAsync<List<AuditResponse>>("/api/phase1/audit-events");
        Assert.Contains(audits ?? [], audit => audit.Action == "care_worker.added" && audit.Actor == "admin");
    }

    [Fact]
    public async Task ResponsesIncludeRequestIdAndSecurityHeaders()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Request-ID"));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task ConfigStatusDoesNotExposeSecrets()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/status/config");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("jwtConfigured", body);
        Assert.DoesNotContain("test-signing-key", body, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task Login(HttpClient client, string userName, string password)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName, password });
        login.EnsureSuccessStatusCode();
        var payload = await login.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(payload?.Token);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", payload.Token);
    }

    private sealed record LoginResponse(string Token);
    private sealed record NotificationResponse(Guid Id, string Title, string Detail, DateTimeOffset CreatedAt, bool IsRead);
    private sealed record UnreadCountResponse(int Unread);
    private sealed record AuditResponse(Guid Id, string Action, string Actor, string EntityType, Guid? EntityId, DateTimeOffset CreatedAt);
}

public sealed class AiCareApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open();
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(configuration =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=aicare_tests;Username=test;Password=test",
                ["JwtOptions:Issuer"] = "AiCare",
                ["JwtOptions:Audience"] = "AiCareClient",
                ["JwtOptions:SigningKey"] = "test-signing-key-with-enough-length-for-hmac",
                ["JwtOptions:TokenLifetimeMinutes"] = "120"
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<CareDbContext>>();
            services.AddDbContext<CareDbContext>(options => options.UseSqlite(_connection));

            using var scope = services.BuildServiceProvider().CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            context.Database.EnsureCreated();
            Seed(context);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _connection.Dispose();
    }

    private static void Seed(CareDbContext context)
    {
        context.AppUsers.RemoveRange(context.AppUsers);
        context.PayrollRuns.RemoveRange(context.PayrollRuns);
        context.Invoices.RemoveRange(context.Invoices);
        context.MedicationAdministrationRecords.RemoveRange(context.MedicationAdministrationRecords);
        context.Medications.RemoveRange(context.Medications);
        context.ServiceUsers.RemoveRange(context.ServiceUsers);
        context.CareWorkers.RemoveRange(context.CareWorkers);
        context.Visits.RemoveRange(context.Visits);
        context.SaveChanges();

        context.ServiceUsers.Add(new ServiceUser(
            TestIds.ServiceUserId,
            "Test Service User",
            new DateOnly(1970, 1, 1),
            "+10000000000",
            "Personal care",
            "Emergency contact",
            "Test Worker",
            RiskLevel.Low,
            "Active",
            "Test address",
            "None",
            "None",
            "Private",
            "Not specified",
            "",
            "Independent",
            "None",
            "None",
            "None",
            "None",
            TenantDefaults.OrganizationId,
            TenantDefaults.BranchId));
        context.CareWorkers.AddRange(
            new CareWorker(TestIds.WorkerId, "Test Worker", "Care", "Weekdays", 1, 50, "Clear", "Complete", "10 miles", TenantDefaults.OrganizationId, TenantDefaults.BranchId),
            new CareWorker(TestIds.OtherWorkerId, "Other Worker", "Care", "Weekdays", 0, 10, "Clear", "Complete", "10 miles", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        context.Visits.Add(new Visit(
            TestIds.VisitId,
            TestIds.ServiceUserId,
            TestIds.WorkerId,
            DateTimeOffset.UtcNow.AddDays(1),
            "Morning visit",
            30,
            "Personal care",
            VisitStatus.Scheduled,
            null,
            null,
            null,
            null,
            null,
            null,
            TenantDefaults.OrganizationId,
            TenantDefaults.BranchId));
        context.Medications.Add(new Medication(
            TestIds.MedicationId,
            TestIds.ServiceUserId,
            "Paracetamol",
            "500mg",
            "Oral",
            "Morning",
            false,
            "Test Pharmacy",
            "Check allergies",
            TenantDefaults.OrganizationId,
            TenantDefaults.BranchId));
        context.MedicationAdministrationRecords.Add(new MedicationAdministrationRecord(
            TestIds.MarId,
            TestIds.MedicationId,
            TestIds.VisitId,
            TestIds.WorkerId,
            DateTimeOffset.UtcNow.AddHours(1),
            null,
            "Scheduled",
            "Due with breakfast",
            TenantDefaults.OrganizationId,
            TenantDefaults.BranchId));
        context.PayrollRuns.Add(new PayrollRun(
            TestIds.PayrollRunId,
            "2026-W33",
            2,
            100m,
            "Generated",
            DateTimeOffset.UtcNow,
            TenantDefaults.OrganizationId,
            TenantDefaults.BranchId));
        context.Invoices.AddRange(
            new Invoice(TestIds.InvoiceId, TestIds.ServiceUserId, "Private", 120m, "Approved", DateTimeOffset.UtcNow, TenantDefaults.OrganizationId, TenantDefaults.BranchId),
            new Invoice(TestIds.VoidInvoiceId, TestIds.ServiceUserId, "Private", 90m, "Generated", DateTimeOffset.UtcNow, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        context.AppUsers.AddRange(
            new AppUser(Guid.NewGuid(), "admin", "admin@test.local", PasswordHasher.HashPassword("AdminPassword123!"), UserRole.Administrator, true, TenantDefaults.OrganizationId),
            new AppUser(Guid.NewGuid(), "backoffice", "backoffice@test.local", PasswordHasher.HashPassword("BackOfficePassword123!"), UserRole.BackOffice, true, TenantDefaults.OrganizationId),
            new AppUser(Guid.NewGuid(), "worker", "worker@test.local", PasswordHasher.HashPassword("WorkerPassword123!"), UserRole.CareWorker, true, TenantDefaults.OrganizationId, null, TestIds.WorkerId),
            new AppUser(Guid.NewGuid(), "other-worker", "other@test.local", PasswordHasher.HashPassword("OtherWorkerPassword123!"), UserRole.CareWorker, true, TenantDefaults.OrganizationId, null, TestIds.OtherWorkerId));
        context.SaveChanges();
    }
}

internal static class TestIds
{
    public static readonly Guid ServiceUserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public static readonly Guid WorkerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    public static readonly Guid OtherWorkerId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    public static readonly Guid VisitId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    public static readonly Guid MedicationId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    public static readonly Guid MarId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
    public static readonly Guid PayrollRunId = Guid.Parse("11111111-aaaa-aaaa-aaaa-111111111111");
    public static readonly Guid InvoiceId = Guid.Parse("22222222-aaaa-aaaa-aaaa-222222222222");
    public static readonly Guid VoidInvoiceId = Guid.Parse("33333333-aaaa-aaaa-aaaa-333333333333");
}
