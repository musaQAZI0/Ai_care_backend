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
public sealed class SchedulingQualificationPolicyRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task EveryApplicableMandatoryCourseRequiresCurrentCompletionEvidence()
    {
        var fixture=await Fixture("Mandatory course","Care"); var course=Guid.NewGuid(); var starts=DateTimeOffset.UtcNow.AddDays(50);
        using (var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                insert into training_catalogue_courses(id,organization_id,branch_id,code,title,is_mandatory,active,required_job_titles)
                values({course},{TenantDefaults.OrganizationId},{fixture.Branch},{"MAND-"+course.ToString("N")},{"Synthetic mandatory course"},true,true,ARRAY[]::text[])
                """);
        }
        using var client=await Client(fixture.Actor);
        var request=Request(fixture,starts,"");
        var missing=await client.PostAsJsonAsync("/api/phase1/visits/conflicts",request);
        Assert.Equal(HttpStatusCode.OK,missing.StatusCode);
        Assert.Contains("mandatory-course-missing",await missing.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/api/phase1/visits",request)).StatusCode);
        using (var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                insert into worker_training_bookings(id,course_id,care_worker_id,organization_id,branch_id,scheduled_at,status,completion_evidence,completed_at,expires_at,booked_by)
                values({Guid.NewGuid()},{course},{fixture.Worker},{TenantDefaults.OrganizationId},{fixture.Branch},{starts.AddDays(-10)},'Completed','Synthetic verified completion',{starts.AddDays(-9)},{starts.AddHours(1)},{fixture.Actor})
                """);
        }
        var eligible=await client.PostAsJsonAsync("/api/phase1/visits/conflicts",request);
        Assert.DoesNotContain("mandatory-course-missing",await eligible.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created,(await client.PostAsJsonAsync("/api/phase1/visits",request)).StatusCode);
    }

    [Fact]
    public async Task DeclaredSpecialisationDoesNotReplaceVerifiedSkillEvidence()
    {
        var fixture=await Fixture("Verified skill","Medication support"); var starts=DateTimeOffset.UtcNow.AddDays(51);
        using var client=await Client(fixture.Actor); var request=Request(fixture,starts,"Medication support");
        var declaredOnly=await client.PostAsJsonAsync("/api/phase1/visits/conflicts",request);
        Assert.Contains("skill-gap",await declaredOnly.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/api/phase1/visits",request)).StatusCode);
        using (var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                insert into worker_competency_records(id,care_worker_id,organization_id,branch_id,competency,status,assessed_by,assessed_at,expires_at)
                values({Guid.NewGuid()},{fixture.Worker},{TenantDefaults.OrganizationId},{fixture.Branch},{"Medication support"},'Valid',{fixture.Actor},{starts.AddDays(-5)},{starts.AddHours(1)})
                """);
        }
        var verified=await client.PostAsJsonAsync("/api/phase1/visits/conflicts",request);
        Assert.DoesNotContain("skill-gap",await verified.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created,(await client.PostAsJsonAsync("/api/phase1/visits",request)).StatusCode);
    }

    private async Task<FixtureData> Fixture(string label,string specialisation)
    {
        await factory.EnsureClinicalSeedAsync(); var branch=Guid.NewGuid(); var person=Guid.NewGuid(); var worker=Guid.NewGuid(); var actor=$"qualification.{Guid.NewGuid():N}";
        using var scope=factory.Services.CreateScope(); var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
        db.Branches.Add(new(branch,TenantDefaults.OrganizationId,label+" branch","Test","Active"));
        db.ServiceUsers.Add(new(person,label+" person",new DateOnly(1950,1,1),"1","Care","Contact","",RiskLevel.Low,"Active","Address","None","None","Private","","","Independent","Independent","Verbal","Routine","Standard",TenantDefaults.OrganizationId,branch));
        db.CareWorkers.Add(new(worker,label+" worker",specialisation,"Flexible",0,0,"Valid","Compliant","Local",TenantDefaults.OrganizationId,branch));
        db.AppUsers.Add(new(Guid.NewGuid(),actor,actor+"@test.invalid",PasswordHasher.HashPassword("Admin123!"),UserRole.CareManager,true,TenantDefaults.OrganizationId,branch));
        await db.SaveChangesAsync(); return new(branch,person,worker,actor);
    }
    private static object Request(FixtureData fixture,DateTimeOffset starts,string skills)=>new { serviceUserId=fixture.Person,careWorkerId=fixture.Worker,additionalCareWorkerIds=Array.Empty<Guid>(),startsAt=starts,visitType="Qualification policy",durationMinutes=30,requiredSkills=skills };
    private async Task<HttpClient> Client(string actor){var client=factory.CreateClient();var login=await client.PostAsJsonAsync("/api/auth/login",new{userName=actor,password="Admin123!",mfaCode=(string?)null});login.EnsureSuccessStatusCode();client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await login.Content.ReadFromJsonAsync<Login>())!.Token);return client;}
    private sealed record FixtureData(Guid Branch,Guid Person,Guid Worker,string Actor);
    private sealed record Login(string Token);
}
