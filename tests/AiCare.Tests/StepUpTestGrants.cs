using System.IdentityModel.Tokens.Jwt;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiCare.Tests;

internal static class StepUpTestGrants
{
    internal static async Task GrantAsync(PostgresRegressionFactory factory, HttpClient client, params string[] purposes)
    {
        var token = client.DefaultRequestHeaders.Authorization?.Parameter ?? throw new InvalidOperationException("Authenticated client required.");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var user = Guid.Parse(jwt.Claims.Single(claim => claim.Type == "sub").Value);
        var session = Guid.Parse(jwt.Claims.Single(claim => claim.Type == "sid").Value);
        var organization = Guid.Parse(jwt.Claims.Single(claim => claim.Type == "organization_id").Value);
        var branchClaim = jwt.Claims.SingleOrDefault(claim => claim.Type == "branch_id")?.Value;
        var branch = Guid.TryParse(branchClaim, out var branchId) ? branchId : (Guid?)null;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        foreach (var purpose in purposes.Distinct())
            await db.Database.ExecuteSqlInterpolatedAsync($"insert into auth_step_up_grants(id,user_id,session_id,organization_id,branch_id,purpose,operation_scope,evidence,expires_at) values({Guid.NewGuid()},{user},{session},{organization},{branch},{purpose},{"*"},{"Regression fixture grant"},{DateTimeOffset.UtcNow.AddMinutes(10)})");
    }
}
