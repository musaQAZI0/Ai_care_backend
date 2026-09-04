using System.Data;
using System.Data.Common;
using System.Security.Claims;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

public sealed class PrivilegedAccessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, CareDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        // Privileged access is backed by PostgreSQL-only tables and query features
        // (uuid, timestamptz and text arrays). Lightweight API tests use SQLite,
        // where those migration-owned tables cannot be created by EnsureCreated.
        // Preserve the caller's ordinary permissions instead of failing requests.
        if (!db.Database.IsNpgsql())
        {
            await next(context);
            return;
        }

        var userId = ReadGuid(context.User, ClaimTypes.NameIdentifier) ?? ReadGuid(context.User, "sub");
        var organizationId = ReadGuid(context.User, "organization_id");
        var branchId = ReadGuid(context.User, "branch_id");
        if (userId is null || organizationId is null)
        {
            await next(context);
            return;
        }

        var routePerson = context.Request.RouteValues.Values
            .Select(value => Guid.TryParse(value?.ToString(), out var id) ? id : (Guid?)null)
            .FirstOrDefault(value => value is not null);
        var path = context.Request.Path.Value ?? "";
        var method = context.Request.Method.ToUpperInvariant();
        var access = await FindAccess(db, userId.Value, organizationId.Value, branchId, routePerson, path, method, context.RequestAborted);
        if (access is not null)
        {
            var identity = new ClaimsIdentity("PrivilegedAccess");
            identity.AddClaim(new Claim(ClaimTypes.Role, access.Role));
            identity.AddClaim(new Claim("privileged_access_id", access.Id.ToString()));
            identity.AddClaim(new Claim("privileged_access_type", access.Type));
            context.User.AddIdentity(identity);
            await RecordUse(db, access, userId.Value, context.User.Identity?.Name ?? "unknown", organizationId.Value, branchId, path, method, context.RequestAborted);
        }

        await next(context);
    }

    private static async Task<AccessGrant?> FindAccess(CareDbContext db, Guid userId, Guid organizationId, Guid? branchId, Guid? routePerson, string path, string method, CancellationToken token)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select id,'Delegation',delegated_role,branch_id,person_id,route_prefix,http_methods
            from access_delegations
            where organization_id=@organization and granted_to_user_id=@user and status='Active'
              and starts_at<=now() and expires_at>now()
              and (branch_id is null or branch_id=@branch)
            union all
            select id,'Emergency','CareCoordinator',branch_id,person_id,route_prefix,http_methods
            from emergency_access_grants
            where organization_id=@organization and user_id=@user and status='Active' and expires_at>now()
              and branch_id=@branch
            order by 2 desc
            """;
        Add(command, "organization", organizationId);
        Add(command, "user", userId);
        Add(command, "branch", branchId);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var person = reader.IsDBNull(4) ? (Guid?)null : reader.GetGuid(4);
            var prefix = reader.GetString(5);
            var methods = reader.GetFieldValue<string[]>(6);
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !methods.Contains(method, StringComparer.OrdinalIgnoreCase)) continue;
            if (person is not null && routePerson != person) continue;
            return new AccessGrant(reader.GetGuid(0), reader.GetString(1), reader.GetString(2));
        }
        return null;
    }

    private static async Task RecordUse(CareDbContext db, AccessGrant access, Guid userId, string actor, Guid organizationId, Guid? branchId, string path, string method, CancellationToken token)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "insert into privileged_access_events(id,organization_id,branch_id,access_type,access_id,event_type,actor_user_id,actor,detail) values(@id,@organization,@branch,@type,@access,'Used',@user,@actor,@detail)";
        Add(command, "id", Guid.NewGuid()); Add(command, "organization", organizationId); Add(command, "branch", branchId); Add(command, "type", access.Type); Add(command, "access", access.Id); Add(command, "user", userId); Add(command, "actor", actor); Add(command, "detail", $"{method} {path}");
        await command.ExecuteNonQueryAsync(token);
    }

    private static Guid? ReadGuid(ClaimsPrincipal user, string type) => Guid.TryParse(user.FindFirstValue(type), out var id) ? id : null;
    private static void Add(DbCommand command, string name, object? value) { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; command.Parameters.Add(parameter); }
    private sealed record AccessGrant(Guid Id, string Type, string Role);
}
