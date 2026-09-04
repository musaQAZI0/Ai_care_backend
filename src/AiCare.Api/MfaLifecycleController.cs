using System.Data;
using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiCare.Api;

[ApiController]
[Authorize]
[Route("api/security/mfa")]
public sealed class MfaLifecycleController(CareDbContext db, IOptions<JwtOptions> jwt, ITenantContext tenant) : ControllerBase
{
    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        var current = Current(); if (current is null) return Unauthorized();
        var state = await State(current.Id, ct);
        var remaining = await Scalar("select count(*) from auth_mfa_recovery_codes where user_id=@user and used_at is null and invalidated_at is null", c => Add(c, "user", current.Id), ct);
        return Ok(new { status = state.Status, enabled = state.Enabled, verifiedAt = state.VerifiedAt, lockedUntil = state.LockedUntil, resetRequired = state.ResetRequired, recoveryCodesRemaining = remaining, policyRequired = await PolicyRequired(current, ct) });
    }

    [HttpPost("enroll")]
    public async Task<IActionResult> Enroll(CancellationToken ct)
    {
        var current = Current(); if (current is null) return Unauthorized();
        var state = await State(current.Id, ct);
        if (state.Enabled && !state.ResetRequired) return Conflict(new { message = "MFA is already active." });
        var secret = MfaSecurity.GenerateSecret();
        var protectedSecret = MfaSecurity.Protect(secret, jwt.Value.SigningKey);
        await Exec("insert into auth_user_security(user_id,mfa_secret,mfa_enabled,mfa_status,mfa_failed_attempts,mfa_locked_until,mfa_reset_required,updated_at) values(@user,@secret,false,'Pending',0,null,false,now()) on conflict(user_id) do update set mfa_secret=@secret,mfa_enabled=false,mfa_status='Pending',mfa_failed_attempts=0,mfa_locked_until=null,mfa_reset_required=false,updated_at=now()", c => { Add(c, "user", current.Id); Add(c, "secret", protectedSecret); }, ct);
        await InvalidateCodes(current.Id, ct); await Event(current, current.Id, "EnrollmentStarted", "Authenticator enrollment started", ct); Audit("security.mfa_enrollment_started", current.Id, current);
        await db.SaveChangesAsync(ct);
        var issuer = Uri.EscapeDataString("AiCare"); var account = Uri.EscapeDataString(current.Email);
        return Ok(new { secret, otpauthUri = $"otpauth://totp/{issuer}:{account}?secret={secret}&issuer={issuer}&digits=6&period=30" });
    }

    [HttpPost("enroll/verify")]
    public async Task<IActionResult> VerifyEnrollment(MfaCodeRequest request, CancellationToken ct)
    {
        var current = Current(); if (current is null) return Unauthorized();
        var state = await State(current.Id, ct);
        if (state.LockedUntil > DateTimeOffset.UtcNow) return StatusCode(423, new { message = "MFA verification is temporarily locked." });
        if (state.Status != "Pending" || string.IsNullOrWhiteSpace(state.Secret)) return Conflict(new { message = "Start enrollment before verification." });
        if (!MfaSecurity.VerifyTotp(MfaSecurity.Unprotect(state.Secret, jwt.Value.SigningKey), request.Code)) { await Fail(current, "Enrollment code rejected", ct); return BadRequest(new { message = "Authenticator code is invalid." }); }
        await Exec("update auth_user_security set mfa_enabled=true,mfa_status='Active',mfa_verified_at=now(),mfa_failed_attempts=0,mfa_locked_until=null,mfa_reset_required=false,updated_at=now() where user_id=@user", c => Add(c, "user", current.Id), ct);
        var codes = await ReplaceCodes(current.Id, ct); await Event(current, current.Id, "Enabled", "Authenticator MFA enabled and recovery codes issued", ct); Audit("security.mfa_enabled", current.Id, current); await db.SaveChangesAsync(ct);
        return Ok(new { recoveryCodes = codes });
    }

    [HttpPost("recovery-codes/regenerate")]
    public async Task<IActionResult> Regenerate(MfaCodeRequest request, CancellationToken ct)
    {
        var current = Current(); if (current is null) return Unauthorized();
        var state = await State(current.Id, ct);
        if (!state.Enabled || state.LockedUntil > DateTimeOffset.UtcNow || !MfaSecurity.VerifyTotp(MfaSecurity.Unprotect(state.Secret, jwt.Value.SigningKey), request.Code)) return Unauthorized(new { message = "A current authenticator code is required." });
        var codes = await ReplaceCodes(current.Id, ct); await Event(current, current.Id, "RecoveryRegenerated", "Recovery codes regenerated; previous codes invalidated", ct); Audit("security.mfa_recovery_regenerated", current.Id, current); await db.SaveChangesAsync(ct); return Ok(new { recoveryCodes = codes });
    }

    [HttpPost("disable")]
    public async Task<IActionResult> Disable(DisableMfaRequest request, CancellationToken ct)
    {
        var current = Current(); if (current is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(request.Reason) || string.IsNullOrWhiteSpace(request.IdentityEvidence) || !PasswordHasher.VerifyPassword(request.CurrentPassword, current.PasswordHash)) return BadRequest(new { message = "Current password, identity evidence and reason are required." });
        var state = await State(current.Id, ct); if (!state.Enabled || !MfaSecurity.VerifyTotp(MfaSecurity.Unprotect(state.Secret, jwt.Value.SigningKey), request.Code)) return Unauthorized(new { message = "A current authenticator code is required." });
        await Exec("update auth_user_security set mfa_secret=null,mfa_enabled=false,mfa_status='Disabled',mfa_disabled_at=now(),mfa_failed_attempts=0,mfa_locked_until=null,updated_at=now() where user_id=@user", c => Add(c, "user", current.Id), ct); await InvalidateCodes(current.Id, ct); await RevokeSessions(current.Id, ct); await Event(current, current.Id, "Disabled", $"Self-service disable: {request.Reason}; evidence: {request.IdentityEvidence}", ct); Audit("security.mfa_disabled", current.Id, current); await db.SaveChangesAsync(ct); return NoContent();
    }

    [HttpPut("policy")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> Policy(MfaPolicyRequest request, CancellationToken ct)
    {
        var current = Current(); if (current is null) return Unauthorized();
        var allowed = Enum.GetNames<UserRole>(); if (request.RequiredRoles.Count == 0 || request.RequiredRoles.Any(role => !allowed.Contains(role)) || request.GracePeriodDays is < 0 or > 90) return BadRequest(new { message = "Valid required roles and a 0-90 day grace period are required." });
        await Exec("insert into auth_mfa_policies(id,organization_id,branch_id,required_roles,grace_period_days,enabled,effective_at,updated_by) values(@id,@organization,@policyBranch,@roles,@grace,@enabled,@effective,@actor) on conflict(organization_id,coalesce(branch_id,'00000000-0000-0000-0000-000000000000'::uuid)) do update set required_roles=@roles,grace_period_days=@grace,enabled=@enabled,effective_at=@effective,updated_by=@actor,updated_at=now()", c => { Add(c, "id", Guid.NewGuid()); Add(c, "policyBranch", request.OrganizationWide ? null : current.BranchId); Add(c, "roles", request.RequiredRoles.ToArray()); Add(c, "grace", request.GracePeriodDays); Add(c, "enabled", request.Enabled); Add(c, "effective", request.EffectiveAt); }, ct);
        await Event(current, current.Id, "PolicyUpdated", $"Required roles: {string.Join(',', request.RequiredRoles)}", ct); Audit("security.mfa_policy_updated", current.Id, current); await db.SaveChangesAsync(ct); return Ok();
    }

    [HttpPost("users/{userId:guid}/reset")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> AdminReset(Guid userId, AdminResetMfaRequest request, CancellationToken ct)
    {
        var current = Current(); if (current is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(request.IdentityEvidence) || string.IsNullOrWhiteSpace(request.Reason) || string.IsNullOrWhiteSpace(request.AdminMfaCode)) return BadRequest(new { message = "Identity evidence, reason and administrator MFA verification are required." });
        var adminState = await State(current.Id, ct); if (!adminState.Enabled || !MfaSecurity.VerifyTotp(MfaSecurity.Unprotect(adminState.Secret, jwt.Value.SigningKey), request.AdminMfaCode)) return Unauthorized(new { message = "Administrator MFA step-up failed." });
        var target = await db.AppUsers.SingleOrDefaultAsync(x => x.Id == userId && x.OrganizationId == current.OrganizationId && x.IsActive, ct); if (target is null || (current.BranchId is not null && target.BranchId != current.BranchId)) return NotFound();
        await Exec("insert into auth_user_security(user_id,mfa_enabled,mfa_status,mfa_reset_required,updated_at) values(@target,false,'ResetRequired',true,now()) on conflict(user_id) do update set mfa_secret=null,mfa_enabled=false,mfa_status='ResetRequired',mfa_reset_required=true,mfa_failed_attempts=0,mfa_locked_until=null,updated_at=now()", c => Add(c, "target", target.Id), ct); await InvalidateCodes(target.Id, ct); await RevokeSessions(target.Id, ct); await Event(current, target.Id, "AdminReset", $"Reason: {request.Reason}; identity evidence: {request.IdentityEvidence}", ct); Audit("security.mfa_admin_reset", target.Id, current); await db.SaveChangesAsync(ct); return NoContent();
    }

    private async Task<List<string>> ReplaceCodes(Guid userId, CancellationToken ct) { await InvalidateCodes(userId, ct); var codes = Enumerable.Range(0, 10).Select(_ => MfaSecurity.GenerateRecoveryCode()).ToList(); foreach (var code in codes) await Exec("insert into auth_mfa_recovery_codes(id,user_id,code_hash) values(@id,@user,@hash)", c => { Add(c, "id", Guid.NewGuid()); Add(c, "user", userId); Add(c, "hash", MfaSecurity.HashRecoveryCode(code, userId, jwt.Value.SigningKey)); }, ct); return codes; }
    private Task InvalidateCodes(Guid userId, CancellationToken ct) => Exec("update auth_mfa_recovery_codes set invalidated_at=now() where user_id=@user and used_at is null and invalidated_at is null", c => Add(c, "user", userId), ct);
    private Task RevokeSessions(Guid userId, CancellationToken ct) => Exec("update auth_refresh_tokens set revoked_at=now() where user_id=@user and revoked_at is null;update auth_sessions set revoked_at=now(),revoked_by=@actor,revocation_reason='MFA security state changed' where user_id=@user and revoked_at is null", c => { Add(c, "user", userId); Add(c, "actor", User.Identity?.Name ?? "system"); }, ct);
    private async Task Fail(AppUser current, string detail, CancellationToken ct) { await Exec("update auth_user_security set mfa_failed_attempts=mfa_failed_attempts+1,mfa_locked_until=case when mfa_failed_attempts+1>=5 then now()+interval '15 minutes' else mfa_locked_until end,updated_at=now() where user_id=@user", c => Add(c, "user", current.Id), ct); await Event(current, current.Id, "ChallengeFailed", detail, ct); Audit("security.mfa_challenge_failed", current.Id, current); await db.SaveChangesAsync(ct); }
    private async Task<bool> PolicyRequired(AppUser current, CancellationToken ct) => await Exists("select exists(select 1 from auth_mfa_policies where organization_id=@organization and enabled=true and effective_at<=now() and @role=any(required_roles) and (branch_id is null or branch_id=@branch))", c => Add(c, "role", current.Role.ToString()), ct);
    private async Task<MfaState> State(Guid userId, CancellationToken ct) { var rows = await Query("select coalesce(mfa_status,'NotEnrolled'),mfa_enabled,mfa_secret,mfa_verified_at,mfa_locked_until,mfa_reset_required from auth_user_security where user_id=@user", c => Add(c, "user", userId), r => new MfaState(r.GetString(0), r.GetBoolean(1), r.IsDBNull(2) ? "" : r.GetString(2), Date(r, 3), Date(r, 4), r.GetBoolean(5)), ct); return rows.FirstOrDefault() ?? new("NotEnrolled", false, "", null, null, false); }
    private Task Event(AppUser actor, Guid userId, string type, string detail, CancellationToken ct) => Exec("insert into auth_mfa_events(id,user_id,organization_id,branch_id,event_type,detail,actor) values(@id,@user,@organization,@eventBranch,@type,@detail,@actor)", c => { Add(c, "id", Guid.NewGuid()); Add(c, "user", userId); Add(c, "eventBranch", actor.BranchId); Add(c, "type", type); Add(c, "detail", detail); }, ct);
    private void Audit(string action, Guid userId, AppUser actor) => db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), action, actor.UserName, nameof(AppUser), userId, DateTimeOffset.UtcNow, actor.OrganizationId, actor.BranchId));
    private AppUser? Current() { var subject = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier); return Guid.TryParse(subject, out var id) ? db.AppUsers.SingleOrDefault(x => x.Id == id && x.IsActive) : null; }
    private static DateTimeOffset? Date(DbDataReader reader, int index) => reader.IsDBNull(index) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(index), DateTimeKind.Utc));
    private async Task<bool> Exists(string sql, Action<DbCommand> bind, CancellationToken ct) { await using var command = await Command(sql, ct); bind(command); return Convert.ToBoolean(await command.ExecuteScalarAsync(ct)); }
    private async Task<long> Scalar(string sql, Action<DbCommand> bind, CancellationToken ct) { await using var command = await Command(sql, ct); bind(command); return Convert.ToInt64(await command.ExecuteScalarAsync(ct)); }
    private async Task<int> Exec(string sql, Action<DbCommand> bind, CancellationToken ct) { await using var command = await Command(sql, ct); bind(command); return await command.ExecuteNonQueryAsync(ct); }
    private async Task<List<T>> Query<T>(string sql, Action<DbCommand> bind, Func<DbDataReader, T> map, CancellationToken ct) { await using var command = await Command(sql, ct); bind(command); await using var reader = await command.ExecuteReaderAsync(ct); var result = new List<T>(); while (await reader.ReadAsync(ct)) result.Add(map(reader)); return result; }
    private async Task<DbCommand> Command(string sql, CancellationToken ct) { var connection = db.Database.GetDbConnection(); if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct); var command = connection.CreateCommand(); command.CommandText = sql; Add(command, "organization", tenant.OrganizationId); Add(command, "branch", tenant.BranchId ?? TenantDefaults.BranchId); Add(command, "actor", User.Identity?.Name ?? "unknown"); return command; }
    private static void Add(DbCommand command, string name, object? value) { if (command.Parameters.Contains(name)) return; var p = command.CreateParameter(); p.ParameterName = name; p.Value = value ?? DBNull.Value; command.Parameters.Add(p); }
    private sealed record MfaState(string Status, bool Enabled, string Secret, DateTimeOffset? VerifiedAt, DateTimeOffset? LockedUntil, bool ResetRequired);
}

public sealed record MfaCodeRequest(string Code);
public sealed record DisableMfaRequest(string CurrentPassword, string Code, string IdentityEvidence, string Reason);
public sealed record MfaPolicyRequest(IReadOnlyCollection<string> RequiredRoles, int GracePeriodDays, bool Enabled, DateTimeOffset EffectiveAt, bool OrganizationWide);
public sealed record AdminResetMfaRequest(string IdentityEvidence, string Reason, string AdminMfaCode);
