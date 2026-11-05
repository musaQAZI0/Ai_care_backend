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
public sealed class AssessmentReviewDeadlineRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExpiredReviewCannotSubmitOrSupersedeCurrentApproval(bool risk, bool approval)
    {
        await factory.EnsureClinicalSeedAsync();
        var person = Guid.NewGuid(); var current = Guid.NewGuid(); var expired = Guid.NewGuid();
        var name = $"review.deadline.{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.ServiceUsers.Add(new(person, "Fictional review deadline person", new DateOnly(1945, 1, 1), "+441234567890",
                "Support", "Contact", "", RiskLevel.Medium, "Active", "Address", "None", "None", "Private", "", "",
                "Independent", "Independent", "Verbal", "Routine", "Standard", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.AppUsers.Add(new(Guid.NewGuid(), name, $"{name}@example.test", PasswordHasher.HashPassword("Admin123!"),
                UserRole.CareManager, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await db.SaveChangesAsync();
            // Model time passing after an initially valid draft was saved. Clinical
            // content is immutable, so seed its elapsed date rather than rewriting it.
            await Seed(db, risk, person, current, "Current", 1, null, DateTimeOffset.UtcNow.AddDays(30));
            await Seed(db, risk, person, expired, approval ? "InReview" : "Draft", 2, current, DateTimeOffset.UtcNow.AddDays(-1));
        }

        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = name, password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<Login>())!.Token);
        var kind = risk ? "risks" : "assessments";
        var root = $"/api/phase1/service-users/{person}/clinical-governance";
        var response = approval
            ? await client.PostAsJsonAsync($"{root}/{kind}/{expired}/approve", new { signerName = "Manager", declaration = "Reviewed exact version" })
            : await client.PostAsync($"{root}/{kind}/{expired}/submit", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("review date", await response.Content.ReadAsStringAsync());

        var history = await client.GetFromJsonAsync<JsonElement>(root);
        var rows = history.GetProperty(kind).EnumerateArray().ToArray();
        Assert.Equal(current, Assert.Single(rows, item => item.GetProperty("status").GetString() == "Current").GetProperty("id").GetGuid());
        Assert.Equal(approval ? "InReview" : "Draft", rows.Single(item => item.GetProperty("id").GetGuid() == expired).GetProperty("status").GetString());
        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.False(await verifyDb.AuditEvents.AnyAsync(item => item.EntityId == expired
            && (item.Action == "assessment.approved" || item.Action == "risk_assessment.approved"
                || item.Action == "assessment.submitted" || item.Action == "risk_assessment.submitted")));
    }

    private static Task<int> Seed(CareDbContext db, bool risk, Guid person, Guid id, string status, int version, Guid? previous, DateTimeOffset due)
    {
        const string emptyAnswers = "{}";
        if (risk) return db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into governed_risk_assessments(id,service_user_id,organization_id,branch_id,category,hazard,likelihood,severity,score,risk_level,controls,contingency,owner,status,version,supersedes_id,review_due_at,created_by)
            values({id},{person},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},'Falls','Unsteady transfers',2,2,4,'Low','Support safely','Contact manager','Manager',{status},{version},{previous},{due},'manager')
            """);
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into governed_assessments(id,service_user_id,organization_id,branch_id,assessment_type,template_key,template_version,answers_json,score,risk_level,summary,recommended_actions,status,version,supersedes_id,assessor_name,assessor_role,review_due_at,created_by)
            values({id},{person},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},'Needs','review-deadline','1',cast({emptyAnswers} as jsonb),1,'Low','Fictional review','Review support',{status},{version},{previous},'Assessor','Manager',{due},'manager')
            """);
    }
    private sealed record Login(string Token);
}
