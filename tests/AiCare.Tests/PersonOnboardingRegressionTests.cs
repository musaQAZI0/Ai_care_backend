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

        var revisions = (await client.GetFromJsonAsync<List<PersonRecordRevision>>(
            $"/api/phase1/service-users/{person.Id}/person-record/history"))!;
        Assert.Equal(2, revisions.Count);
        var initial = Assert.Single(revisions, item => item.BeforeJson is null);
        var changed = Assert.Single(revisions, item => item.BeforeJson is not null);
        var before = JsonSerializer.Deserialize<PersonRecord>(changed.BeforeJson!)!;
        var after = JsonSerializer.Deserialize<PersonRecord>(changed.AfterJson)!;
        Assert.Equal(request.PreferredName, before.PreferredName);
        Assert.Equal("Updated preferred name", after.PreferredName);
        Assert.Equal("Updated history", after.PersonalHistory);
        Assert.Equal(JsonSerializer.Deserialize<PersonRecord>(initial.AfterJson)! with { LastReviewedAt = before.LastReviewedAt }, before);
        Assert.All(revisions, item => { Assert.Equal(actor, item.Actor); Assert.Equal(branch, item.BranchId);
            Assert.Equal(person.Id, item.ServiceUserId); Assert.Equal(recordId.Value, item.PersonRecordId); });
        Assert.True(await evidenceDb.AuditEvents.AnyAsync(item => item.Action == "person_record.history_viewed"
            && item.EntityId == person.Id && item.Actor == actor && item.BranchId == branch));
        var export = await client.GetFromJsonAsync<JsonElement>($"/api/phase1/data-governance/service-users/{person.Id}/export");
        Assert.Equal(2, export.GetProperty("personRecordRevisions").GetArrayLength());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProfileAndContactHistoryRetainsPriorValuesAndPersonBranch(bool administratorAcrossBranches)
    {
        var (client, actor) = await ManagerClient(administratorAcrossBranches ? UserRole.Administrator : UserRole.CareManager);
        using var ownedClient = client;
        var request = Request();
        var create = await client.PostAsJsonAsync("/api/phase1/service-users", request);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var person = (await create.Content.ReadFromJsonAsync<ServiceUser>())!;
        var branch = TenantDefaults.BranchId;
        if (administratorAcrossBranches)
        {
            branch = Guid.NewGuid();
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Branches.Add(new Branch(branch, TenantDefaults.OrganizationId, "Profile history branch", "North", "Active"));
            var tracked = await db.ServiceUsers.SingleAsync(item => item.Id == person.Id);
            db.Entry(tracked).CurrentValues.SetValues(tracked with { BranchId = branch });
            await db.SaveChangesAsync();
        }
        var edit = request with { FullName = request.FullName + " updated", Address = "New fictional address",
            CareNeeds = "Updated support", Contact = request.Contact! with { Postcode = " cd3 4ef ", EmergencyContactName = "New fictional contact" } };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/phase1/service-users/{person.Id}", edit)).StatusCode);
        // Omitted contact means preserve the existing contact, including in the snapshot.
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/phase1/service-users/{person.Id}", edit with { Contact = null })).StatusCode);
        var revisions = (await client.GetFromJsonAsync<List<ServiceUserProfileRevision>>($"/api/phase1/service-users/{person.Id}/profile/history"))!;
        Assert.Equal(3, revisions.Count);
        var initial = Assert.Single(revisions, item => item.BeforeJson is null);
        using var initialJson = JsonDocument.Parse(initial.AfterJson);
        Assert.Equal(request.FullName, initialJson.RootElement.GetProperty("Profile").GetProperty("FullName").GetString());
        var edits = revisions.Where(item => item.BeforeJson is not null).ToList();
        Assert.All(edits, revision =>
        {
            Assert.Equal(branch, revision.BranchId); Assert.Equal(actor, revision.Actor);
            using var after = JsonDocument.Parse(revision.AfterJson);
            Assert.Equal(edit.FullName, after.RootElement.GetProperty("Profile").GetProperty("FullName").GetString());
            Assert.Equal("CD3 4EF", after.RootElement.GetProperty("Contact").GetProperty("Postcode").GetString());
            Assert.Equal("New fictional contact", after.RootElement.GetProperty("Contact").GetProperty("EmergencyContactName").GetString());
        });
        Assert.Contains(edits, revision => JsonDocument.Parse(revision.BeforeJson!).RootElement.GetProperty("Profile").GetProperty("FullName").GetString() == request.FullName);
        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal(3, await verifyDb.ServiceUserProfileRevisions.CountAsync(item => item.ServiceUserId == person.Id));
        Assert.True(await verifyDb.AuditEvents.AnyAsync(item => item.Action == "service_user.updated" && item.EntityId == person.Id && item.BranchId == branch && item.Actor == actor));
        var export = await client.GetFromJsonAsync<JsonElement>($"/api/phase1/data-governance/service-users/{person.Id}/export");
        Assert.Equal(3, export.GetProperty("profileRevisions").GetArrayLength());
    }

    [Fact]
    public async Task ProfileAndContactWriteRollBackIfRevisionPersistenceFails()
    {
        var (client, _) = await ManagerClient();
        using var ownedClient = client;
        var request = Request();
        var create = await client.PostAsJsonAsync("/api/phase1/service-users", request);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var person = (await create.Content.ReadFromJsonAsync<ServiceUser>())!;
        using var setup = factory.Services.CreateScope();
        var db = setup.ServiceProvider.GetRequiredService<CareDbContext>();
        // A deterministic database failure after the normal validation boundary.
        await db.Database.ExecuteSqlRawAsync($"""ALTER TABLE "ServiceUserProfileRevisions" ADD CONSTRAINT "ck_regression_reject_profile_update" CHECK ("ServiceUserId" <> '{person.Id:D}'::uuid OR "BeforeJson" IS NULL)""");
        try
        {
            var failed = await client.PutAsJsonAsync($"/api/phase1/service-users/{person.Id}", request with
            { FullName = "Must not persist", Contact = request.Contact! with { EmergencyContactName = "Must not persist" } });
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("""ALTER TABLE "ServiceUserProfileRevisions" DROP CONSTRAINT "ck_regression_reject_profile_update"""");
        }
        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal(person, await verifyDb.ServiceUsers.AsNoTracking().SingleAsync(item => item.Id == person.Id));
        Assert.Equal(request.Contact!.EmergencyContactName, (await verifyDb.ProfileContactDetails.SingleAsync(item => item.SubjectId == person.Id)).EmergencyContactName);
        Assert.Equal(1, await verifyDb.ServiceUserProfileRevisions.CountAsync(item => item.ServiceUserId == person.Id));
        Assert.False(await verifyDb.AuditEvents.AnyAsync(item => item.Action == "service_user.updated" && item.EntityId == person.Id));
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
