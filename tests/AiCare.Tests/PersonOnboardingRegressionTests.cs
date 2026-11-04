using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed class PersonOnboardingRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task FictionalPersonOnboardingReloadsEveryProfileFieldAndRecordsActor()
    {
        var (client, actor) = await ManagerClient();
        using var ownedClient = client;
        var request = Request();
        var response = await client.PostAsJsonAsync("/api/phase1/service-users", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<ServiceUser>())!;

        // A separate HTTP request and database scope ensure this is persisted data.
        var reloaded = (await client.GetFromJsonAsync<ServiceUser>($"/api/phase1/service-users/{created.Id}"))!;
        var expected = JsonSerializer.SerializeToElement(request);
        var actual = JsonSerializer.SerializeToElement(reloaded);
        foreach (var field in expected.EnumerateObject().Where(field => field.Name != "Contact"))
            Assert.Equal(field.Value.GetRawText(), actual.GetProperty(field.Name).GetRawText());
        Assert.Equal(TenantDefaults.OrganizationId, reloaded.OrganizationId);
        Assert.Equal(TenantDefaults.BranchId, reloaded.BranchId);
        Assert.Equal(request.Contact, await client.GetFromJsonAsync<ProfileContactRequest>($"/api/phase1/profile-contact/ServiceUser/{created.Id}"));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal(reloaded, await db.ServiceUsers.AsNoTracking().SingleAsync(person => person.Id == created.Id));
        Assert.True(await db.AuditEvents.AnyAsync(item => item.Action == "service_user.onboarded"
            && item.EntityId == created.Id && item.Actor == actor
            && item.OrganizationId == TenantDefaults.OrganizationId && item.BranchId == TenantDefaults.BranchId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrDefaultBirthDateCannotCreateOrOverwritePerson(bool omitDate)
    {
        var (client, _) = await ManagerClient();
        using var ownedClient = client;
        var request = Request();
        var createdResponse = await client.PostAsJsonAsync("/api/phase1/service-users", request);
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var original = (await createdResponse.Content.ReadFromJsonAsync<ServiceUser>())!;
        var invalid = JsonSerializer.SerializeToElement(request with { DateOfBirth = default })
            .EnumerateObject().Where(field => !omitDate || field.Name != "DateOfBirth")
            .ToDictionary(field => field.Name, field => field.Value.Clone());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/phase1/service-users", invalid)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/phase1/service-users/{original.Id}", invalid)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal(original, await db.ServiceUsers.AsNoTracking().SingleAsync(person => person.Id == original.Id));
        Assert.Equal(1, await db.ServiceUsers.CountAsync(person => person.FullName == request.FullName));
        Assert.False(await db.AuditEvents.AnyAsync(item => item.EntityId == original.Id && item.Action == "service_user.updated"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtendedRecordCreateAndUpdateReloadEveryFieldAndKeepPersonBranch(bool administratorAcrossBranches)
    {
        var (client, actor) = await ManagerClient(administratorAcrossBranches ? UserRole.Administrator : UserRole.CareManager);
        using var ownedClient = client;
        var create = await client.PostAsJsonAsync("/api/phase1/service-users", Request());
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var person = (await create.Content.ReadFromJsonAsync<ServiceUser>())!;
        var branch = TenantDefaults.BranchId;
        if (administratorAcrossBranches)
        {
            branch = Guid.NewGuid();
            using var setup = factory.Services.CreateScope();
            var db = setup.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Branches.Add(new Branch(branch, TenantDefaults.OrganizationId, "Person record destination", "North", "Active"));
            var tracked = await db.ServiceUsers.SingleAsync(item => item.Id == person.Id);
            db.Entry(tracked).CurrentValues.SetValues(tracked with { BranchId = branch });
            await db.SaveChangesAsync();
        }

        var request = new UpsertPersonRecordRequest("Fictional preferred name", "she/her", "FICTIONAL-123", "Fictional GP",
            "Fictional pharmacy", "Fictional representative", "Consent given", "Has capacity", "Large print passport",
            "Fictional history", "Remain at home", "Walk to the garden", "Discuss wishes before intervention",
            new DateTimeOffset(2025, 1, 2, 10, 0, 0, TimeSpan.Zero), null);
        Guid? recordId = null;
        foreach (var revision in new[] { request, request with { PreferredName = "Updated preferred name", PersonalHistory = "Updated history",
            DesiredOutcomes = "Walk safely with support", DischargedAt = new DateTimeOffset(2025, 3, 4, 10, 0, 0, TimeSpan.Zero) } })
        {
            var saved = await client.PutAsJsonAsync($"/api/phase1/service-users/{person.Id}/person-record", revision);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            var record = (await saved.Content.ReadFromJsonAsync<PersonRecord>())!;
            if (recordId is null) recordId = record.Id;
            Assert.Equal(recordId.Value, record.Id);
            Assert.Equal(branch, record.BranchId);
            Assert.Equal(TenantDefaults.OrganizationId, record.OrganizationId);

            var complete = await client.GetFromJsonAsync<JsonElement>($"/api/phase1/service-users/{person.Id}/complete-record");
            var reloaded = complete.GetProperty("record").Deserialize<PersonRecord>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            // PostgreSQL stores timestamps at microsecond precision.
            Assert.Equal(record with { LastReviewedAt = reloaded.LastReviewedAt }, reloaded);
            Assert.InRange(Math.Abs((record.LastReviewedAt - reloaded.LastReviewedAt).Ticks), 0L, 10L);
            var expected = JsonSerializer.SerializeToElement(revision);
            var actual = JsonSerializer.SerializeToElement(reloaded);
            foreach (var field in expected.EnumerateObject())
                Assert.Equal(field.Value.GetRawText(), actual.GetProperty(field.Name).GetRawText());

            using var verify = factory.Services.CreateScope();
            var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
            Assert.Equal(reloaded, await verifyDb.PersonRecords.AsNoTracking().SingleAsync(item => item.ServiceUserId == person.Id));
        }

        using var evidence = factory.Services.CreateScope();
        var evidenceDb = evidence.ServiceProvider.GetRequiredService<CareDbContext>();
        var history = await evidenceDb.AuditEvents.Where(item => item.EntityId == recordId
            && (item.Action == "person_record.created" || item.Action == "person_record.updated")).ToListAsync();
        Assert.Equal(2, history.Count);
        Assert.Single(history, item => item.Action == "person_record.created");
        Assert.Single(history, item => item.Action == "person_record.updated");
        Assert.All(history, item => { Assert.Equal(actor, item.Actor); Assert.Equal(branch, item.BranchId);
            Assert.Equal(TenantDefaults.OrganizationId, item.OrganizationId); });
    }

    private static CreateServiceUserRequest Request() => new(
        $"Fictional onboarding person {Guid.NewGuid():N}", new DateOnly(1945, 6, 7), "+441234567890",
        "Support washing and meals", "Fictional daughter", "Fictional preferred worker", "1 Fictional Street",
        "Penicillin rash", "Arthritis", "Private", "Female", "https://example.test/person.png",
        "Walking frame", "Decision support", "Large print", "Morning routine", "Vegetarian",
        new ProfileContactRequest($"fictional.{Guid.NewGuid():N}@example.test", "+447700900001", "1 Fictional Street",
            "AB1 2CD", "Fictional daughter", "Daughter", "+447700900002", "Email"));

    private async Task<(HttpClient Client, string Actor)> ManagerClient(UserRole role = UserRole.CareManager)
    {
        await factory.EnsureClinicalSeedAsync();
        var name = $"onboarding.manager.{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.AppUsers.Add(new AppUser(Guid.NewGuid(), name, $"{name}@example.test", PasswordHasher.HashPassword("Admin123!"),
                role, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId, null, null));
            await db.SaveChangesAsync();
        }
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = name, password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<Login>())!.Token);
        return (client, name);
    }

    private sealed record Login(string Token);
}
