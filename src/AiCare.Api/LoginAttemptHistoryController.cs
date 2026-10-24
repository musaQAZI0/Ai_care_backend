using System.Data;
using AiCare.Application;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[ApiController, Authorize(Policy = "Phase1User", Roles = "Administrator")]
[Route("api/phase1/admin/login-attempts")]
public sealed class LoginAttemptHistoryController(CareDbContext db, ITenantContext tenant) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? userId, [FromQuery] int limit = 50, CancellationToken token = default)
    {
        if (limit is < 1 or > 200) return BadRequest(new { message = "Choose a limit between 1 and 200." });
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select a.user_id, u."UserName", a.outcome, a.ip_address, a.occurred_at
            from auth_login_attempts a
            join "AppUsers" u on u."Id"=a.user_id
            where a.organization_id=@organization and (@wide or a.branch_id=@branch)
              and (cast(@user as uuid) is null or a.user_id=cast(@user as uuid))
            order by a.occurred_at desc
            limit @limit
            """;
        Add("organization", tenant.OrganizationId);
        Add("wide", tenant.IsOrganizationWide);
        Add("branch", tenant.BranchId);
        Add("user", userId);
        Add("limit", limit);
        var rows = new List<object>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) rows.Add(new
        {
            userId = reader.GetGuid(0),
            userName = reader.GetString(1),
            outcome = reader.GetString(2),
            ipAddress = reader.GetString(3),
            occurredAt = reader.GetFieldValue<DateTimeOffset>(4)
        });
        return Ok(rows);

        void Add(string key, object? value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = key;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }
}
