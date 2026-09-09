using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed class ProductionRegressionTests : IClassFixture<PostgresRegressionFactory>
{
    private readonly PostgresRegressionFactory _factory;

    public ProductionRegressionTests(PostgresRegressionFactory factory) => _factory = factory;

    [Fact]
    public async Task ProductionHealthAndDatabaseAreHealthy()
    {
        var client = _factory.CreateClient();
        var health = await client.GetAsync("/health");
        var database = await client.GetAsync("/health/db");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, database.StatusCode);
        Assert.Contains("healthy", await database.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshTokenRotatesAndOldTokenCannotBeReused()
    {
        var client = _factory.CreateClient();
        var login = await Login(client);

        var rotatedResponse = await client.PostAsJsonAsync("/api/auth/refresh-token", new { refreshToken = login.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, rotatedResponse.StatusCode);
        var rotated = await rotatedResponse.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.False(string.IsNullOrWhiteSpace(rotated?.RefreshToken));
        Assert.NotEqual(login.RefreshToken, rotated!.RefreshToken);

        var replay = await client.PostAsJsonAsync("/api/auth/refresh-token", new { refreshToken = login.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rotated.Token);
        var logout = await client.PostAsJsonAsync("/api/auth/logout", new { refreshToken = rotated.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, logout.StatusCode);
        var afterLogout = await client.PostAsJsonAsync("/api/auth/refresh-token", new { refreshToken = rotated.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Fact]
    public async Task MedicationSafetyProfileAndMarAuditEventRoundTrip()
    {
        await _factory.EnsureClinicalSeedAsync();
        var client = _factory.CreateClient();
        var login = await Login(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        await StepUpTestGrants.GrantAsync(_factory, client, "medication");

        var profile = await client.PutAsJsonAsync($"/api/phase1/medication-safety/medications/{RegressionIds.MedicationId}/profile", new
        {
            indication = "Pain management",
            prescriber = "Dr Regression",
            form = "Tablet",
            strength = "500 mg",
            startDate = DateTimeOffset.UtcNow.AddDays(-1),
            endDate = (DateTimeOffset?)null,
            doseWindowMinutes = 60,
            maxPrnDoses24h = 4,
            minPrnIntervalMinutes = 240,
            prnIndication = "Pain score 4 or above",
            prnEffectReviewMinutes = 60,
            stockOnHand = 20m,
            reorderLevel = 5m,
            requiresWitness = false,
            lastReconciledAt = DateTimeOffset.UtcNow,
            reconciledBy = "Regression Admin",
            reconciliationStatus = "Verified",
            sourceType = "Prescription",
            sourceReference = "RX-REG-2",
            changeReason = "Initial verified reconciliation"
        });
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        Assert.Contains("Dr Regression", await profile.Content.ReadAsStringAsync());

        var safetyEvent = await client.PostAsJsonAsync($"/api/phase1/medication-safety/mar/{RegressionIds.MarId}/events", new
        {
            eventType = "OmissionReason",
            reason = "Person asleep; manager informed",
            effect = "Dose withheld safely",
            witnessedBy = "",
            stockDelta = (decimal?)null
        });
        Assert.Equal(HttpStatusCode.Created, safetyEvent.StatusCode);

        var events = await client.GetAsync($"/api/phase1/medication-safety/mar/{RegressionIds.MarId}/events");
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        var body = await events.Content.ReadAsStringAsync();
        Assert.Contains("OmissionReason", body);
        Assert.Contains("Person asleep", body);
    }

    [Fact]
    public async Task SafeguardingCaseSupportsActionAndSafeClosure()
    {
        await _factory.EnsureClinicalSeedAsync();
        var client = _factory.CreateClient();
        var login = await Login(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        var create = await client.PostAsJsonAsync("/api/phase1/safeguarding/cases", new
        {
            serviceUserId = RegressionIds.ServiceUserId,
            incidentId = (Guid?)null,
            category = "Neglect",
            concern = "Regression safeguarding concern",
            immediateActions = "Manager informed and person made safe",
            riskLevel = "High",
            externalReferral = "Local authority",
            referralReference = "REG-001",
            owner = "Regression Admin",
            reviewDueAt = DateTimeOffset.UtcNow.AddDays(1)
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<SafeguardingCaseResponse>();
        Assert.NotNull(created);

        var action = await client.PostAsJsonAsync($"/api/phase1/safeguarding/cases/{created!.Id}/actions", new
        {
            actionType = "Protection",
            detail = "Confirm immediate protection plan",
            owner = "Regression Admin",
            dueAt = DateTimeOffset.UtcNow.AddHours(4)
        });
        Assert.Equal(HttpStatusCode.Created, action.StatusCode);
        var actionId = (await action.Content.ReadFromJsonAsync<CreatedId>())!.Id;

        var prematureClose = await client.PutAsJsonAsync($"/api/phase1/safeguarding/cases/{created.Id}", new { status="Closed", immediateActions="Person protected", riskLevel="High", externalReferral="Local authority", referralReference="SG-REG-1", owner="Regression manager", reviewDueAt=(DateTimeOffset?)null, closureSummary="Premature" });
        Assert.Equal(HttpStatusCode.Conflict,prematureClose.StatusCode);
        var complete = await client.PostAsJsonAsync($"/api/phase1/safeguarding/cases/{created.Id}/actions/{actionId}/complete", new { completionEvidence="Protection plan verified by manager" });
        Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);

        var coordinatorName=$"safeguarding.coordinator.{Guid.NewGuid():N}";
        using(var scope=_factory.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();db.AppUsers.Add(new AppUser(Guid.NewGuid(),coordinatorName,$"{coordinatorName}@aicare.local",PasswordHasher.HashPassword("Admin123!"),UserRole.CareCoordinator,true,TenantDefaults.OrganizationId,TenantDefaults.BranchId,null,null));await db.SaveChangesAsync();}
        var coordinator=_factory.CreateClient();var coordinatorLogin=await coordinator.PostAsJsonAsync("/api/auth/login",new{userName=coordinatorName,password="Admin123!",mfaCode=(string?)null});coordinatorLogin.EnsureSuccessStatusCode();coordinator.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await coordinatorLogin.Content.ReadFromJsonAsync<LoginResponse>())!.Token);
        var unauthorizedClose=await coordinator.PutAsJsonAsync($"/api/phase1/safeguarding/cases/{created.Id}",new{status="Closed",immediateActions="Protection plan completed",riskLevel="Low",externalReferral="Local authority",referralReference="REG-001",owner="Coordinator",reviewDueAt=(DateTimeOffset?)null,closureSummary="Coordinator cannot approve closure"});
        Assert.Equal(HttpStatusCode.Forbidden,unauthorizedClose.StatusCode);

        var close = await client.PutAsJsonAsync($"/api/phase1/safeguarding/cases/{created.Id}", new
        {
            status = "Closed",
            immediateActions = "Protection plan completed",
            riskLevel = "Low",
            externalReferral = "Local authority",
            referralReference = "REG-001",
            owner = "Regression Admin",
            reviewDueAt = (DateTimeOffset?)null,
            closureSummary = "Regression case reviewed and safely closed"
        });
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        Assert.Contains("Closed", await close.Content.ReadAsStringAsync());
    }

    private static async Task<LoginResponse> Login(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "Admin123!", mfaCode = (string?)null });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }


    [Fact]
    public async Task MedicationReconciliationSourceEvidenceIsRequiredBeforeVerifiedProfile()
    {
        await _factory.EnsureClinicalSeedAsync();
        var client = _factory.CreateClient();
        var login = await Login(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        await StepUpTestGrants.GrantAsync(_factory, client, "medication");

        var profile = await client.PutAsJsonAsync($"/api/phase1/medication-safety/medications/{RegressionIds.MedicationId}/profile", new
        {
            indication = "Pain management",
            prescriber = "Dr Regression",
            form = "Tablet",
            strength = "500 mg",
            startDate = DateTimeOffset.UtcNow.AddDays(-1),
            endDate = (DateTimeOffset?)null,
            doseWindowMinutes = 60,
            maxPrnDoses24h = 4,
            minPrnIntervalMinutes = 240,
            prnIndication = "Pain score 4 or above",
            prnEffectReviewMinutes = 60,
            stockOnHand = 20m,
            reorderLevel = 5m,
            requiresWitness = false,
            lastReconciledAt = DateTimeOffset.UtcNow,
            reconciledBy = "Regression Admin",
            reconciliationStatus = "Verified",
            sourceType = "",
            sourceReference = "",
            changeReason = ""
        });
        Assert.Equal(HttpStatusCode.BadRequest, profile.StatusCode);
        Assert.Contains("source", await profile.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }
    private sealed record LoginResponse(string Token, string RefreshToken, int ExpiresInMinutes);
    private sealed record CreatedId(Guid Id);
    private sealed record SafeguardingCaseResponse(Guid Id, string Status);
}

[CollectionDefinition("Postgres regression", DisableParallelization = true)]
public sealed class PostgresRegressionCollection;

public sealed class PostgresRegressionFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = Environment.GetEnvironmentVariable("AICARE_REGRESSION_CONNECTION") ?? "Host=localhost;Port=5432;Database=aicare_regression;Username=postgres;Password=postgres",
            ["JwtOptions:Issuer"] = "AiCare",
            ["JwtOptions:Audience"] = "AiCareClient",
            ["JwtOptions:SigningKey"] = "regression-signing-key-with-enough-length-for-hmac-2026",
            ["JwtOptions:TokenLifetimeMinutes"] = "30",
            ["Storage:Provider"] = "Local",
            ["MedicationSafety:EmarProductionEnabled"] = "true",
            ["MedicationSafety:ClinicalSafetyOfficer"] = "Regression CSO",
            ["MedicationSafety:MedicationSafetyLead"] = "Regression medication lead",
            ["MedicationSafety:ClinicalSafetyCaseReference"] = "REG-EMAR-SAFETY",
            ["MedicationSafety:MedicationUatEvidenceReference"] = "REG-EMAR-UAT"
        }));
        builder.ConfigureServices(services =>
        {
            var worker = services.SingleOrDefault(descriptor => descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(IntegrationJobWorker));
            if (worker is not null) services.Remove(worker);
        });
    }

    public async Task EnsureClinicalSeedAsync()
    {
        _ = CreateClient();
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        await db.Database.ExecuteSqlRawAsync("delete from auth_mfa_policies");
        var admin = await db.AppUsers.SingleOrDefaultAsync(user => user.UserName == "admin");
        if (admin is null)
            db.AppUsers.Add(new AppUser(Guid.NewGuid(), "admin", "admin@aicare.local", PasswordHasher.HashPassword("Admin123!"), UserRole.Administrator, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId, null, null));
        else
            db.Entry(admin).CurrentValues.SetValues(admin with { PasswordHash = PasswordHasher.HashPassword("Admin123!"), IsActive = true });
        if (await db.ServiceUsers.FindAsync(RegressionIds.ServiceUserId) is null)
            db.ServiceUsers.Add(new ServiceUser(RegressionIds.ServiceUserId, "Regression Person", new DateOnly(1980, 1, 1), "07000000000", "Personal care", "Regression Contact", "Regression Worker", RiskLevel.Medium, "Active", "1 Test Street", "None", "None", "Local authority", "Other", "", "Independent", "Full capacity", "Verbal", "None", "Standard", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        if (await db.CareWorkers.FindAsync(RegressionIds.WorkerId) is null)
            db.CareWorkers.Add(new CareWorker(RegressionIds.WorkerId, "Regression Worker", "Medication support", "Available", 1, 50, "Valid", "Compliant", "10 miles", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        if (await db.Visits.FindAsync(RegressionIds.VisitId) is null)
            db.Visits.Add(new Visit(RegressionIds.VisitId, RegressionIds.ServiceUserId, RegressionIds.WorkerId, DateTimeOffset.UtcNow.AddHours(1), "Medication visit", 30, "Medication support", VisitStatus.Scheduled, null, null, null, null, null, null, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        if (await db.Medications.FindAsync(RegressionIds.MedicationId) is null)
            db.Medications.Add(new Medication(RegressionIds.MedicationId, RegressionIds.ServiceUserId, "Paracetamol", "500 mg", "Oral", "PRN", true, "Regression Pharmacy", "None", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        if (await db.MedicationAdministrationRecords.FindAsync(RegressionIds.MarId) is null)
            db.MedicationAdministrationRecords.Add(new MedicationAdministrationRecord(RegressionIds.MarId, RegressionIds.MedicationId, RegressionIds.VisitId, RegressionIds.WorkerId, DateTimeOffset.UtcNow.AddHours(1), null, "Scheduled", "", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("update auth_user_security set failed_attempts=0, lockout_until=null where user_id in (select \"Id\" from \"AppUsers\")");
    }
    public async Task SeedCarePlanActivationPrerequisitesAsync(Guid personId)
    {
        using var scope=Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();var now=DateTimeOffset.UtcNow;var review=now.AddMonths(6);
        await db.Database.ExecuteSqlRawAsync("insert into capacity_decisions(id,service_user_id,organization_id,branch_id,decision_context,assessment_reason,outcome,can_understand,can_retain,can_use_or_weigh,can_communicate,support_provided,assessor_name,assessor_role,evidence_reference,assessed_at,review_due_at,status,version,supersedes_id,best_interest_required,created_by) values({0},{1},{2},{3},'Care plan agreement','Regression prerequisite','HasCapacity',true,true,true,true,'Accessible explanation','Regression assessor','Care manager','CAP-REG',{4},{5},'Current',1,null,false,'regression')",Guid.NewGuid(),personId,TenantDefaults.OrganizationId,TenantDefaults.BranchId,now,review);
        await db.Database.ExecuteSqlRawAsync("insert into consent_records(id,service_user_id,organization_id,branch_id,consent_type,scope,status,capacity_basis,decision_maker,evidence_reference,effective_from,expires_at,withdrawn_at,withdrawal_reason,created_at,version,supersedes_id,information_categories,sharing_parties,review_due_at) values({0},{1},{2},{3},'Care and support','Care-plan delivery','Active','Person has capacity','Service user','CONSENT-REG',{4},{5},null,'',now(),1,null,'Care records','Assigned care team',{5})",Guid.NewGuid(),personId,TenantDefaults.OrganizationId,TenantDefaults.BranchId,now,review);
        await db.Database.ExecuteSqlRawAsync("insert into governed_assessments(id,service_user_id,organization_id,branch_id,assessment_type,template_key,template_version,answers_json,score,risk_level,summary,recommended_actions,status,version,supersedes_id,change_reason,assessor_name,assessor_role,review_due_at,submitted_at,approved_at,approved_by,signed_at,signed_by,signature_declaration,created_by) values({0},{1},{2},{3},'Initial needs','initial-needs','1.0','{{}}'::jsonb,5,'Medium','Regression assessment','Follow care plan','Current',1,null,'','Regression assessor','Care manager',{4},now(),now(),'Regression manager',now(),'Regression manager','Approved exact version','regression')",Guid.NewGuid(),personId,TenantDefaults.OrganizationId,TenantDefaults.BranchId,review);
        await db.Database.ExecuteSqlRawAsync("insert into governed_risk_assessments(id,service_user_id,organization_id,branch_id,category,hazard,likelihood,severity,score,risk_level,controls,contingency,owner,status,version,supersedes_id,change_reason,review_due_at,submitted_at,approved_at,approved_by,signed_at,signed_by,signature_declaration,created_by) values({0},{1},{2},{3},'General safety','Care delivery hazards',2,2,4,'Low','Follow care plan','Escalate changes','Care manager','Current',1,null,'',{4},now(),now(),'Regression manager',now(),'Regression manager','Approved exact version','regression')",Guid.NewGuid(),personId,TenantDefaults.OrganizationId,TenantDefaults.BranchId,review);
    }
}

internal static class RegressionIds
{
    internal static readonly Guid ServiceUserId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    internal static readonly Guid WorkerId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    internal static readonly Guid VisitId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
    internal static readonly Guid MedicationId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004");
    internal static readonly Guid MarId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000005");
}
