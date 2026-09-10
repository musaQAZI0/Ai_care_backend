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
public sealed class FullProviderJourneyRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task WebBackendProviderJourneyLinksPlanVisitCareDocumentationFamilyTimelineAndFinance()
    {
        await factory.EnsureClinicalSeedAsync();

        var workerId = Guid.NewGuid();
        var serviceUserId = Guid.NewGuid();
        var visitId = Guid.NewGuid();
        var noteId = Guid.NewGuid();
        var starts = new DateTimeOffset(2033, 4, 5, 9, 0, 0, TimeSpan.Zero);
        var managerName = $"journey.manager.{Guid.NewGuid():N}";
        var workerName = $"journey.worker.{Guid.NewGuid():N}";
        var financeName = $"journey.finance.{Guid.NewGuid():N}";

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.CareWorkers.Add(new CareWorker(workerId, "Full Journey Worker", "Personal care", "Flexible", 0, 0, "Valid", "Compliant", "10 miles", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.ServiceUsers.Add(new ServiceUser(serviceUserId, "Full Journey Person", new DateOnly(1944, 2, 3), "+10000000009", "Morning personal care and nutrition support", "Daughter", "", RiskLevel.Medium, "Onboarded", "1 Journey Street", "None recorded", "Diabetes", "Local Authority", "Regression", "", "Independent", "Independent", "Verbal", "Routine", "Standard", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.PersonRecords.Add(new PersonRecord(Guid.NewGuid(), serviceUserId, "NHS-123", "MRN-456", "NHS", "Journey GP", "Journey Pharmacy", "Daughter", "Active consent", "Has capacity", "", "Diabetes history", "Stay well at home", "Maintain safe routine", "", null, null, DateTimeOffset.UtcNow, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.AppUsers.AddRange(User(managerName, UserRole.Administrator, null), User(workerName, UserRole.CareWorker, workerId), User(financeName, UserRole.BackOffice, null));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("insert into funding_arrangements(id,service_user_id,organization_id,branch_id,funding_source,funder_name,contract_reference,care_package_type,authorized_hours_per_week,hourly_rate,valid_from,valid_to,status,notes,created_at,updated_at) values({0},{1},{2},{3},'Local Authority','Council','JOURNEY-1','Domiciliary care',2.00,30.00,{4},null,'Active','Full journey funding',now(),now())", Guid.NewGuid(), serviceUserId, TenantDefaults.OrganizationId, TenantDefaults.BranchId, starts.UtcDateTime);
        }

        var manager = await Client(managerName);
        var worker = await Client(workerName);
        var finance = await Client(financeName);

        var createPlan = await manager.PostAsJsonAsync("/api/phase1/care-plans", new
        {
            serviceUserId,
            personalCare = "Support with washing and dressing",
            medicationSupport = "Prompt prescribed medication and report concerns",
            mobilityAndTransfers = "One worker verbal support",
            nutrition = "Prepare breakfast and encourage fluids",
            reviewDueAt = starts.AddMonths(3)
        });
        Assert.Equal(HttpStatusCode.Created, createPlan.StatusCode);
        var plan = (await createPlan.Content.ReadFromJsonAsync<CarePlanDto>())!;

        var createTask = await manager.PostAsJsonAsync($"/api/phase1/care-plans/{plan.Id}/tasks", new
        {
            title = "Prepare breakfast and prompt fluids",
            category = "Nutrition",
            instructions = "Offer preferred breakfast and record any concerns",
            isRequired = true,
            frequency = "Every morning visit"
        });
        Assert.Equal(HttpStatusCode.Created, createTask.StatusCode);

        var draft = await GetLifecycle(manager, plan.Id);
        var submitted = await PostLifecycle(manager, plan.Id, "submit-review", draft.Version.Revision, "Ready for approval");
        var approved = await PostLifecycle(manager, plan.Id, "lifecycle/approve", submitted.Version.Revision, "Care plan approved for regression journey");
        var managerSigned = await Sign(manager, plan.Id, approved.Version.Revision, "CareManager", "Journey Manager", "Care manager");
        var representativeSigned = await Sign(manager, plan.Id, managerSigned.Version.Revision, "Representative", "Journey Representative", "Daughter");
        await factory.SeedCarePlanActivationPrerequisitesAsync(serviceUserId);
        var active = await PostActivate(manager, plan.Id, representativeSigned.Version.Revision);
        Assert.Equal("Active", active.Version.Status);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Visits.Add(new Visit(visitId, serviceUserId, workerId, starts, "Morning care", 60, "Personal care; Nutrition", VisitStatus.Scheduled, null, null, null, null, null, null, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await db.SaveChangesAsync();
        }

        var materializedTasks = await worker.GetFromJsonAsync<List<VisitTaskDto>>($"/api/phase1/visits/{visitId}/tasks");
        var visitTask = Assert.Single(materializedTasks!);
        Assert.Contains("breakfast", visitTask.Title, StringComparison.OrdinalIgnoreCase);

        var taskOutcome = await worker.PostAsJsonAsync($"/api/phase1/visits/{visitId}/tasks/{visitTask.Id}/outcome", new { outcome = "Completed", exceptionReason = "" });
        Assert.Equal(HttpStatusCode.OK, taskOutcome.StatusCode);

        var observation = await worker.PostAsJsonAsync($"/api/phase1/visits/{visitId}/delivery/observations", new { observationType = "Mood", value = "Settled", unit = "", notes = "Person was settled during morning call", recordedAt = (DateTimeOffset?)null });
        Assert.Equal(HttpStatusCode.Created, observation.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.CareNotes.Add(new CareNote(noteId, visitId, serviceUserId, workerId, "Morning care completed", "Supported wash and dress", "Breakfast accepted", "Medication prompt completed", "No concerns", true, DateTimeOffset.UtcNow, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await db.SaveChangesAsync();
        }

        var amended = await worker.PostAsJsonAsync($"/api/phase1/care-documentation/notes/{noteId}/amendments", new { reason = "Worker clarification", summary = "Morning care completed", personalCare = "Supported wash and dress", mealsAndHydration = "Breakfast and fluids accepted", medication = "Medication prompt completed", concerns = "No concerns", requiresReview = true });
        Assert.Equal(HttpStatusCode.Created, amended.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await manager.PostAsJsonAsync($"/api/phase1/care-documentation/notes/{noteId}/reviews", new { status = "Approved", comment = "Reviewed for full journey" })).StatusCode);

        var handover = await worker.PostAsJsonAsync($"/api/phase1/visit-operations/visits/{visitId}/handovers", new { summary = "Morning care completed", outstandingActions = "Monitor hydration at next call", urgent = false, attachmentReference = "" });
        Assert.Equal(HttpStatusCode.Created, handover.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            var visit = await db.Visits.SingleAsync(x => x.Id == visitId);
            db.Entry(visit).CurrentValues.SetValues(visit with { Status = VisitStatus.Completed });
            await db.SaveChangesAsync();
        }

        var timeline = await manager.GetStringAsync($"/api/phase1/care-documentation/service-users/{serviceUserId}/timeline");
        Assert.Contains("CareNote", timeline);
        Assert.Contains("Observation", timeline);
        Assert.Contains("Handover", timeline);

        var invoiceBatch = await finance.PostAsJsonAsync("/api/phase1/finance/invoice-batches", new { periodStart = starts.AddHours(-1), periodEnd = starts.AddDays(1), defaultHourlyRate = 25m, mileageRate = 0m });
        Assert.Equal(HttpStatusCode.Created, invoiceBatch.StatusCode);
        var invoicePayload = (await invoiceBatch.Content.ReadFromJsonAsync<InvoiceBatchDto>())!;
        Assert.Contains(invoicePayload.Invoices, x => x.ServiceUserId == serviceUserId && x.Amount == 30m);

        var payroll = await finance.PostAsJsonAsync("/api/phase1/finance/payroll-batches", new { periodStart = starts.AddHours(-1), periodEnd = starts.AddDays(1), defaultHourlyRate = 18m, mileageRate = 0m });
        Assert.Equal(HttpStatusCode.Created, payroll.StatusCode);

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x => x.Action == "care_plan_task.created" && x.EntityId == visitTask.CarePlanTaskId));
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x => x.Action == "visit_task.outcome_recorded" && x.EntityId == visitTask.Id));
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x => x.Action == "visit.observation_recorded"));
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x => x.Action == "care_note.reviewed"));
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x => x.Action == "visit_handover.created"));
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x => x.Action == "finance.invoice_batch_generated"));
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x => x.Action == "finance.payroll_batch_generated"));
    }

    private async Task<LifecycleDto> PostLifecycle(HttpClient client, Guid planId, string action, long expectedRevision, string comment)
    {
        var response = await client.PostAsJsonAsync($"/api/phase1/care-plans/{planId}/{action}", new { expectedRevision, comment });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LifecycleDto>())!;
    }

    private async Task<LifecycleDto> Sign(HttpClient client, Guid planId, long expectedRevision, string signerType, string signerName, string relationship)
    {
        var response = await client.PostAsJsonAsync($"/api/phase1/care-plans/{planId}/signatures", new { expectedRevision, signerType, signerName, relationship, declaration = "I confirm review of this care plan version.", signatureMethod = "AuthenticatedConfirmation" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LifecycleDto>())!;
    }

    private async Task<LifecycleDto> PostActivate(HttpClient client, Guid planId, long expectedRevision)
    {
        var response = await client.PostAsJsonAsync($"/api/phase1/care-plans/{planId}/activate", new { expectedRevision });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LifecycleDto>())!;
    }

    private async Task<LifecycleDto> GetLifecycle(HttpClient client, Guid carePlanId)
    {
        var response = await client.GetAsync($"/api/phase1/care-plans/{carePlanId}/lifecycle");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LifecycleDto>())!;
    }

    private static AppUser User(string name, UserRole role, Guid? worker) => new(Guid.NewGuid(), name, $"{name}@aicare.local", PasswordHasher.HashPassword("Admin123!"), role, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId, worker, null);

    private async Task<HttpClient> Client(string name)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { userName = name, password = "Admin123!", mfaCode = (string?)null });
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<LoginDto>())!.Token);
        return client;
    }

    private sealed record LoginDto(string Token);
    private sealed record CarePlanDto(Guid Id, Guid ServiceUserId, string Version, string Status);
    private sealed record VersionDto(Guid Id, Guid CarePlanId, Guid ServiceUserId, int VersionNumber, Guid? PreviousCarePlanId, string ChangeReason, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long Revision);
    private sealed record SignatureDto(Guid Id, string SignerType, string SignerName);
    private sealed record AcknowledgementDto(Guid Id, Guid CareWorkerId, string AcknowledgedBy, DateTimeOffset AcknowledgedAt);
    private sealed record EventDto(Guid Id, string FromStatus, string ToStatus, string Reason, string Comment, DateTimeOffset PerformedAt);
    private sealed record LifecycleDto(CarePlanDto CarePlan, VersionDto Version, List<SignatureDto> Signatures, List<AcknowledgementDto> Acknowledgements, List<EventDto> Events, bool RequiredSignaturesSatisfied);
    private sealed record VisitTaskDto(Guid Id, Guid VisitId, Guid? CarePlanTaskId, Guid ServiceUserId, Guid CareWorkerId, string Title, string Category, string Instructions, bool IsRequired, string Status, string Outcome, string ExceptionReason, DateTimeOffset? CompletedAt);
    private sealed record InvoiceBatchDto(int Count, List<InvoiceDto> Invoices);
    private sealed record InvoiceDto(Guid Id, Guid ServiceUserId, decimal Amount);
}
