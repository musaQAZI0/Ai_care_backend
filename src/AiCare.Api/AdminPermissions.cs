using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

public sealed record PermissionDefinition(string Key, string Label, string Description, UserRole[] Roles);

public static class AdminPermissionCatalog
{
    public static readonly PermissionDefinition[] All =
    [
        new("users.read", "Review users", "View accounts and access history.", [UserRole.Administrator]),
        new("users.manage", "Manage users", "Invite, suspend, unblock and change user access.", [UserRole.Administrator]),
        new("settings.manage", "Manage care options", "Change diary, monitoring and cancellation options.", [UserRole.Administrator]),
        new("training.manage", "Manage training courses", "Create, edit and deactivate training courses.", [UserRole.Administrator, UserRole.CareManager]),
        new("clients.manage", "Manage core client records", "Create, edit and archive core client records.", [UserRole.Administrator, UserRole.CareManager, UserRole.CareCoordinator]),
        new("workers.manage", "Manage carer profiles", "Create, edit and archive carer records and employment profiles.", [UserRole.Administrator, UserRole.CareManager]),
        new("visits.schedule", "Schedule visits", "Create and change rota visits.", [UserRole.Administrator, UserRole.CareManager, UserRole.CareCoordinator]),
        new("reports.export", "Generate and export reports", "Generate reports and download report files.", [UserRole.Administrator, UserRole.CareManager, UserRole.CareCoordinator, UserRole.BackOffice]),
    ];

    public static PermissionDefinition? Find(string key) => All.FirstOrDefault(item => item.Key == key);
    public static bool GrantedByRole(UserRole role, string key) => Find(key)?.Roles.Contains(role) == true;
}

public sealed class AdminPermissionService(CareDbContext db)
{
    public async Task<string[]> Denied(Guid organizationId, Guid userId, CancellationToken token)
    {
        if (!db.Database.IsNpgsql()) return [];
        return await db.Database.SqlQuery<string>($"select permission_key as \"Value\" from admin_permission_denials where organization_id={organizationId} and user_id={userId}").ToArrayAsync(token);
    }

    public async Task<bool> Allowed(Guid organizationId, Guid userId, UserRole role, string key, CancellationToken token)
    {
        if (!AdminPermissionCatalog.GrantedByRole(role, key)) return false;
        return !(await Denied(organizationId, userId, token)).Contains(key, StringComparer.Ordinal);
    }
}

