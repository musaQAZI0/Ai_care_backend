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
public sealed class CarePlanLifecycleRegressionTests : IClassFixture<PostgresRegressionFactory>
{
    private readonly PostgresRegressionFactory _factory;

    public CarePlanLifecycleRegressionTests(PostgresRegressionFactory factory) => _factory = factory;

    [Fact]
    public async Task CarePlanLifecycleIsVersionedSignedImmutableAndConcurrencySafe()
    {
        await _factory.EnsureClinicalSeedAsync();
        await EnsureWorkerLoginAsync();

        var admin = _factory.CreateClient();
        var adminLogin = await Login(admin, "admin", "Admin123!");
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminLogin.Token);

        var createPlan = await admin.PostAsJsonAsync("/api/phase1/care-plans", new
        {
            serviceUserId = RegressionIds.ServiceUserId,
            personalCare = "Support with morning personal care",
            medicationSupport = "Prompt and record medication support",
            mobilityAndTransfers = "One worker support",
            nutrition = "Encourage fluids and balanced meals",
            reviewDueAt = DateTimeOffset.UtcNow.AddMonths(3)
        });
        Assert.Equal(HttpStatusCode.Created, createPlan.StatusCode);
        var plan = await createPlan.Content.ReadFromJsonAsync<CarePlanDto>();
        Assert.NotNull(plan);

        var task = await admin.PostAsJsonAsync($"/api/phase1/care-plans/{plan!.Id}/tasks", new
        {
            title = "Morning personal care",
            category = "Personal care",
            instructions = "Follow the person's preferred routine",
            isRequired = true,
            frequency = "Every visit"
        });
        Assert.Equal(HttpStatusCode.Created, task.StatusCode);

        var draft = await GetLifecycle(admin, plan.Id);
        Assert.Equal("Draft", draft.Version.Status);
        Assert.Equal(1, draft.Version.VersionNumber);
        Assert.Equal(1, draft.Version.Revision);

        var submittedResponse = await admin.PostAsJsonAsync($"/api/phase1/care-plans/{plan.Id}/submit-review", new
        {
            expectedRevision = draft.Version.Revision,
            comment = "Ready for manager review"
        });
        Assert.Equal(HttpStatusCode.OK, submittedResponse.StatusCode);
        var submitted = (await submittedResponse.Content.ReadFromJsonAsync<LifecycleDto>())!;
        Assert.Equal("InReview", submitted.Version.Status);

        var staleApprove = await admin.PostAsJsonAsync($"/api/phase1/care-plans/{plan.Id}/approve", new
        {
            expectedRevision = draft.Version.Revision,
            comment = "This request is deliberately stale"
        });
        Assert.Equal(HttpStatusCode.Conflict, staleApprove.StatusCode);

        var approvedResponse = await admin.PostAsJsonAsync($"/api/phase1/care-plans/{plan.Id}/approve", new
        {
            expectedRevision = submitted.Version.Revision,
            comment = "Clinical content reviewed"
        });
        Assert.Equal(HttpStatusCode.OK, approvedResponse.StatusCode);
        var approved = (await approvedResponse.Content.ReadFromJsonAsync<LifecycleDto>())!;
        Assert.Equal("Approved", approved.Version.Status);

        await AssertClinicalContentIsImmutableAsync(plan.Id);

        var managerSignatureResponse = await admin.PostAsJsonAsync($"/api/phase1/care-plans/{plan.Id}/signatures", new
        {
            expectedRevision = approved.Version.Revision,
            signerType = "CareManager",
            signerName = "Regression Admin",
            relationship = "Care manager",
            declaration = "I confirm that I reviewed and approved this exact care plan version.",
            signatureMethod = "AuthenticatedConfirmation"
        });
        Assert.Equal(HttpStatusCode.OK, managerSignatureResponse.StatusCode);
        var managerSigned = (await managerSignatureResponse.Content.ReadFromJsonAsync<LifecycleDto>())!;
        Assert.Equal("Approved", managerSigned.Version.Status);
        Assert.False(managerSigned.RequiredSignaturesSatisfied);

        var blockedActivation = await admin.PostAsJsonAsync($"/api/phase1/care-plans/{plan.Id}/activate", new
        {
            expectedRevision = managerSigned.Version.Revision
        });
        Assert.Equal(HttpStatusCode.BadRequest, blockedActivation.StatusCode);

        var representativeSignatureResponse = await admin.PostAsJsonAsync($"/api/phase1/care-plans/{plan.Id}/signatures", new
        {
            expectedRevision = managerSigned.Version.Revision,
            signerType = "Representative",
            signerName = "Regression Representative",
            relationship = "Authorized representative",
            declaration = "I confirm that the care plan has been reviewed with the person and this signature applies to this version.",
            signatureMethod = "RepresentativeConfirmation"
        });
        Assert.Equal(HttpStatusCode.OK, representativeSignatureResponse.StatusCode);
        var signed = (await representativeSignatureResponse.Content.ReadFromJsonAsync<LifecycleDto>())!;
        Assert.Equal("Signed", signed.Version.Status);
        Assert.True(signed.RequiredSignaturesSatisfied);
        Assert.Equal(2, signed.Signatures.Count);

