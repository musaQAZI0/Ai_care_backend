using System.Data;
using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiCare.Api;

[ApiController]
[Authorize]
[Route("api/security/step-up")]
public sealed class StepUpSecurityController(CareDbContext db, IOptions<JwtOptions> jwtOptions) : ControllerBase
{
    private readonly JwtOptions _jwtOptions = jwtOptions.Value;

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        var user = CurrentUser();
        if (user is null) return Unauthorized();

        var grants = await Rows(
            "select id,purpose,operation_scope,created_at,expires_at,consumed_at from auth_step_up_grants where user_id=@user and organization_id=@organization and revoked_at is null and expires_at>now() and session_id=@session order by expires_at desc",
            command =>
            {
                Add(command, "user", user.Id);
                Add(command, "organization", user.OrganizationId ?? TenantDefaults.OrganizationId);
                Add(command, "session", CurrentSession());
            },
            cancellationToken);
        return Ok(new { activeGrants = grants });
    }

    [HttpPost("challenge")]
    public async Task<IActionResult> Challenge(StepUpChallengeRequest request, CancellationToken cancellationToken)
    {
        var user = CurrentUser();
        if (user is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(request.Code)) return BadRequest(new { message = "Authenticator code is required." });

        var purpose = NormalizePurpose(request.Purpose);
        if (purpose == "") return BadRequest(new { message = "A valid step-up purpose is required." });

        var state = await MfaState(user.Id, cancellationToken);
        if (!state.Enabled || string.IsNullOrWhiteSpace(state.Secret))
            return Conflict(new { message = "MFA must be active before step-up can be granted." });

        var session = CurrentSession();
        if (session is null) return Unauthorized(new { message = "An active authenticated session is required for step-up." });
        var scope = Trim(request.OperationScope, 160, "*");
        if (!MfaSecurity.VerifyTotp(state.Secret, request.Code))
        {
            await Event(null, user, session, "Denied", purpose, scope, "Invalid authenticator code", cancellationToken);
            Audit("security.step_up_denied", user.Id, user);
            await db.SaveChangesAsync(cancellationToken);
            return Unauthorized(new { message = "Authenticator code is invalid." });
        }

        var grantId = Guid.NewGuid();
        var evidence = Trim(request.Evidence, 240, "MFA authenticator challenge");
        await Exec(
            "insert into auth_step_up_grants(id,user_id,session_id,organization_id,branch_id,purpose,operation_scope,evidence,expires_at) values(@id,@user,@session,@organization,@branch,@purpose,@scope,@evidence,now()+interval '10 minutes')",
            command =>
            {
                Add(command, "id", grantId);
                Add(command, "user", user.Id);
                Add(command, "session", session);
                Add(command, "organization", user.OrganizationId ?? TenantDefaults.OrganizationId);
                Add(command, "branch", user.BranchId);
                Add(command, "purpose", purpose);
                Add(command, "scope", scope);
                Add(command, "evidence", evidence);
            },
            cancellationToken);
        await Event(grantId, user, session, "Granted", purpose, scope, evidence, cancellationToken);
        Audit("security.step_up_granted", grantId, user);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { id = grantId, purpose, operationScope = scope, expiresAt = DateTimeOffset.UtcNow.AddMinutes(10) });
    }

    private async Task<(bool Enabled, string Secret)> MfaState(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = await Command("select mfa_enabled,mfa_secret from auth_user_security where user_id=@user", cancellationToken);
            Add(command, "user", userId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return (false, "");
            return (reader.GetBoolean(0), reader.IsDBNull(1) ? "" : MfaSecurity.Unprotect(reader.GetString(1), _jwtOptions.SigningKey));
        }
        catch
        {
            return (false, "");
        }
    }

    private Task Event(Guid? grantId, AppUser user, Guid? sessionId, string eventType, string purpose, string scope, string detail, CancellationToken cancellationToken) =>
        Exec(
            "insert into auth_step_up_events(id,grant_id,user_id,session_id,organization_id,branch_id,event_type,purpose,operation_scope,detail,actor) values(@event,@grant,@user,@session,@organization,@branch,@type,@purpose,@scope,@detail,@actor)",
            command =>
            {
                Add(command, "event", Guid.NewGuid());
                Add(command, "grant", grantId);
                Add(command, "user", user.Id);
                Add(command, "session", sessionId);
                Add(command, "organization", user.OrganizationId ?? TenantDefaults.OrganizationId);
                Add(command, "branch", user.BranchId);
                Add(command, "type", eventType);
                Add(command, "purpose", purpose);
                Add(command, "scope", scope);
                Add(command, "detail", detail);
                Add(command, "actor", user.UserName);
            },
            cancellationToken);

    private void Audit(string action, Guid id, AppUser user) =>
        db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), action, user.UserName, "StepUpGrant", id, DateTimeOffset.UtcNow, user.OrganizationId, user.BranchId));

    private AppUser? CurrentUser()
    {
        var subject = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(subject, out var id) ? db.AppUsers.SingleOrDefault(user => user.Id == id && user.IsActive) : null;
    }

    private Guid? CurrentSession() => Guid.TryParse(User.FindFirstValue("sid"), out var id) ? id : null;

    private async Task<List<Dictionary<string, object?>>> Rows(string sql, Action<DbCommand> bind, CancellationToken cancellationToken)
    {
        await using var command = await Command(sql, cancellationToken);
        bind(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++) row[Camel(reader.GetName(i))] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private async Task<int> Exec(string sql, Action<DbCommand> bind, CancellationToken cancellationToken)
    {
        await using var command = await Command(sql, cancellationToken);
        bind(command);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<DbCommand> Command(string sql, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    private static string NormalizePurpose(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "admin" => "admin",
        "export" => "export",
        "medication" => "medication",
        "payroll" => "payroll",
        "privacy" => "privacy",
        "security" => "security",
        _ => ""
    };

    private static string Trim(string? value, int maxLength, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return text[..Math.Min(text.Length, maxLength)];
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        if (command.Parameters.Contains(name)) return;
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string Camel(string value)
    {
        var parts = value.Split('_');
        return parts[0] + string.Concat(parts.Skip(1).Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }
}

public sealed record StepUpChallengeRequest(string Purpose, string? OperationScope, string Code, string? Evidence);
