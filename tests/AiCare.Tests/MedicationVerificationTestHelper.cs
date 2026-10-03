using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiCare.Tests;

internal static class MedicationVerificationTestHelper
{
    private const string ReviewerName = "regression.medication.reviewer";

    internal static async Task<HttpResponseMessage> SaveAndVerifyAsync(
        PostgresRegressionFactory factory, HttpClient preparer, Guid medicationId, object draftValues)
    {
        using (var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            if (!await db.AppUsers.AnyAsync(x=>x.UserName==ReviewerName))
            {
                db.AppUsers.Add(new AppUser(Guid.NewGuid(),ReviewerName,ReviewerName+"@aicare.local",
                    PasswordHasher.HashPassword("Manager123!"),UserRole.CareManager,true,
                    TenantDefaults.OrganizationId,TenantDefaults.BranchId,null,null));
                await db.SaveChangesAsync();
            }
        }
        var path=$"/api/phase1/medication-safety/medications/{medicationId}/profile";
        var body=JsonSerializer.SerializeToNode(draftValues,new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        body["reconciliationStatus"]="Draft";
        body["reconciledBy"]="Regression preparer";
        if (string.IsNullOrWhiteSpace(body["prnIndication"]?.GetValue<string>()))
            body["prnIndication"]="Not applicable";
        await StepUpTestGrants.GrantAsync(factory,preparer,"medication");
        var saved=await preparer.PutAsJsonAsync(path,body);
        if (!saved.IsSuccessStatusCode) return saved;
        await StepUpTestGrants.GrantAsync(factory,preparer,"medication");
        var submitted=await preparer.PostAsync(path+"/submit",null);
        if (!submitted.IsSuccessStatusCode) return submitted;
        using var json=JsonDocument.Parse(await submitted.Content.ReadAsStringAsync());
        var reviewId=json.RootElement.GetProperty("id").GetGuid();

        var reviewer=factory.CreateClient();
        var login=await reviewer.PostAsJsonAsync("/api/auth/login",
            new{userName=ReviewerName,password="Manager123!",mfaCode=(string?)null});
        login.EnsureSuccessStatusCode();
        using var loginJson=JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        reviewer.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",
            loginJson.RootElement.GetProperty("token").GetString());
        await StepUpTestGrants.GrantAsync(factory,reviewer,"medication");
        var approved=await reviewer.PostAsJsonAsync(path+"/reviews/"+reviewId,
            new{decision="Approve",reason="Regression signed prescription reviewed independently"});
        if (!approved.IsSuccessStatusCode) return approved;
        return await preparer.GetAsync(path);
    }
}
