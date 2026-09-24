using System.Data;
using System.Data.Common;
using System.Security.Claims;
using AiCare.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Infrastructure;

public sealed class SensitiveOperationStepUpMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, CareDbContext db)
    {
        var rule = Rule.For(context.Request);
        if (rule is null || context.User.Identity?.IsAuthenticated != true) { await next(context); return; }
        var subject = context.User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(subject, out var userId) || !Guid.TryParse(context.User.FindFirstValue("sid"), out var sessionId)) { await Deny(context, rule); return; }
        var organizationId = Guid.TryParse(context.User.FindFirstValue("organization_id"), out var organization) ? organization : TenantDefaults.OrganizationId;
        await using var grantCommand = await Command(db, "update auth_step_up_grants set consumed_at=now() where id=(select id from auth_step_up_grants where user_id=@user and session_id=@session and organization_id=@organization and purpose=@purpose and consumed_at is null and revoked_at is null and expires_at>now() and (operation_scope='*' or operation_scope=@scope) order by expires_at desc limit 1 for update skip locked) returning id", context.RequestAborted);
        Add(grantCommand, "user", userId); Add(grantCommand, "session", sessionId); Add(grantCommand, "organization", organizationId); Add(grantCommand, "purpose", rule.Purpose); Add(grantCommand, "scope", rule.Scope);
        var value = await grantCommand.ExecuteScalarAsync(context.RequestAborted);
        var grantId = value is Guid id ? id : (Guid?)null;
        await using var eventCommand = await Command(db, "insert into auth_step_up_events(id,grant_id,user_id,session_id,organization_id,branch_id,event_type,purpose,operation_scope,detail,actor) values(@id,@grant,@user,@session,@organization,@branch,@type,@purpose,@scope,@detail,@actor)", context.RequestAborted);
        Add(eventCommand, "id", Guid.NewGuid()); Add(eventCommand, "grant", grantId); Add(eventCommand, "user", userId); Add(eventCommand, "session", sessionId); Add(eventCommand, "organization", organizationId); Add(eventCommand, "branch", Guid.TryParse(context.User.FindFirstValue("branch_id"), out var branch) ? branch : null); Add(eventCommand, "type", grantId is null ? "Required" : "Used"); Add(eventCommand, "purpose", rule.Purpose); Add(eventCommand, "scope", rule.Scope); Add(eventCommand, "detail", context.Request.Path.Value ?? ""); Add(eventCommand, "actor", context.User.Identity?.Name ?? userId.ToString());
        await eventCommand.ExecuteNonQueryAsync(context.RequestAborted);
        if (grantId is null) { await Deny(context, rule); return; }
        await next(context);
    }

    private static async Task Deny(HttpContext context, Rule rule)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { message = "Recent MFA verification is required for this sensitive operation.", stepUpRequired = true, purpose = rule.Purpose, operationScope = rule.Scope }, context.RequestAborted);
    }

    private static async Task<DbCommand> Command(CareDbContext db, string sql, CancellationToken token)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(token);
        var command = connection.CreateCommand(); command.CommandText = sql; return command;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; command.Parameters.Add(parameter);
    }
}

public sealed record Rule(string Purpose, string Scope)
{
    public static Rule? For(HttpRequest request)
    {
        var method = request.Method; var path = (request.Path.Value ?? "").TrimEnd('/').ToLowerInvariant();
        bool Has(string value) => path.Contains(value); bool Ends(string value) => path.EndsWith(value);
        if (method == "POST" && (path is "/api/phase1/reports/generate" or "/api/phase1/reports/builder" || Has("/report-runs"))) return new("export", "report.generate");
        if (method == "GET" && ((Has("report") && (Ends(".pdf") || Ends(".csv"))) || (Has("payroll-runs") && Ends("/export")))) return new("export", "report.export");
        if (method == "POST" && Has("/payroll-runs/") && Ends("/approve")) return new("payroll", "payroll.approve");
        if (method == "POST" && path == "/api/phase1/admin/users") return new("admin", "admin.user.create");
        if (method == "PATCH" && Has("/admin/users/") && Ends("/role")) return new("admin", "admin.role.change");
        if (method == "POST" && Has("/privacy-rights/requests/") && Ends("/release")) return new("privacy", "privacy.pack.release");
        if (method == "POST" && Has("/privacy-rights/legal-holds/") && Ends("/release")) return new("privacy", "privacy.legal-hold.release");
        if (method == "POST" && Has("/privacy-rights/restrictions/") && Ends("/lift")) return new("privacy", "privacy.restriction.lift");
        if (method == "PUT" && Has("/medication-safety/medications/") && Ends("/profile")) return new("medication", "medication.profile.update");
        if (method == "POST" && path == "/api/phase1/medications") return new("medication", "medication.create");
        if (method == "PUT" && Has("/medications/")) return new("medication", "medication.update");
        if (method == "POST" && path == "/api/phase1/mar") return new("medication", "emar.schedule");
        if (method == "POST" && Has("/emar-safety/mar/") && Ends("/record")) return new("medication", "emar.administration.record");
        if (method == "POST" && Has("/medication-safety/mar/") && Ends("/events")) return new("medication", "emar.safety-event.record");
        if (method == "POST" && Has("/emar-safety/medications/") && Ends("/stock")) return new("medication", "emar.stock.record");
        if (method == "POST" && Has("/emar-safety/mar/") && Ends("/corrections")) return new("medication", "emar.correction.record");
        return null;
    }
}

file static class JwtRegisteredClaimNames { public const string Sub = "sub"; }
