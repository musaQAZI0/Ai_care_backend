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
public sealed class ClientAndAppointmentSettingsRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task ConfiguredClientLeavingReasonControlsDischargeAndIsRecorded()
    {
        await factory.EnsureClinicalSeedAsync();
        var personId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.ServiceUsers.Add(Person(personId));
            await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient();
        await Authenticate(client);
        var reason = $"Completed package {Guid.NewGuid():N}";
        var added = await client.PostAsJsonAsync("/api/phase1/care-operations/options", new { kind = "ClientLeavingReason", value = reason, active = true });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var optionId = (await added.Content.ReadFromJsonAsync<CreatedId>())!.Id;
        try
        {
            var path = $"/api/phase1/person-lifecycle/service-users/{personId}/status";
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { status = "Discharged", reason = "Other" })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(path, new { status = "Discharged", reason })).StatusCode);
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            Assert.Equal("Discharged", (await db.ServiceUsers.SingleAsync(item => item.Id == personId)).Status);
            Assert.Contains(reason, await client.GetStringAsync($"/api/phase1/person-lifecycle/service-users/{personId}"));
        }
        finally
        {
            await client.PutAsJsonAsync($"/api/phase1/care-operations/options/{optionId}", new { kind = "ClientLeavingReason", value = reason, active = false });
        }
    }

    [Fact]
    public async Task StartVarianceSettingBlocksRescheduleOutsideConfiguredWindow()
    {
        await factory.EnsureClinicalSeedAsync();
        var personId = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var visitId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddDays(40);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.ServiceUsers.Add(Person(personId));
            db.CareWorkers.Add(new CareWorker(workerId, "Variance worker", "Care", "Flexible", 0, 0, "Valid", "Compliant", "Local", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.Visits.Add(new Visit(visitId, personId, workerId, start, "Care", 30, "", VisitStatus.Scheduled, null, null, null, null, null, null, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient();
        await Authenticate(client);
        const string policyPath = "/api/phase1/admin/appointment-start-variance";
        var previous = await client.GetFromJsonAsync<AppointmentPolicy>(policyPath);
        try
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(policyPath, new { maxChangeMinutes = 15 })).StatusCode);
            Assert.Contains("15", await client.GetStringAsync(policyPath));
            var change = new { serviceUserId = personId, careWorkerId = workerId, startsAt = start.AddHours(1), visitType = "Care", durationMinutes = 30, requiredSkills = "", changeReason = "Family requested later visit" };
            var response = await client.PutAsJsonAsync($"/api/phase1/visits/{visitId}", change);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("15 minutes", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await client.PutAsJsonAsync(policyPath, new { maxChangeMinutes = previous?.MaxChangeMinutes });
        }
    }

    [Fact]
    public async Task StartVarianceSettingControlsRecurringEditsAndClearingRestoresUnrestrictedChanges()
    {
        await factory.EnsureClinicalSeedAsync();
        var person=Guid.NewGuid(); var worker=Guid.NewGuid();
        var local=DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(57).AddHours(10),DateTimeKind.Unspecified);
        var starts=new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local,TimeZoneInfo.FindSystemTimeZoneById("Europe/London")));
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.ServiceUsers.Add(Person(person));
            db.CareWorkers.Add(new(worker,"Variance series worker","Care","Flexible",0,0,"Valid","Compliant","Local",TenantDefaults.OrganizationId,TenantDefaults.BranchId));
            await db.SaveChangesAsync();
        }
        using var client=factory.CreateClient();
        await Authenticate(client);
        const string policyPath="/api/phase1/admin/appointment-start-variance";
        var previous=await client.GetFromJsonAsync<AppointmentPolicy>(policyPath);
        try
        {
            Assert.Equal(HttpStatusCode.NoContent,(await client.PutAsJsonAsync(policyPath,new{maxChangeMinutes=15})).StatusCode);
            Assert.Equal(15,(await client.GetFromJsonAsync<AppointmentPolicy>(policyPath))!.MaxChangeMinutes);
            var created=await client.PostAsJsonAsync("/api/phase1/visits/recurring",new{
                serviceUserId=person,careWorkerId=worker,startsAt=starts,visitType="Variance series",durationMinutes=30,requiredSkills="",frequency="Weekly",occurrences=3
            });
            Assert.Equal(HttpStatusCode.Created,created.StatusCode);
            var body=await created.Content.ReadFromJsonAsync<JsonElement>();
            var series=body.GetProperty("seriesId").GetGuid();
            var ids=body.GetProperty("visits").EnumerateArray().Select(x=>x.GetProperty("id").GetGuid()).ToArray();
            var path=$"/api/phase1/visits/recurring-series/{series}";
            object Edit(string time)=>new{careWorkerId=worker,visitType="Variance series",durationMinutes=30,requiredSkills="",startTime=time,changeReason="Synthetic variance acceptance"};
            var blocked=await client.PutAsJsonAsync(path,Edit("11:00"));
            Assert.Equal(HttpStatusCode.BadRequest,blocked.StatusCode);
            Assert.Contains("15 minutes",await blocked.Content.ReadAsStringAsync());
            using(var scope=factory.Services.CreateScope())
            {
                var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
                var visits=await db.Visits.AsNoTracking().Where(v=>ids.Contains(v.Id)).OrderBy(v=>v.StartsAt).ToListAsync();
                Assert.Equal(3,visits.Count);
                for(var i=0;i<visits.Count;i++)
                    Assert.Equal(new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local.AddDays(i*7),TimeZoneInfo.FindSystemTimeZoneById("Europe/London"))),visits[i].StartsAt);
                Assert.False(await db.AuditEvents.AnyAsync(a=>a.EntityId==series&&a.Action=="visit.recurring_series_updated"));
            }
            Assert.Equal(HttpStatusCode.OK,(await client.PutAsJsonAsync(path,Edit("10:15"))).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent,(await client.PutAsJsonAsync(policyPath,new{maxChangeMinutes=(int?)null})).StatusCode);
            Assert.Null((await client.GetFromJsonAsync<AppointmentPolicy>(policyPath))!.MaxChangeMinutes);
            Assert.Equal(HttpStatusCode.OK,(await client.PutAsJsonAsync(path,Edit("11:00"))).StatusCode);
            using(var scope=factory.Services.CreateScope())
            {
                var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
                var visits=await db.Visits.AsNoTracking().Where(v=>ids.Contains(v.Id)).ToListAsync();
                Assert.Equal(3,visits.Count);
                Assert.All(visits,v=>Assert.Equal(new TimeOnly(11,0),TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(v.StartsAt,TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime)));
            }
        }
        finally
        {
            Assert.Equal(HttpStatusCode.NoContent,(await client.PutAsJsonAsync(policyPath,new{maxChangeMinutes=previous?.MaxChangeMinutes})).StatusCode);
        }
    }

    private static ServiceUser Person(Guid id) => new(id, "Settings regression client", new DateOnly(1950, 1, 1), "+10000000000", "Care", "Contact", "", RiskLevel.Low, "Active", "Address", "None", "None", "Private", "", "", "Independent", "Independent", "Verbal", "None", "Standard", TenantDefaults.OrganizationId, TenantDefaults.BranchId);

    private static async Task Authenticate(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "Admin123!", mfaCode = (string?)null });
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<Login>())!.Token);
    }

    private sealed record Login(string Token);
    private sealed record CreatedId(Guid Id);
    private sealed record AppointmentPolicy(int? MaxChangeMinutes);
}
