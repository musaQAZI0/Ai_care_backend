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
public sealed class VisitClinicalInstructionsRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task AssignedWorkerReceivesOnlyCurrentSignedInstructionsAndOverdueFlag()
    {
        var data = await Seed("Assigned");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        foreach (var status in new[] { "Draft", "InReview", "Superseded", "Current" })
        {
            var signed = status == "Current";
            var marker = signed ? "APPROVED" : $"{status}-SECRET";
            await Assessment(db, data.Person, TenantDefaults.OrganizationId, TenantDefaults.BranchId, "Mobility", status, marker, signed);
            await Risk(db, data.Person, status, marker, signed);
        }
        await Assessment(db, data.Person, TenantDefaults.OrganizationId, TenantDefaults.BranchId, "Unsigned", "Current", "UNSIGNED-SECRET", false);
        await Assessment(db, data.Person, Guid.NewGuid(), TenantDefaults.BranchId, "Foreign", "Current", "TENANT-SECRET", true);
        await Assessment(db, data.Person, TenantDefaults.OrganizationId, Guid.NewGuid(), "Other branch", "Current", "BRANCH-SECRET", true);
        using var client = await Login(data.Actor);
        var response = await client.GetAsync(Path(data.Visit));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore == true);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SECRET", text);
        var payload = JsonSerializer.Deserialize<JsonElement>(text);
        var instructions = payload.GetProperty("instructions").EnumerateArray().ToArray();
        Assert.Equal(2, instructions.Length);
        Assert.All(instructions, item => Assert.Contains("APPROVED", item.GetProperty("instructions").GetString()!));
        Assert.True(instructions.Single(item => item.GetProperty("kind").GetString() == "Risk").GetProperty("reviewOverdue").GetBoolean());
        Assert.False(instructions.Single(item => item.GetProperty("kind").GetString() == "Assessment").GetProperty("reviewOverdue").GetBoolean());
        Assert.True(await db.AuditEvents.AnyAsync(item => item.Action == "visit.clinical_instructions_viewed" && item.EntityId == data.Visit && item.Actor == data.Actor));
        await db.Database.ExecuteSqlInterpolatedAsync($"update governed_assessments set status='Superseded' where service_user_id={data.Person} and organization_id={TenantDefaults.OrganizationId} and branch_id={TenantDefaults.BranchId} and assessment_type='Mobility' and status='Current'");
        await Assessment(db, data.Person, TenantDefaults.OrganizationId, TenantDefaults.BranchId, "Mobility", "Current", "REPLACEMENT-APPROVED", true);
        var refreshed = await client.GetFromJsonAsync<JsonElement>(Path(data.Visit));
        var latest = refreshed.GetProperty("instructions").EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "Assessment");
        Assert.Equal("REPLACEMENT-APPROVED", latest.GetProperty("instructions").GetString());
    }

    [Theory]
    [InlineData("Unassigned")]
    [InlineData("OtherBranch")]
    [InlineData("OtherTenant")]
    [InlineData("Unpublished")]
    public async Task WorkerCannotReadInstructionsOutsideVisibleAssignedVisit(string mode)
    {
        var data = await Seed(mode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        await Assessment(db, data.Person, TenantDefaults.OrganizationId, TenantDefaults.BranchId, "Mobility", "Current", "PRIVATE-INSTRUCTION", true);
        using var client = await Login(data.Actor);
        var response = await client.GetAsync(Path(data.Visit));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("PRIVATE-INSTRUCTION", await response.Content.ReadAsStringAsync());
        Assert.False(await db.AuditEvents.AnyAsync(item => item.Action == "visit.clinical_instructions_viewed" && item.EntityId == data.Visit));
    }

    private async Task<(Guid Person, Guid Visit, string Actor)> Seed(string mode)
    {
        await factory.EnsureClinicalSeedAsync();
        var person = Guid.NewGuid(); var visit = Guid.NewGuid(); var owner = Guid.NewGuid();
        var actor = $"instructions.worker.{Guid.NewGuid():N}";
        var organization = mode == "OtherTenant" ? Guid.NewGuid() : TenantDefaults.OrganizationId;
        var branch = mode is "OtherBranch" or "OtherTenant" ? Guid.NewGuid() : TenantDefaults.BranchId;
        var worker = mode == "Assigned" || mode == "Unpublished" ? owner : Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        if (organization != TenantDefaults.OrganizationId) db.Organizations.Add(new(organization, "Other instruction tenant", "Pilot", "Active"));
        if (branch != TenantDefaults.BranchId) db.Branches.Add(new(branch, organization, "Other instruction branch", "North", "Active"));
        db.CareWorkers.Add(new(owner, "Assigned instruction worker", "Care", "Flexible", 0, 0, "Valid", "Compliant", "Local", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        if (worker != owner) db.CareWorkers.Add(new(worker, "Other instruction worker", "Care", "Flexible", 0, 0, "Valid", "Compliant", "Local", organization, branch));
        db.ServiceUsers.Add(new(person, "Fictional instruction person", new DateOnly(1945, 1, 1), "+441234567890", "Support", "Contact", "", RiskLevel.Medium, "Active", "Address", "None", "None", "Private", "", "", "Independent", "Independent", "Verbal", "Routine", "Standard", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        db.Visits.Add(new(visit, person, owner, mode == "Unpublished" ? DateTimeOffset.UtcNow.AddDays(2) : DateTimeOffset.UtcNow.AddHours(-1), "Care", 30, "Care", VisitStatus.Scheduled, null, null, null, null, null, null, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        db.AppUsers.Add(new(Guid.NewGuid(), actor, $"{actor}@example.test", PasswordHasher.HashPassword("Admin123!"), UserRole.CareWorker, true, organization, branch, worker, null));
        await db.SaveChangesAsync();
        return (person, visit, actor);
    }

    private const string PrivateAnswers = "{\"private\":\"RAW-ANSWERS-SECRET\"}";
    private static Task<int> Assessment(CareDbContext db, Guid person, Guid organization, Guid branch, string type, string status, string marker, bool signed) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into governed_assessments(id,service_user_id,organization_id,branch_id,assessment_type,template_key,template_version,answers_json,score,risk_level,summary,recommended_actions,status,version,assessor_name,assessor_role,review_due_at,created_by,approved_at,signed_at)
            values({Guid.NewGuid()},{person},{organization},{branch},{type},{"instruction-test"},{"1"},cast({PrivateAnswers} as jsonb),1,'Low',{marker},{marker},{status},1,'Assessor','Manager',{DateTimeOffset.UtcNow.AddDays(30)},'manager',{(signed ? DateTimeOffset.UtcNow : (DateTimeOffset?)null)},{(signed ? DateTimeOffset.UtcNow : (DateTimeOffset?)null)})
            """);

    private static Task<int> Risk(CareDbContext db, Guid person, string status, string marker, bool signed) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into governed_risk_assessments(id,service_user_id,organization_id,branch_id,category,hazard,likelihood,severity,score,risk_level,controls,contingency,owner,status,version,review_due_at,created_by,approved_at,signed_at)
            values({Guid.NewGuid()},{person},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},'Falls',{marker},2,2,4,'Low',{marker},'Contact manager','Manager',{status},1,{DateTimeOffset.UtcNow.AddDays(-1)},'manager',{(signed ? DateTimeOffset.UtcNow : (DateTimeOffset?)null)},{(signed ? DateTimeOffset.UtcNow : (DateTimeOffset?)null)})
            """);

    private static string Path(Guid visit) => $"/api/phase1/visits/{visit}/clinical-instructions";
    private async Task<HttpClient> Login(string actor)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { userName = actor, password = "Admin123!", mfaCode = (string?)null });
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<LoginResponse>())!.Token);
        return client;
    }
    private sealed record LoginResponse(string Token);
}
