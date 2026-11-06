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
public sealed class CareNoteAccessRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Theory]
    [InlineData(UserRole.Administrator, true)]
    [InlineData(UserRole.CareManager, true)]
    [InlineData(UserRole.CareCoordinator, true)]
    [InlineData(UserRole.CareWorker, true)]
    [InlineData(UserRole.FamilyMember, false)]
    [InlineData(UserRole.ServiceUser, false)]
    [InlineData(UserRole.BackOffice, false)]
    public async Task RawNotesAndObservationsRequireACareDeliveryRole(UserRole role, bool allowed)
    {
        var data = await Setup(role);
        using var client = await Client(data.Actor);
        foreach (var path in new[] { "/api/phase1/care-notes", $"/api/phase1/care-notes/{data.Note}", "/api/phase1/observations" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            if (allowed) Assert.Contains(data.Summary, body); else Assert.DoesNotContain(data.Summary, body);
        }
        if (allowed) return;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/phase1/care-notes", Request(data, data.Visit))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/phase1/care-notes/{data.Note}", Request(data, data.Visit))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/phase1/care-notes/{data.Note}")).StatusCode);
        await Unchanged(data);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WorkerCannotReadNotesFromUnpublishedOrUnassignedVisits(bool unpublished)
    {
        var data = await Setup(UserRole.CareWorker, unpublished);
        using var client = await Client(data.Actor);
        var id = unpublished ? data.Note : data.OtherNote;
        var summary = unpublished ? data.Summary : data.OtherSummary;
        var response = await client.GetAsync($"/api/phase1/care-notes/{id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(summary, await response.Content.ReadAsStringAsync());
        var listing = await client.GetAsync("/api/phase1/care-notes");
        Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
        Assert.DoesNotContain(summary, await listing.Content.ReadAsStringAsync());
        var observations = await client.GetAsync("/api/phase1/observations");
        Assert.Equal(HttpStatusCode.OK, observations.StatusCode);
        var observationBody = await observations.Content.ReadAsStringAsync();
        Assert.DoesNotContain(data.OtherSummary, observationBody);
        if (unpublished) Assert.DoesNotContain(data.Summary, observationBody);
        else Assert.Contains(data.Summary, observationBody);
    }

    [Fact]
    public async Task WorkerCannotMoveAnotherWorkersNoteOntoAnAssignedVisitOrReparentOwnNote()
    {
        var data = await Setup(UserRole.CareWorker);
        using var client = await Client(data.Actor);
        // The request targets the attacker's legitimate visit, while the URL identifies another worker's note.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/phase1/care-notes/{data.OtherNote}", Request(data, data.Visit))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/phase1/care-notes/{data.OtherNote}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/phase1/care-notes/{data.Note}", Request(data, data.OtherVisit))).StatusCode);
        await Unchanged(data);
    }

    private async Task Unchanged(Fixture data)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        var own = await db.CareNotes.AsNoTracking().SingleAsync(n => n.Id == data.Note);
        var other = await db.CareNotes.AsNoTracking().SingleAsync(n => n.Id == data.OtherNote);
        Assert.Equal(data.Summary, own.Summary); Assert.Equal(data.Visit, own.VisitId);
        Assert.Equal(data.OtherSummary, other.Summary); Assert.Equal(data.OtherVisit, other.VisitId);
        Assert.Equal(data.Worker, own.CareWorkerId); Assert.NotEqual(data.Worker, other.CareWorkerId);
        Assert.Equal(2, await db.CareNotes.CountAsync(n => n.ServiceUserId == data.Person));
        Assert.False(await db.AuditEvents.AnyAsync(a => a.Actor == data.Actor && a.EntityType == nameof(CareNote)));
    }

    private static object Request(Fixture data, Guid visit) => new {
        visitId = visit, serviceUserId = data.Person, careWorkerId = data.Worker, summary = "Attempted overwrite",
        personalCare = "Care", mealsAndHydration = "Fluids", medication = "None", concerns = "None", requiresReview = true
    };

    private async Task<Fixture> Setup(UserRole role, bool unpublished = false)
    {
        await factory.EnsureClinicalSeedAsync();
        var person = Guid.NewGuid(); var worker = Guid.NewGuid(); var other = Guid.NewGuid();
        var visit = Guid.NewGuid(); var otherVisit = Guid.NewGuid(); var note = Guid.NewGuid(); var otherNote = Guid.NewGuid();
        var actor = $"note.access.{Guid.NewGuid():N}";
        var summary = $"Fictional assigned evidence {Guid.NewGuid():N}";
        var otherSummary = $"Fictional other worker evidence {Guid.NewGuid():N}";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        db.ServiceUsers.Add(new(person, "Fictional note access person", new DateOnly(1950, 1, 1), "1", "Care", "Contact", "", RiskLevel.Low,
            "Active", "Address", "None", "None", "Private", "", "", "Independent", "Independent", "Verbal", "Routine", "Standard", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        foreach (var id in new[] { worker, other })
            db.CareWorkers.Add(new(id, "Note worker", "Care", "Flexible", 0, 0, "Valid", "Compliant", "Local", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        db.Visits.Add(new(visit, person, worker, DateTimeOffset.UtcNow.AddDays(unpublished ? 90 : -1), "Note visit", 30, "Care", VisitStatus.Scheduled,
            null, null, null, null, null, null, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        db.Visits.Add(new(otherVisit, person, other, DateTimeOffset.UtcNow.AddDays(-1), "Other note visit", 30, "Care", VisitStatus.Scheduled,
            null, null, null, null, null, null, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        db.CareNotes.Add(new(note, visit, person, worker, summary, "Care", "Fluids", "None", "None", false, DateTimeOffset.UtcNow, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        db.CareNotes.Add(new(otherNote, otherVisit, person, other, otherSummary, "Care", "Fluids", "None", "None", false, DateTimeOffset.UtcNow, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        db.HealthObservations.Add(new(Guid.NewGuid(), visit, person, "Mood", "Settled", "", summary, DateTimeOffset.UtcNow, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        db.HealthObservations.Add(new(Guid.NewGuid(), otherVisit, person, "Mood", "Settled", "", otherSummary, DateTimeOffset.UtcNow, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
        db.AppUsers.Add(new(Guid.NewGuid(), actor, actor + "@test.invalid", PasswordHasher.HashPassword("Admin123!"), role,
            true, TenantDefaults.OrganizationId, TenantDefaults.BranchId, role == UserRole.CareWorker ? worker : null));
        await db.SaveChangesAsync();
        return new(person, worker, visit, otherVisit, note, otherNote, actor, summary, otherSummary);
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
    private sealed record Fixture(Guid Person, Guid Worker, Guid Visit, Guid OtherVisit, Guid Note, Guid OtherNote, string Actor, string Summary, string OtherSummary);
}
