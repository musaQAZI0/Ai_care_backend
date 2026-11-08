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
    [InlineData("withdrawal", "care_note.withdrawn")]
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
                "review" => await client.PostAsJsonAsync($"{Root}/notes/{data.Note}/reviews", new { status = "Approved", comment = "Synthetic review", expectedRevision = 1 }),
                "withdrawal" => await client.DeleteAsync($"/api/phase1/care-notes/{data.Note}"),
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

    [Theory]
    [InlineData("success")]
    [InlineData("threshold-failure")]
    [InlineData("observation-audit-failure")]
    [InlineData("location-audit-failure")]
    public async Task DeliveryEvidenceAndRequiredAlertsCommitTogether(string mode)
    {
        var data = await Setup();
        using var client = await Client(data.Actor);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into observation_thresholds(id,organization_id,branch_id,observation_type,maximum_value,severity,instructions)
            values({Guid.NewGuid()},{TenantDefaults.OrganizationId},{data.Branch},{"Temperature"},{38m},{"High"},{"Contact the manager"})
            """);
        var constraint = "ck_delivery_" + Guid.NewGuid().ToString("N");
        var thresholdFailure = mode == "threshold-failure";
        var location = mode == "location-audit-failure";
        var failing = mode != "success";
        var table = thresholdFailure ? "deterioration_alerts" : "\"AuditEvents\"";
        if (failing)
        {
            if (thresholdFailure)
                await db.Database.ExecuteSqlRawAsync($"""
                    ALTER TABLE deterioration_alerts ADD CONSTRAINT "{constraint}"
                    CHECK (visit_id<>'{data.Visit:D}'::uuid OR observation_id IS NULL)
                    """);
            else
            {
                var action = location ? "visit.location_evidence" : "visit.observation_recorded";
                await db.Database.ExecuteSqlRawAsync($"""
                    ALTER TABLE "AuditEvents" ADD CONSTRAINT "{constraint}"
                    CHECK ("Actor"<>'{data.Actor}' OR "Action"<>'{action}')
                    """);
            }
        }
        try
        {
            var response = location
                ? await client.PostAsJsonAsync($"/api/phase1/visits/{data.Visit}/delivery/location", new {
                    eventType = "Evidence", latitude = 51.5m, longitude = -0.1m, accuracyMeters = 5m,
                    capturedAt = DateTimeOffset.UtcNow, source = "Synthetic regression", notes = "Rollback check"
                })
                : await client.PostAsJsonAsync($"/api/phase1/visits/{data.Visit}/delivery/observations", new {
                    observationType = "Temperature", value = "39.2", unit = "C", notes = "Synthetic threshold check", recordedAt = (DateTimeOffset?)null
                });
            Assert.Equal(failing ? HttpStatusCode.InternalServerError : HttpStatusCode.Created, response.StatusCode);
        }
        finally
        {
            if (failing)
                await db.Database.ExecuteSqlRawAsync($"""
                    ALTER TABLE {table} DROP CONSTRAINT "{constraint}"
                    """);
        }
        Assert.Equal(failing ? 0 : 1, await db.HealthObservations.CountAsync(o => o.VisitId == data.Visit));
        Assert.Equal(failing ? 0 : 1, await db.Database.SqlQuery<int>($"""
            select count(*)::integer as "Value" from deterioration_alerts where visit_id={data.Visit} and observation_id is not null
            """).SingleAsync());
        Assert.Equal(0, await db.Database.SqlQuery<int>($"""
            select count(*)::integer as "Value" from visit_location_events where visit_id={data.Visit}
            """).SingleAsync());
        Assert.Equal(failing ? 0 : 1, await db.AuditEvents.CountAsync(a => a.Actor == data.Actor &&
            (a.Action == "visit.observation_recorded" || a.Action == "visit.location_evidence")));
    }

    private const string Root = "/api/phase1/care-documentation";
    private static object Amendment(string summary) => new {
        reason = "Fictional correction with source evidence", summary, personalCare = "Care", mealsAndHydration = "Fluids",
        medication = "None", concerns = "Follow up", requiresReview = true
    };

    private async Task<(Guid Note, Guid Alert, string Actor, Guid Visit, Guid Branch)> Setup()
    {
        await factory.EnsureClinicalSeedAsync();
        var person = Guid.NewGuid(); var worker = Guid.NewGuid(); var visit = Guid.NewGuid(); var branch = Guid.NewGuid();
        var note = Guid.NewGuid(); var alert = Guid.NewGuid(); var actor = $"documentation.atomic.{Guid.NewGuid():N}";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        db.Branches.Add(new Branch(branch,TenantDefaults.OrganizationId,"Atomic documentation branch","Test","Active"));
        db.ServiceUsers.Add(new ServiceUser(person, "Fictional atomic documentation person", new DateOnly(1950, 1, 1), "1", "Care", "Contact", "",
            RiskLevel.Low, "Active", "Address", "None", "None", "Private", "", "", "Independent", "Independent", "Verbal", "Routine", "Standard", TenantDefaults.OrganizationId, branch));
        db.CareWorkers.Add(new CareWorker(worker, "Documentation worker", "Care", "Flexible", 0, 0, "Valid", "Compliant", "Local", TenantDefaults.OrganizationId, branch));
        db.Visits.Add(new Visit(visit, person, worker, DateTimeOffset.UtcNow, "Documentation", 30, "Care", VisitStatus.Scheduled,
            null, null, null, null, null, null, TenantDefaults.OrganizationId, branch));
        db.CareNotes.Add(new CareNote(note, visit, person, worker, "Original note", "Care", "Fluids", "None", "Concern", true,
            DateTimeOffset.UtcNow, TenantDefaults.OrganizationId, branch));
        db.AppUsers.Add(new AppUser(Guid.NewGuid(), actor, actor + "@test.invalid", PasswordHasher.HashPassword("Admin123!"),
            UserRole.CareManager, true, TenantDefaults.OrganizationId, branch));
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into deterioration_alerts(id,visit_id,service_user_id,organization_id,branch_id,alert_type,severity,detail)
            values({alert},{visit},{person},{TenantDefaults.OrganizationId},{branch},{"Atomic regression"},{"High"},{"Fictional concern"})
            """);
        return (note, alert, actor, visit, branch);
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