public sealed class AdminPermissionGateMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AdminPermissionService permissions, ICurrentUserContext user, ITenantContext tenant)
    {
        var key = RequiredPermission(context.Request);
        if (key is null || context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }
        if (user.UserId is not Guid userId || user.Role is not UserRole role ||
            !await permissions.Allowed(tenant.OrganizationId, userId, role, key, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { message = "Your account does not have permission for this action.", permission = key }, context.RequestAborted);
            return;
        }
        await next(context);
    }

    private static string? RequiredPermission(HttpRequest request)
    {
        var path = (request.Path.Value ?? "").TrimEnd('/').ToLowerInvariant();
        var method = request.Method.ToUpperInvariant();
        if (path.StartsWith("/api/phase1/admin/accounts", StringComparison.Ordinal))
            return method == "GET" ? "users.read" : "users.manage";
        if (path.StartsWith("/api/phase1/admin/users", StringComparison.Ordinal))
            return method == "GET" ? "users.read" : "users.manage";
        if (path.StartsWith("/api/phase1/care-operations/options", StringComparison.Ordinal) && method != "GET")
            return "settings.manage";
        if (path == "/api/phase1/admin/appointment-start-variance" && method == "PUT") return "settings.manage";
        if (path.StartsWith("/api/phase1/admin/bank-holidays", StringComparison.Ordinal) && method != "GET") return "settings.manage";
        if (path.StartsWith("/api/phase1/admin/regions", StringComparison.Ordinal) && method != "GET") return "settings.manage";
        if (path.StartsWith("/api/phase1/admin/login-attempts", StringComparison.Ordinal)) return "users.read";
        if (path.StartsWith("/api/phase1/admin/appointment-configuration/visits/", StringComparison.Ordinal) && method == "PUT") return "visits.schedule";
        if (path.StartsWith("/api/phase1/admin/appointment-configuration/breaks", StringComparison.Ordinal) && method != "GET") return "visits.schedule";
        if (path == "/api/phase1/admin/appointment-configuration/recommendations" && method == "POST") return "visits.schedule";
        if (path.StartsWith("/api/phase1/admin/appointment-configuration", StringComparison.Ordinal) && method != "GET") return "settings.manage";
        if (path.StartsWith("/api/phase1/care-operations/training-courses", StringComparison.Ordinal) && method != "GET")
            return "training.manage";
        if (method == "POST" && (path == "/api/phase1/account-invitations" || path.StartsWith("/api/phase1/account-invitations/", StringComparison.Ordinal) && path.EndsWith("/revoke", StringComparison.Ordinal))) return "users.manage";
        if (method == "POST" && path.StartsWith("/api/security/access-reviews/", StringComparison.Ordinal) && path.EndsWith("/remediate", StringComparison.Ordinal))
            return "users.manage";
        if (method == "GET" && path.StartsWith("/api/phase1/operational-reports/", StringComparison.Ordinal) && path.EndsWith(".csv", StringComparison.Ordinal))
            return "reports.export";
        if (method == "GET" && (path.StartsWith("/api/phase1/reporting-compliance/", StringComparison.Ordinal) || path.StartsWith("/api/phase1/reporting-governance/", StringComparison.Ordinal)) && (path.EndsWith(".csv", StringComparison.Ordinal) || path.EndsWith("/csv", StringComparison.Ordinal)))
            return "reports.export";
        if (method == "GET" && path.StartsWith("/api/phase1/reports/", StringComparison.Ordinal) && (path.EndsWith("/csv", StringComparison.Ordinal) || path.EndsWith("/pdf", StringComparison.Ordinal)))
            return "reports.export";
        if (method == "POST" && (path is "/api/phase1/reports/generate" or "/api/phase1/reports/builder" or "/api/phase1/reporting-compliance/report-runs")) return "reports.export";
        if (method == "POST" && path == "/api/phase1/service-users") return "clients.manage";
        if (method == "POST" && path.StartsWith("/api/phase1/service-users/", StringComparison.Ordinal) &&
            (path.EndsWith("/respite-episodes", StringComparison.Ordinal) || path.EndsWith("/cancel", StringComparison.Ordinal)) &&
            path.Contains("/respite-episodes", StringComparison.Ordinal)) return "clients.manage";
        if (method == "PUT" && IsEntityPath(path, "/api/phase1/service-users/")) return "clients.manage";
        if (method == "PUT" && path.EndsWith("/person-record", StringComparison.Ordinal) && IsEntityPath(path[..^"/person-record".Length], "/api/phase1/service-users/")) return "clients.manage";
        if (method == "DELETE" && IsEntityPath(path, "/api/phase1/service-users/")) return "clients.manage";
        if (method == "POST" && path.StartsWith("/api/phase1/person-lifecycle/service-users/", StringComparison.Ordinal) && path.EndsWith("/status", StringComparison.Ordinal) && IsEntityPath(path[..^"/status".Length], "/api/phase1/person-lifecycle/service-users/")) return "clients.manage";
        if (method == "POST" && path.StartsWith("/api/phase1/person-lifecycle/service-users/", StringComparison.Ordinal) && path.EndsWith("/discharges", StringComparison.Ordinal) && IsEntityPath(path[..^"/discharges".Length], "/api/phase1/person-lifecycle/service-users/")) return "clients.manage";
        if (method == "POST" && path == "/api/phase1/care-workers") return "workers.manage";
        if (method == "PUT" && IsEntityPath(path, "/api/phase1/care-workers/")) return "workers.manage";
        if (method == "DELETE" && IsEntityPath(path, "/api/phase1/care-workers/")) return "workers.manage";
        if (method == "PUT" && path.EndsWith("/employment", StringComparison.Ordinal) && IsEntityPath(path[..^"/employment".Length], "/api/phase1/workforce-lifecycle/care-workers/")) return "workers.manage";
        if (method == "POST" && path == "/api/phase1/visits") return "visits.schedule";
        if (method == "PUT" && IsEntityPath(path, "/api/phase1/visits/")) return "visits.schedule";
        if (method == "POST" && path == "/api/phase1/visits/recurring") return "visits.schedule";
        if (method == "DELETE" && IsEntityPath(path, "/api/phase1/visits/")) return "visits.schedule";
        if (method == "PATCH" && path.StartsWith("/api/phase1/visits/", StringComparison.Ordinal) && path.EndsWith("/status", StringComparison.Ordinal) && IsEntityPath(path[..^7], "/api/phase1/visits/")) return "visits.schedule";
        return null;
    }

    private static bool IsEntityPath(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParse(path[prefix.Length..], out _);
}