        var activateResponse = await admin.PostAsJsonAsync($"/api/phase1/care-plans/{plan.Id}/activate", new
        {
            expectedRevision = signed.Version.Revision
        });
        Assert.Equal(HttpStatusCode.OK, activateResponse.StatusCode);
        var active = (await activateResponse.Content.ReadFromJsonAsync<LifecycleDto>())!;
        Assert.Equal("Active", active.Version.Status);

        var worker = _factory.CreateClient();
        var workerLogin = await Login(worker, "regression.worker", "Worker123!");
        worker.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", workerLogin.Token);
        var acknowledgeResponse = await worker.PostAsJsonAsync($"/api/phase1/care-plans/{plan.Id}/acknowledgements", new
        {
            expectedRevision = active.Version.Revision
        });
        Assert.Equal(HttpStatusCode.OK, acknowledgeResponse.StatusCode);
        var acknowledged = (await acknowledgeResponse.Content.ReadFromJsonAsync<LifecycleDto>())!;
        Assert.Single(acknowledged.Acknowledgements);
        Assert.Equal(RegressionIds.WorkerId, acknowledged.Acknowledgements[0].CareWorkerId);

        var revisionResponse = await admin.PostAsJsonAsync($"/api/phase1/care-plans/{plan.Id}/revisions", new
        {
            expectedRevision = acknowledged.Version.Revision,
            changeReason = "Mobility support changed after reassessment"
        });
        Assert.Equal(HttpStatusCode.Created, revisionResponse.StatusCode);
        var revision = (await revisionResponse.Content.ReadFromJsonAsync<LifecycleDto>())!;
        Assert.NotEqual(plan.Id, revision.CarePlan.Id);
        Assert.Equal("Draft", revision.Version.Status);
        Assert.Equal(2, revision.Version.VersionNumber);
        Assert.Equal(plan.Id, revision.Version.PreviousCarePlanId);
        Assert.Contains("Mobility", revision.Version.ChangeReason, StringComparison.OrdinalIgnoreCase);

        var copiedTasks = await admin.GetAsync($"/api/phase1/care-plans/{revision.CarePlan.Id}/tasks");
        Assert.Equal(HttpStatusCode.OK, copiedTasks.StatusCode);
        Assert.Contains("Morning personal care", await copiedTasks.Content.ReadAsStringAsync());

        var stillActive = await GetLifecycle(admin, plan.Id);
        Assert.Equal("Active", stillActive.Version.Status);

        var versionsResponse = await admin.GetAsync($"/api/phase1/care-plans/service-user/{RegressionIds.ServiceUserId}/versions");
        Assert.Equal(HttpStatusCode.OK, versionsResponse.StatusCode);
        var versions = (await versionsResponse.Content.ReadFromJsonAsync<List<VersionDto>>())!;
        Assert.Contains(versions, x => x.CarePlanId == plan.Id && x.Status == "Active");
        Assert.Contains(versions, x => x.CarePlanId == revision.CarePlan.Id && x.Status == "Draft");

        var lifecycle = await GetLifecycle(admin, plan.Id);
        Assert.Contains(lifecycle.Events, x => x.ToStatus == "InReview");
        Assert.Contains(lifecycle.Events, x => x.ToStatus == "Approved");
        Assert.Contains(lifecycle.Events, x => x.ToStatus == "Signed");
        Assert.Contains(lifecycle.Events, x => x.ToStatus == "Active");
    }

    private async Task EnsureWorkerLoginAsync()
    {
        _ = _factory.CreateClient();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        if (!await db.AppUsers.AnyAsync(x => x.UserName == "regression.worker"))
        {
            db.AppUsers.Add(new AppUser(
                Guid.Parse("aaaaaaaa-0000-0000-0000-000000000006"),
                "regression.worker",
                "regression.worker@aicare.local",
                PasswordHasher.HashPassword("Worker123!"),
                UserRole.CareWorker,
                true,
                TenantDefaults.OrganizationId,
                TenantDefaults.BranchId,
                RegressionIds.WorkerId,
                null));
            await db.SaveChangesAsync();
        }
    }

    private async Task AssertClinicalContentIsImmutableAsync(Guid carePlanId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        var plan = await db.CarePlans.AsNoTracking().SingleAsync(x => x.Id == carePlanId);
        var changed = plan with { PersonalCare = "Unsafe silent overwrite" };
        db.CarePlans.Update(changed);
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.SaveChangesAsync());
        Assert.Contains("immutable", error.MessageText, StringComparison.OrdinalIgnoreCase);
        db.ChangeTracker.Clear();
    }

    private static async Task<LifecycleDto> GetLifecycle(HttpClient client, Guid carePlanId)
    {
        var response = await client.GetAsync($"/api/phase1/care-plans/{carePlanId}/lifecycle");
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
    private sealed record SignatureDto(Guid Id, string SignerType, string SignerName);
    private sealed record AcknowledgementDto(Guid Id, Guid CareWorkerId, string AcknowledgedBy, DateTimeOffset AcknowledgedAt);
    private sealed record EventDto(Guid Id, string FromStatus, string ToStatus, string Reason, string Comment, DateTimeOffset PerformedAt);
    private sealed record LifecycleDto(CarePlanDto CarePlan, VersionDto Version, List<SignatureDto> Signatures, List<AcknowledgementDto> Acknowledgements, List<EventDto> Events, bool RequiredSignaturesSatisfied);
}
