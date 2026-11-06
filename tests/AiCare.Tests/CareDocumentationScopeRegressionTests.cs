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
public sealed class CareDocumentationScopeRegressionTests(PostgresRegressionFactory factory)
    : IClassFixture<PostgresRegressionFactory>
{
    [Theory]
    [InlineData(UserRole.CareManager, false)]
    [InlineData(UserRole.CareCoordinator, false)]
    [InlineData(UserRole.Administrator, false)]
    [InlineData(UserRole.CareManager, true)]
    [InlineData(UserRole.CareCoordinator, true)]
    [InlineData(UserRole.Administrator, true)]
    public async Task DocumentationAccessPreservesBranchAndTenantBoundaries(UserRole role, bool foreignTenant)
    {
        await factory.EnsureClinicalSeedAsync();
        var organization = foreignTenant ? Guid.NewGuid() : TenantDefaults.OrganizationId;
        var branch = Guid.NewGuid();
        var person = Guid.NewGuid();
        var worker = Guid.NewGuid();
        var visit = Guid.NewGuid();
        var note = Guid.NewGuid();
        var alert = Guid.NewGuid();
        var username = $"documentation.scope.{Guid.NewGuid():N}";
        var secretText = $"Fictional branch evidence {Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            if (foreignTenant) db.Organizations.Add(new Organization(organization, "Documentation tenant", "Trial", "Active"));
            db.Branches.Add(new Branch(branch, organization, "Documentation branch", "Test", "Active"));
            db.ServiceUsers.Add(new ServiceUser(person, secretText, new DateOnly(1950, 1, 1), "1", "Care", "Contact", "",
                RiskLevel.Low, "Active", "Address", "None", "None", "Private", "", "", "Independent", "Independent", "Verbal", "Routine", "Standard", organization, branch));
            db.CareWorkers.Add(new CareWorker(worker, "Documentation worker", "Care", "Flexible", 0, 0, "Valid", "Compliant", "10 miles", organization, branch));
            db.Visits.Add(new Visit(visit, person, worker, DateTimeOffset.UtcNow, "Documentation visit", 30, "Care", VisitStatus.Scheduled,
                null, null, null, null, null, null, organization, branch));
            db.CareNotes.Add(new CareNote(note, visit, person, worker, secretText, "Care", "Fluids", "None", secretText, true,
                DateTimeOffset.UtcNow, organization, branch));
            db.AppUsers.Add(new AppUser(Guid.NewGuid(), username, username + "@test.invalid", PasswordHasher.HashPassword("Admin123!"),
                role, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                insert into deterioration_alerts(id,visit_id,service_user_id,organization_id,branch_id,alert_type,severity,detail)
                values({alert},{visit},{person},{organization},{branch},{"Scope regression"},{"High"},{secretText})
                """);
        }

        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = username, password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<Login>())!.Token);
        const string root = "/api/phase1/care-documentation";
        var allowed = role == UserRole.Administrator && !foreignTenant;
        foreach (var path in new[] { $"{root}/visits/{visit}/alerts", $"{root}/service-users/{person}/timeline" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.NotFound, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            if (allowed) Assert.Contains(secretText, body);
            else Assert.DoesNotContain(secretText, body);
        }

        var amendment = await client.PostAsJsonAsync($"{root}/notes/{note}/amendments", new {
            reason = "Verified fictional correction", summary = "Corrected scoped note", personalCare = "Care", mealsAndHydration = "Fluids",
            medication = "None", concerns = "Reviewed", requiresReview = true
        });
        Assert.Equal(allowed ? HttpStatusCode.Created : HttpStatusCode.NotFound, amendment.StatusCode);
        var review = await client.PostAsJsonAsync($"{root}/notes/{note}/reviews", new { status = "Approved", comment = "Scoped review" });
        Assert.Equal(allowed ? HttpStatusCode.Created : HttpStatusCode.NotFound, review.StatusCode);
        var progress = await client.PatchAsJsonAsync($"{root}/alerts/{alert}", new {
            action = "Resolve", immediateAction = "Follow-up completed", externalContact = "Test manager", owner = "Test coordinator"
        });
        Assert.Equal(allowed ? HttpStatusCode.NoContent : HttpStatusCode.NotFound, progress.StatusCode);

        using var verify = factory.Services.CreateScope();
        var database = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        var persisted = await database.CareNotes.AsNoTracking().SingleAsync(n => n.Id == note);
        Assert.Equal(allowed ? "Corrected scoped note" : secretText, persisted.Summary);
        var audit = await database.AuditEvents.AsNoTracking().Where(a => a.Actor == username && a.EntityType == "CareDocumentation").ToListAsync();
        if (allowed)
        {
            Assert.Equal(3, audit.Count);
            Assert.All(audit, entry => { Assert.Equal(organization, entry.OrganizationId); Assert.Equal(branch, entry.BranchId); });
        }
        else Assert.Empty(audit);
        // Check raw history too: denial must not leave an un-audited mutation behind.
        var connection = database.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select (select count(*) from care_note_amendments where care_note_id=@note),
                   (select count(*) from care_note_reviews where care_note_id=@note),
                   (select status from deterioration_alerts where id=@alert)
            """;
        foreach (var pair in new[] { ("note", note), ("alert", alert) })
        {
            var parameter = command.CreateParameter(); parameter.ParameterName = pair.Item1; parameter.Value = pair.Item2;
            command.Parameters.Add(parameter);
        }
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(allowed ? 1L : 0L, reader.GetInt64(0));
        Assert.Equal(allowed ? 1L : 0L, reader.GetInt64(1));
        Assert.Equal(allowed ? "Resolved" : "Open", reader.GetString(2));
    }

    private sealed record Login(string Token);
}
