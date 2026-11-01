using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiCare.Api;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace AiCare.Tests;
public sealed partial class MeasurableCareGoalsRegressionTests
{
    [Fact]
    public async Task CompletenessExceptionsAreScopedVersionedAndNeverOverrideMissingSourceEvidence()
    {
        var f = await Setup(); var root = $"/api/phase1/service-users/{f.Person}/onboarding/remediation";
        var request = new CompletenessEventRequest(Guid.NewGuid(), 0, "Assigned", f.Owner, DateTimeOffset.UtcNow.AddDays(1), "Complete review and activation of the plan");
        await Expect(await f.OtherBranch.GetAsync(root), HttpStatusCode.NotFound);
        await Expect(await (await ForeignManager()).PostAsJsonAsync(root + "/care-plan", request), HttpStatusCode.NotFound);
        await Expect(await f.Worker.GetAsync(root), HttpStatusCode.Forbidden);
        await Expect(await f.Manager.PostAsJsonAsync(root + "/care-plan", request with { OwnerId = Guid.NewGuid() }), HttpStatusCode.BadRequest);
        await Expect(await f.Manager.PostAsJsonAsync(root + "/unknown", request), HttpStatusCode.BadRequest);
        var attempts = await Task.WhenAll(f.Manager.PostAsJsonAsync(root + "/care-plan", request), f.Manager.PostAsJsonAsync(root + "/care-plan", request));
        Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.Created); Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.OK);
        await Expect(await f.Manager.PostAsJsonAsync(root + "/care-plan", request with { Evidence = "Changed replay" }), HttpStatusCode.Conflict);
        await Expect(await f.Manager.PostAsJsonAsync(root + "/care-plan", request with { RequestId = Guid.NewGuid() }), HttpStatusCode.Conflict);
        var exception = request with { RequestId = Guid.NewGuid(), ExpectedRevision = 1, Action = "ExceptionAccepted",
            Evidence = "Manager reviewed the outstanding plan evidence; follow up remains assigned", ExceptionExpiresAt = DateTimeOffset.UtcNow.AddDays(2) };
        await Expect(await f.Manager.PostAsJsonAsync(root + "/care-plan", exception with { ExceptionExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }), HttpStatusCode.BadRequest);
        await Expect(await f.Manager.PostAsJsonAsync(root + "/care-plan", exception), HttpStatusCode.Created);
        var view = await f.Manager.GetFromJsonAsync<JsonElement>(root);
        Assert.False(view.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("key").GetString() == "care-plan").GetProperty("complete").GetBoolean());
        Assert.Equal(2, view.GetProperty("history").GetArrayLength());
        await Expect(await f.Manager.PostAsync($"/api/phase1/service-users/{f.Person}/onboarding/activate", null), HttpStatusCode.BadRequest);
        var resolved = request with { RequestId = Guid.NewGuid(), ExpectedRevision = 2, Action = "Resolved", Evidence = "Verified current active plan" };
        await Expect(await f.Manager.PostAsJsonAsync(root + "/care-plan", resolved), HttpStatusCode.Conflict);
        await Activate(f, f.Plan, seed: true);
        await Expect(await f.Manager.PostAsJsonAsync(root + "/care-plan", resolved), HttpStatusCode.Created);
        await Expect(await f.Manager.PostAsJsonAsync(root + "/care-plan", resolved), HttpStatusCode.OK);
        using var scope = factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("delete from person_completeness_events where id={0}", exception.RequestId));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("update person_completeness_events set evidence='overwrite' where id={0}", resolved.RequestId));
        Assert.Equal(3, await db.AuditEvents.CountAsync(e => e.EntityId == request.RequestId || e.EntityId == exception.RequestId || e.EntityId == resolved.RequestId));
    }

    [Fact]
    public async Task CoordinatorsCanAssignRemediationButCannotAcceptAnException()
    {
        var f = await Setup(); var name = $"completeness.coordinator.{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.AppUsers.Add(new AppUser(Guid.NewGuid(), name, name + "@test.invalid", PasswordHasher.HashPassword("Admin123!"),
                UserRole.CareCoordinator, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId)); await db.SaveChangesAsync();
        }
        var coordinator = await Client(name); var root = $"/api/phase1/service-users/{f.Person}/onboarding/remediation/care-plan";
        var request = new CompletenessEventRequest(Guid.NewGuid(), 0, "Assigned", f.Owner, DateTimeOffset.UtcNow.AddDays(1), "Coordinate evidence collection");
        await Expect(await coordinator.PostAsJsonAsync(root, request), HttpStatusCode.Created);
        await Expect(await coordinator.PostAsJsonAsync(root, request with { RequestId = Guid.NewGuid(), ExpectedRevision = 1,
            Action = "ExceptionAccepted", ExceptionExpiresAt = DateTimeOffset.UtcNow.AddDays(2) }), HttpStatusCode.Forbidden);
    }
}
