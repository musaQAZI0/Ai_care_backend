using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed class CarePlanSupersedingRegressionTests : IClassFixture<PostgresRegressionFactory>
{
    private readonly PostgresRegressionFactory _factory;

    public CarePlanSupersedingRegressionTests(PostgresRegressionFactory factory) => _factory = factory;

    [Fact]
    public async Task FullRevisionLifecycleSupersedesPreviousVersionAndProtectsClinicalChildren()
    {
        await _factory.EnsureClinicalSeedAsync();
        var personId = await CreateIsolatedPersonAndWorkerLogin();

        var admin = _factory.CreateClient();
        var adminLogin = await Login(admin, "admin", "Admin123!");
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminLogin.Token);

        var createPlan = await admin.PostAsJsonAsync("/api/phase1/care-plans", new
        {
            serviceUserId = personId,
            personalCare = "Regression personal care",
            medicationSupport = "Regression medication support",
            mobilityAndTransfers = "One worker support",
            nutrition = "Encourage fluids",
            reviewDueAt = DateTimeOffset.UtcNow.AddMonths(3)
        });
        Assert.Equal(HttpStatusCode.Created, createPlan.StatusCode);
        var plan = (await createPlan.Content.ReadFromJsonAsync<CarePlanDto>())!;

        var task = await admin.PostAsJsonAsync($"/api/phase1/care-plans/{plan.Id}/tasks", new
        {
            title = "Regression morning care",
            category = "Personal care",
            instructions = "Follow agreed routine",
            isRequired = true,
            frequency = "Every visit"
        });
        Assert.Equal(HttpStatusCode.Created, task.StatusCode);

        var draft = await GetLifecycle(admin, plan.Id);
        var submitted = await Transition(admin, $"/api/phase1/care-plans/{plan.Id}/submit-review", draft.Version.Revision, new { comment = "Ready" });
        Assert.Equal("InReview", submitted.Version.Status);

        var approved = await Transition(admin, $"/api/phase1/care-plans/{plan.Id}/lifecycle/approve", submitted.Version.Revision, new { comment = "Approved" });
        Assert.Equal("Approved", approved.Version.Status);

        var blockedTask = await admin.PostAsJsonAsync($"/api/phase1/care-plans/{plan.Id}/tasks", new
        {
            title = "Forbidden post-approval task",
            category = "Regression",
            instructions = "Must not persist",
            isRequired = true,
            frequency = "Every visit"
        });
        Assert.Equal(HttpStatusCode.BadRequest, blockedTask.StatusCode);

        await AssertOutcomeMutationIsBlocked(plan.Id, personId);

        var legacyApprove = await admin.PostAsync($"/api/phase1/care-plans/{plan.Id}/approve", null);
        Assert.Equal(HttpStatusCode.InternalServerError, legacyApprove.StatusCode);
        var afterLegacy = await GetLifecycle(admin, plan.Id);
        Assert.Equal("Approved", afterLegacy.Version.Status);

        var managerSigned = await Sign(admin, plan.Id, approved.Version.Revision, "CareManager", "Regression Manager", "Care manager", "AuthenticatedConfirmation");
        Assert.False(managerSigned.RequiredSignaturesSatisfied);
        var personSigned = await Sign(admin, plan.Id, managerSigned.Version.Revision, "Representative", "Regression Representative", "Authorized representative", "RepresentativeConfirmation");
        Assert.Equal("Signed", personSigned.Version.Status);
        Assert.True(personSigned.RequiredSignaturesSatisfied);

        var active = await Transition(admin, $"/api/phase1/care-plans/{plan.Id}/activate", personSigned.Version.Revision, null);
        Assert.Equal("Active", active.Version.Status);

        var worker = _factory.CreateClient();
        var workerLogin = await Login(worker, "regression.worker.superseding", "Worker123!");
        worker.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", workerLogin.Token);
        var acknowledgedResponse = await worker.PostAsJsonAsync($"/api/phase1/care-plans/{plan.Id}/acknowledgements", new { expectedRevision = active.Version.Revision });
        Assert.Equal(HttpStatusCode.OK, acknowledgedResponse.StatusCode);
        var acknowledged = (await acknowledgedResponse.Content.ReadFromJsonAsync<LifecycleDto>())!;
        Assert.Single(acknowledged.Acknowledgements);

        var revisionResponse = await admin.PostAsJsonAsync($"/api/phase1/care-plans/{plan.Id}/revisions", new
        {
            expectedRevision = acknowledged.Version.Revision,
            changeReason = "Mobility reassessment changed support needs"
        });
        Assert.Equal(HttpStatusCode.Created, revisionResponse.StatusCode);
        var revision = (await revisionResponse.Content.ReadFromJsonAsync<LifecycleDto>())!;
        Assert.Equal("Draft", revision.Version.Status);
        Assert.Equal(plan.Id, revision.Version.PreviousCarePlanId);
        Assert.Empty(revision.Signatures);
        Assert.Empty(revision.Acknowledgements);

        var copiedTasks = await admin.GetStringAsync($"/api/phase1/care-plans/{revision.CarePlan.Id}/tasks");
        Assert.Contains("Regression morning care", copiedTasks);

        var revisionSubmitted = await Transition(admin, $"/api/phase1/care-plans/{revision.CarePlan.Id}/submit-review", revision.Version.Revision, new { comment = "Revision ready" });
        var revisionApproved = await Transition(admin, $"/api/phase1/care-plans/{revision.CarePlan.Id}/lifecycle/approve", revisionSubmitted.Version.Revision, new { comment = "Revision approved" });
        var revisionManagerSigned = await Sign(admin, revision.CarePlan.Id, revisionApproved.Version.Revision, "CareManager", "Regression Manager", "Care manager", "AuthenticatedConfirmation");
        var revisionPersonSigned = await Sign(admin, revision.CarePlan.Id, revisionManagerSigned.Version.Revision, "Representative", "Regression Representative", "Authorized representative", "RepresentativeConfirmation");
        var revisionActive = await Transition(admin, $"/api/phase1/care-plans/{revision.CarePlan.Id}/activate", revisionPersonSigned.Version.Revision, null);
        Assert.Equal("Active", revisionActive.Version.Status);

        var oldVersion = await GetLifecycle(admin, plan.Id);
        Assert.Equal("Superseded", oldVersion.Version.Status);
        Assert.Contains(oldVersion.Events, x => x.ToStatus == "Superseded");

        var newAcknowledgementResponse = await worker.PostAsJsonAsync($"/api/phase1/care-plans/{revision.CarePlan.Id}/acknowledgements", new { expectedRevision = revisionActive.Version.Revision });
        Assert.Equal(HttpStatusCode.OK, newAcknowledgementResponse.StatusCode);
        var newAcknowledgement = (await newAcknowledgementResponse.Content.ReadFromJsonAsync<LifecycleDto>())!;
        Assert.Single(newAcknowledgement.Acknowledgements);

        var versions = await admin.GetFromJsonAsync<List<VersionDto>>($"/api/phase1/care-plans/service-user/{personId}/versions");
        Assert.NotNull(versions);
        Assert.Contains(versions!, x => x.CarePlanId == plan.Id && x.Status == "Superseded");
        Assert.Contains(versions!, x => x.CarePlanId == revision.CarePlan.Id && x.Status == "Active");
        Assert.Single(versions!.Where(x => x.Status == "Active"));
    }

    private async Task<Guid> CreateIsolatedPersonAndWorkerLogin()
    {
        _ = _factory.CreateClient();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        var personId = Guid.NewGuid();
        db.ServiceUsers.Add(new ServiceUser(
            personId, $"Lifecycle Regression {personId:N}", new DateOnly(1970, 1, 1), "+10000000000",
            "Regression care needs", "Regression Contact +10000000001", "Regression Worker", RiskLevel.Medium,
            "Onboarded", "Regression address", "None", "Regression condition", "Regression funding", "Regression",
            "", "Regression mobility", "Regression cognition", "Regression communication", "Regression preferences",
            "Regression diet", TenantDefaults.OrganizationId, TenantDefaults.BranchId));

        if (!await db.AppUsers.AnyAsync(x => x.UserName == "regression.worker.superseding"))
        {
            db.AppUsers.Add(new AppUser(
                Guid.NewGuid(), "regression.worker.superseding", "regression.worker.superseding@aicare.local",
                PasswordHasher.HashPassword("Worker123!"), UserRole.CareWorker, true,
                TenantDefaults.OrganizationId, TenantDefaults.BranchId, RegressionIds.WorkerId, null));
        }
        await db.SaveChangesAsync();
        return personId;
    }

    private async Task AssertOutcomeMutationIsBlocked(Guid carePlanId, Guid personId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        db.CarePlanOutcomes.Add(new CarePlanOutcome(
            Guid.NewGuid(), carePlanId, personId, "Forbidden outcome", "Must not persist", "None",
            "Regression Admin", "None", "Active", DateTimeOffset.UtcNow.AddMonths(1),
            TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal("P0001", postgres.SqlState);
        Assert.Contains("draft", postgres.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<LifecycleDto> Transition(HttpClient client, string url, long revision, object? extra)
    {
        object body = extra switch
        {
            null => new { expectedRevision = revision },
            _ => MergeRevision(revision, extra)
        };
        var response = await client.PostAsJsonAsync(url, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LifecycleDto>())!;
    }

    private static object MergeRevision(long revision, object extra)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToElement(extra);
        var comment = json.TryGetProperty("comment", out var value) ? value.GetString() : null;
        return new { expectedRevision = revision, comment };
    }

    private static async Task<LifecycleDto> Sign(HttpClient client, Guid planId, long revision, string signerType, string signerName, string relationship, string signatureMethod)
    {
        var response = await client.PostAsJsonAsync($"/api/phase1/care-plans/{planId}/signatures", new
        {
            expectedRevision = revision,
            signerType,
            signerName,
            relationship,
            declaration = "I confirm that I reviewed this exact care plan version and this signature applies to it.",
            signatureMethod
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LifecycleDto>())!;
    }

    private static async Task<LifecycleDto> GetLifecycle(HttpClient client, Guid planId)
    {
        var response = await client.GetAsync($"/api/phase1/care-plans/{planId}/lifecycle");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LifecycleDto>())!;
    }

    private static async Task<LoginDto> Login(HttpClient client, string userName, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { userName, password, mfaCode = (string?)null });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginDto>())!;
    }

    private sealed record LoginDto(string Token, string RefreshToken, int ExpiresInMinutes);
    private sealed record CarePlanDto(Guid Id, Guid ServiceUserId, string Version, string Status);
    private sealed record VersionDto(Guid Id, Guid CarePlanId, Guid ServiceUserId, int VersionNumber, Guid? PreviousCarePlanId, string ChangeReason, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long Revision);
    private sealed record AcknowledgementDto(Guid Id, Guid CareWorkerId, string AcknowledgedBy, DateTimeOffset AcknowledgedAt);
    private sealed record EventDto(Guid Id, string FromStatus, string ToStatus, string Reason, string Comment, DateTimeOffset PerformedAt);
    private sealed record LifecycleDto(CarePlanDto CarePlan, VersionDto Version, List<object> Signatures, List<AcknowledgementDto> Acknowledgements, List<EventDto> Events, bool RequiredSignaturesSatisfied);
}
