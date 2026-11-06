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
public sealed class CareDocumentationAtomicityRegressionTests(PostgresRegressionFactory factory)
    : IClassFixture<PostgresRegressionFactory>
{
    [Theory]
    [InlineData("amendment", "care_note.amended")]
    [InlineData("review", "care_note.reviewed")]
    [InlineData("alert", "deterioration_alert.resolved")]
    public async Task FailedAuditRollsBackDocumentationEvidence(string operation, string action)
    {
        var data = await Setup();
        using var client = await Client(data.Actor);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        // Values are generated fixture IDs and fixed InlineData, never external SQL input.
        var constraint = "ck_documentation_" + Guid.NewGuid().ToString("N");
        await db.Database.ExecuteSqlRawAsync($"""
            ALTER TABLE "AuditEvents" ADD CONSTRAINT "{constraint}"
            CHECK ("Actor"<>'{data.Actor}' OR "Action"<>'{action}')
            """);
        try
        {
            var response = operation switch
            {
                "amendment" => await client.PostAsJsonAsync($"{Root}/notes/{data.Note}/amendments", Amendment("Changed note")),
                "review" => await client.PostAsJsonAsync($"{Root}/notes/{data.Note}/reviews", new { status = "Approved", comment = "Synthetic review" }),
                _ => await client.PatchAsJsonAsync($"{Root}/alerts/{data.Alert}", new { action = "Resolve", immediateAction = "Follow-up", externalContact = "Test manager", owner = "Test coordinator" })
            };
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync($"""
                ALTER TABLE "AuditEvents" DROP CONSTRAINT "{constraint}"
                """);
        }

        Assert.Equal("Original note", (await db.CareNotes.AsNoTracking().SingleAsync(n => n.Id == data.Note)).Summary);
        Assert.Equal(0, await db.Database.SqlQuery<int>($"""
            select count(*)::integer as "Value" from care_note_amendments where care_note_id={data.Note}
            """).SingleAsync());
        Assert.Equal(0, await db.Database.SqlQuery<int>($"""
            select count(*)::integer as "Value" from care_note_reviews where care_note_id={data.Note}
            """).SingleAsync());
        Assert.Equal("Open", await db.Database.SqlQuery<string>($"""
            select status as "Value" from deterioration_alerts where id={data.Alert}
            """).SingleAsync());
        Assert.False(await db.AuditEvents.AnyAsync(a => a.Actor == data.Actor && a.EntityType == "CareDocumentation"));
    }

    [Fact]
    public async Task ConcurrentAmendmentsRetainAContinuousBeforeAndAfterHistory()
    {
        var data = await Setup();
        using var client = await Client(data.Actor);
        var responses = await Task.WhenAll(
            client.PostAsJsonAsync($"{Root}/notes/{data.Note}/amendments", Amendment("First submitted note")),
            client.PostAsJsonAsync($"{Root}/notes/{data.Note}/amendments", Amendment("Second submitted note")));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        var final = (await db.CareNotes.AsNoTracking().SingleAsync(n => n.Id == data.Note)).Summary;
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        var history = new List<(string Before, string After)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "select old_values_json,new_values_json from care_note_amendments where care_note_id=@note";
            var parameter = command.CreateParameter(); parameter.ParameterName = "note"; parameter.Value = data.Note; command.Parameters.Add(parameter);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                using var before = JsonDocument.Parse(reader.GetString(0));
                using var after = JsonDocument.Parse(reader.GetString(1));
                history.Add((before.RootElement.GetProperty("Summary").GetString()!, after.RootElement.GetProperty("Summary").GetString()!));
            }
        }
        Assert.Equal(2, history.Count);
        var first = Assert.Single(history, row => row.Before == "Original note");
        var second = Assert.Single(history, row => row.Before == first.After);
        Assert.Equal(second.After, final);
        Assert.NotEqual(first.After, second.After);
        Assert.Equal(2, await db.AuditEvents.CountAsync(a => a.Actor == data.Actor && a.Action == "care_note.amended"));
    }

    private const string Root = "/api/phase1/care-documentation";
    private static object Amendment(string summary) => new {
        reason = "Fictional correction with source evidence", summary, personalCare = "Care", mealsAndHydration = "Fluids",
        medication = "None", concerns = "Follow up", requiresReview = true
    };

    private async Task<(Guid Note, Guid Alert, string Actor)> Setup()
    {
        await factory.EnsureClinicalSeedAsync();
        var person = Guid.NewGuid(); var worker = Guid.NewGuid(); var visit = Guid.NewGuid();
        var note = Guid.NewGuid(); var alert = Guid.NewGuid(); var actor = $"documentation.atomic.{Guid.NewGuid():N}";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        db.ServiceUsers.Add(new ServiceUser(person, "Fictional atomic documentation person", new DateOnly(1950, 1, 1), "1", "Care", "Contact", "",
            RiskLevel.Low, "Active", "Address", "None", "None", "Private", "", "", "Independent", "Independent", "Verbal", "Routine", "Standard", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        db.CareWorkers.Add(new CareWorker(worker, "Documentation worker", "Care", "Flexible", 0, 0, "Valid", "Compliant", "Local", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        db.Visits.Add(new Visit(visit, person, worker, DateTimeOffset.UtcNow, "Documentation", 30, "Care", VisitStatus.Scheduled,
            null, null, null, null, null, null, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        db.CareNotes.Add(new CareNote(note, visit, person, worker, "Original note", "Care", "Fluids", "None", "Concern", true,
            DateTimeOffset.UtcNow, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        db.AppUsers.Add(new AppUser(Guid.NewGuid(), actor, actor + "@test.invalid", PasswordHasher.HashPassword("Admin123!"),
            UserRole.CareManager, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into deterioration_alerts(id,visit_id,service_user_id,organization_id,branch_id,alert_type,severity,detail)
            values({alert},{visit},{person},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},{"Atomic regression"},{"High"},{"Fictional concern"})
            """);
        return (note, alert, actor);
    }

    private async Task<HttpClient> Client(string actor)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = actor, password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<Login>())!.Token);
        return client;
    }
    private sealed record Login(string Token);
}
