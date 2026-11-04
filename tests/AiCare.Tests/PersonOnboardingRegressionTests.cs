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

    private static CreateServiceUserRequest Request() => new(
        $"Fictional onboarding person {Guid.NewGuid():N}", new DateOnly(1945, 6, 7), "+441234567890",
        "Support washing and meals", "Fictional daughter", "Fictional preferred worker", "1 Fictional Street",
        "Penicillin rash", "Arthritis", "Private", "Female", "https://example.test/person.png",
        "Walking frame", "Decision support", "Large print", "Morning routine", "Vegetarian",
        new ProfileContactRequest($"fictional.{Guid.NewGuid():N}@example.test", "+447700900001", "1 Fictional Street",
            "AB1 2CD", "Fictional daughter", "Daughter", "+447700900002", "Email"));

    private async Task<(HttpClient Client, string Actor)> ManagerClient()
    {
        await factory.EnsureClinicalSeedAsync();
        var name = $"onboarding.manager.{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.AppUsers.Add(new AppUser(Guid.NewGuid(), name, $"{name}@example.test", PasswordHasher.HashPassword("Admin123!"),
                UserRole.CareManager, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId, null, null));
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
