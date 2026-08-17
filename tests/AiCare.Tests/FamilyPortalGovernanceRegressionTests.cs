using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed class FamilyPortalGovernanceRegressionTests : IClassFixture<PostgresRegressionFactory>
{
    private readonly PostgresRegressionFactory _factory;

    public FamilyPortalGovernanceRegressionTests(PostgresRegressionFactory factory) => _factory = factory;

    [Fact]
    public async Task VerifiedRepresentativeActivationPermissionsCarePlanAndRevocationAreEnforced()
    {
        var seed = await CreateIsolatedFamilyScenario();
        var admin = _factory.CreateClient();
        var adminLogin = await Login(admin, "admin", "Admin123!");
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminLogin.Token);

        var invalidVerification = await admin.PutAsJsonAsync($"/api/phase1/family-access/{seed.FamilyMemberId}", new
        {
            authorityType = "Authorized representative",
            verificationStatus = "Verified",
            verificationReference = "",
            permissions = new[] { "ViewCareSummary" }
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidVerification.StatusCode);

        var configured = await ConfigureAccess(admin, seed.FamilyMemberId, null, new[]
        {
            "ViewCareSummary", "ViewTimeline", "ViewVisits", "ViewAppointments", "ViewCarePlan", "SubmitFeedback"
        });
        Assert.Equal("Verified", configured.VerificationStatus);
        Assert.Equal("Active", configured.AccessStatus);
        Assert.DoesNotContain("SignCarePlan", configured.Permissions);

        var invite = await admin.PostAsync($"/api/phase1/family-access/{seed.FamilyMemberId}/invite", null);
        Assert.Equal(HttpStatusCode.OK, invite.StatusCode);
        var invitation = (await invite.Content.ReadFromJsonAsync<InvitationDto>())!;
        Assert.Equal("Sent", invitation.Status);
        Assert.False(string.IsNullOrWhiteSpace(invitation.DevelopmentActivationUrl));
        var token = Uri.UnescapeDataString(invitation.DevelopmentActivationUrl!.Split("token=", 2)[1]);
        Assert.True(token.Length >= 40);

        var storedHash = await GetInvitationHash(invitation.InvitationId);
        Assert.NotEqual(token, storedHash);
        Assert.Equal(64, storedHash.Length);

        var validate = await admin.PostAsJsonAsync("/api/family/invitations/validate", new { token });
        Assert.Equal(HttpStatusCode.OK, validate.StatusCode);
        var validation = (await validate.Content.ReadFromJsonAsync<ValidationDto>())!;
        Assert.True(validation.Valid);

        var rejectedTerms = await admin.PostAsJsonAsync("/api/family/invitations/accept", new
        {
            token,
            password = "FamilySecure123!",
            acceptTerms = false
        });
        Assert.Equal(HttpStatusCode.BadRequest, rejectedTerms.StatusCode);

        var accepted = await admin.PostAsJsonAsync("/api/family/invitations/accept", new
        {
            token,
            password = "FamilySecure123!",
            acceptTerms = true
        });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var replay = await admin.PostAsJsonAsync("/api/family/invitations/accept", new
        {
            token,
            password = "FamilySecure123!",
            acceptTerms = true
        });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);

        var family = _factory.CreateClient();
        var familyLogin = await Login(family, seed.Email, "FamilySecure123!");
        family.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", familyLogin.Token);

        var people = await family.GetAsync("/api/family/people");
        Assert.Equal(HttpStatusCode.OK, people.StatusCode);
        var peopleBody = await people.Content.ReadAsStringAsync();
        Assert.Contains(seed.PersonId.ToString(), peopleBody, StringComparison.OrdinalIgnoreCase);

        var ownOverview = await family.GetAsync($"/api/family/service-users/{seed.PersonId}/overview");
        Assert.Equal(HttpStatusCode.OK, ownOverview.StatusCode);
        var wrongOverview = await family.GetAsync($"/api/family/service-users/{Guid.NewGuid()}/overview");
        Assert.Equal(HttpStatusCode.Forbidden, wrongOverview.StatusCode);

        var feedback = await family.PostAsJsonAsync("/api/family/feedback", new
        {
            serviceUserId = seed.PersonId,
            type = "Concern",
            subject = "Regression family concern",
            description = "Please review this governed family concern.",
            priority = "Medium"
        });
        Assert.Equal(HttpStatusCode.Created, feedback.StatusCode);

        var plan = await CreateApprovedManagerSignedPlan(admin, seed.PersonId);
        var lifecycleRead = await family.GetAsync($"/api/phase1/care-plans/{plan.CarePlanId}/lifecycle");
        Assert.Equal(HttpStatusCode.OK, lifecycleRead.StatusCode);

        var forbiddenSignature = await family.PostAsJsonAsync($"/api/phase1/care-plans/{plan.CarePlanId}/signatures", new
        {
            expectedRevision = plan.Revision,
            signerType = "Representative",
            signerName = "Regression Family Representative",
            relationship = "Authorized representative",
            declaration = "I confirm that I reviewed this exact care plan version and this signature applies to it.",
            signatureMethod = "RepresentativeConfirmation"
        });
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenSignature.StatusCode);

        configured = await ConfigureAccess(admin, seed.FamilyMemberId, configured.Revision, configured.Permissions.Append("SignCarePlan").ToArray());
        Assert.Contains("SignCarePlan", configured.Permissions);

        var allowedSignature = await family.PostAsJsonAsync($"/api/phase1/care-plans/{plan.CarePlanId}/signatures", new
        {
            expectedRevision = plan.Revision,
            signerType = "Representative",
            signerName = "Regression Family Representative",
            relationship = "Authorized representative",
            declaration = "I confirm that I reviewed this exact care plan version and this signature applies to it.",
            signatureMethod = "RepresentativeConfirmation"
        });
        Assert.Equal(HttpStatusCode.OK, allowedSignature.StatusCode);
        var signedBody = await allowedSignature.Content.ReadAsStringAsync();
        Assert.Contains("Signed", signedBody);

        var suspend = await admin.PostAsync($"/api/phase1/family-access/{seed.FamilyMemberId}/suspend", null);
        Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);
        var blockedWhileSuspended = await family.GetAsync($"/api/family/service-users/{seed.PersonId}/overview");
        Assert.Equal(HttpStatusCode.Forbidden, blockedWhileSuspended.StatusCode);

        var restore = await admin.PostAsync($"/api/phase1/family-access/{seed.FamilyMemberId}/restore", null);
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        var restoredOverview = await family.GetAsync($"/api/family/service-users/{seed.PersonId}/overview");
        Assert.Equal(HttpStatusCode.OK, restoredOverview.StatusCode);

        var revoke = await admin.PostAsync($"/api/phase1/family-access/{seed.FamilyMemberId}/revoke", null);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        var blockedAfterRevoke = await family.GetAsync($"/api/family/service-users/{seed.PersonId}/overview");
        Assert.Equal(HttpStatusCode.Forbidden, blockedAfterRevoke.StatusCode);

        var relogin = await family.PostAsJsonAsync("/api/auth/login", new { userName = seed.Email, password = "FamilySecure123!", mfaCode = (string?)null });
        Assert.Equal(HttpStatusCode.Unauthorized, relogin.StatusCode);

        await AssertAuditEvents(seed.FamilyMemberId);
    }

    private async Task<ScenarioSeed> CreateIsolatedFamilyScenario()
    {
        _ = _factory.CreateClient();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        var personId = Guid.NewGuid();
        var familyMemberId = Guid.NewGuid();
        var email = $"family.regression.{familyMemberId:N}@aicare.local";
        db.ServiceUsers.Add(new ServiceUser(
            personId, $"Family Regression {personId:N}", new DateOnly(1955, 3, 10), "+440000000000",
            "Regression support needs", "Regression emergency contact", "Regression worker", RiskLevel.Low,
            "Onboarded", "Regression address", "None", "Regression condition", "Private", "Other", "",
            "Independent", "Full capacity", "Verbal", "None", "Standard",
            TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        db.FamilyMembers.Add(new FamilyMember(
            familyMemberId, personId, "Regression Family Representative", email,
            "Daughter", "None", "Pending", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        await db.SaveChangesAsync();
        return new ScenarioSeed(personId, familyMemberId, email);
    }

    private static async Task<FamilyAccessDto> ConfigureAccess(HttpClient admin, Guid familyMemberId, long? revision, IReadOnlyCollection<string> permissions)
    {
        var response = await admin.PutAsJsonAsync($"/api/phase1/family-access/{familyMemberId}", new
        {
            authorityType = "Authorized representative",
            verificationStatus = "Verified",
            verificationReference = "Identity and authority evidence checked by regression manager",
            validFrom = DateTimeOffset.UtcNow.AddMinutes(-5),
            validUntil = DateTimeOffset.UtcNow.AddYears(1),
            permissions,
            expectedRevision = revision
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<FamilyAccessDto>())!;
    }

    private static async Task<PlanState> CreateApprovedManagerSignedPlan(HttpClient admin, Guid personId)
    {
        var create = await admin.PostAsJsonAsync("/api/phase1/care-plans", new
        {
            serviceUserId = personId,
            personalCare = "Family regression personal care",
            medicationSupport = "Family regression medication support",
            mobilityAndTransfers = "Family regression mobility",
            nutrition = "Family regression nutrition",
            reviewDueAt = DateTimeOffset.UtcNow.AddMonths(3)
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var carePlanId = created.RootElement.GetProperty("id").GetGuid();

        var lifecycle = await admin.GetFromJsonAsync<LifecycleDto>($"/api/phase1/care-plans/{carePlanId}/lifecycle");
        Assert.NotNull(lifecycle);
        var review = await admin.PostAsJsonAsync($"/api/phase1/care-plans/{carePlanId}/submit-review", new { expectedRevision = lifecycle!.Version.Revision, comment = "Family regression review" });
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        var reviewed = (await review.Content.ReadFromJsonAsync<LifecycleDto>())!;
        var approve = await admin.PostAsJsonAsync($"/api/phase1/care-plans/{carePlanId}/lifecycle/approve", new { expectedRevision = reviewed.Version.Revision, comment = "Family regression approved" });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        var approved = (await approve.Content.ReadFromJsonAsync<LifecycleDto>())!;
        var managerSign = await admin.PostAsJsonAsync($"/api/phase1/care-plans/{carePlanId}/signatures", new
        {
            expectedRevision = approved.Version.Revision,
            signerType = "CareManager",
            signerName = "Regression Manager",
            relationship = "Care manager",
            declaration = "I confirm that I reviewed this exact care plan version and this signature applies to it.",
            signatureMethod = "AuthenticatedConfirmation"
        });
        Assert.Equal(HttpStatusCode.OK, managerSign.StatusCode);
        var managerSigned = (await managerSign.Content.ReadFromJsonAsync<LifecycleDto>())!;
        return new PlanState(carePlanId, managerSigned.Version.Revision);
    }

    private async Task<string> GetInvitationHash(Guid invitationId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "select token_hash from family_portal_invitations where id = @id";
        command.Parameters.AddWithValue("id", invitationId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task AssertAuditEvents(Guid familyMemberId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        var actions = await db.AuditEvents.AsNoTracking()
            .Where(x => x.OrganizationId == TenantDefaults.OrganizationId && (x.EntityId == familyMemberId || x.Action == "family.feedback_submitted"))
            .Select(x => x.Action)
            .ToListAsync();
        Assert.Contains("family.access_configured", actions);
        Assert.Contains("family.invited", actions);
        Assert.Contains("family.invitation_accepted", actions);
        Assert.Contains("family.access_suspended", actions);
        Assert.Contains("family.access_revoked", actions);
    }

    private static async Task<LoginDto> Login(HttpClient client, string userName, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { userName, password, mfaCode = (string?)null });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginDto>())!;
    }

    private sealed record ScenarioSeed(Guid PersonId, Guid FamilyMemberId, string Email);
    private sealed record PlanState(Guid CarePlanId, long Revision);
    private sealed record LoginDto(string Token, string RefreshToken, int ExpiresInMinutes);
    private sealed record InvitationDto(Guid InvitationId, string Status, DateTimeOffset ExpiresAt, string? DevelopmentActivationUrl);
    private sealed record ValidationDto(bool Valid, string Status, string Message);
    private sealed record FamilyAccessDto(string VerificationStatus, string AccessStatus, long Revision, List<string> Permissions);
    private sealed record VersionDto(long Revision, string Status);
    private sealed record LifecycleDto(VersionDto Version);
}
