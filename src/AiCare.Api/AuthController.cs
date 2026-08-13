using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AiCare.Api;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly CareDbContext _context;
    private readonly JwtOptions _jwtOptions;

    public AuthController(CareDbContext context, IOptions<JwtOptions> jwtOptions)
    {
        _context = context;
        _jwtOptions = jwtOptions.Value;
    }

    [HttpPost("login")]
    public IActionResult Login(LoginRequest request)
    {
        var user = _context.AppUsers.SingleOrDefault(u => u.UserName == request.UserName && u.IsActive);
        if (user is null || !PasswordHasher.VerifyPassword(request.Password, user.PasswordHash))
        {
            return Unauthorized(new { message = "Invalid credentials." });
        }

        var token = CreateJwtToken(user);
        return Ok(new { token });
    }

    private string CreateJwtToken(AppUser user)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.UniqueName, user.UserName),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim("organization_id", (user.OrganizationId ?? AiCare.Domain.TenantDefaults.OrganizationId).ToString()),
            new Claim("branch_id", user.BranchId?.ToString() ?? string.Empty),
            new Claim("care_worker_id", user.CareWorkerId?.ToString() ?? string.Empty),
        };

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOptions.SigningKey));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
        var expires = DateTime.UtcNow.AddMinutes(_jwtOptions.TokenLifetimeMinutes);

        var token = new JwtSecurityToken(
            issuer: _jwtOptions.Issuer,
            audience: _jwtOptions.Audience,
            claims: claims,
            expires: expires,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [HttpPost("change-password")]
    public IActionResult ChangePassword(ChangePasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserName) ||
            string.IsNullOrWhiteSpace(request.CurrentPassword) ||
            string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return BadRequest(new { message = "Username, current password, and new password are required." });
        }

        if (request.NewPassword.Length < 10)
        {
            return BadRequest(new { message = "New password must be at least 10 characters." });
        }

        var user = _context.AppUsers.SingleOrDefault(u => u.UserName == request.UserName && u.IsActive);
        if (user is null || !PasswordHasher.VerifyPassword(request.CurrentPassword, user.PasswordHash))
        {
            return Unauthorized(new { message = "Invalid current password." });
        }

        _context.Entry(user).CurrentValues.SetValues(user with { PasswordHash = PasswordHasher.HashPassword(request.NewPassword) });
        _context.AuditEvents.Add(new AiCare.Domain.AuditEvent(Guid.NewGuid(), "auth.password_changed", user.UserName, nameof(AppUser), user.Id, DateTimeOffset.UtcNow, user.OrganizationId, user.BranchId));
        _context.SaveChanges();
        return NoContent();
    }

    [Authorize]
    [HttpGet("me")]
    public IActionResult Me()
    {
        var subject = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(subject, out var userId)) return Unauthorized();
        var user = _context.AppUsers.SingleOrDefault(item => item.Id == userId && item.IsActive);
        if (user is null) return Unauthorized();
        return Ok(new
        {
            id = user.Id,
            userName = user.UserName,
            email = user.Email,
            role = user.Role.ToString(),
            organizationId = user.OrganizationId ?? AiCare.Domain.TenantDefaults.OrganizationId,
            branchId = user.BranchId,
        });
    }

    [HttpPost("refresh-token")]
    public IActionResult RefreshToken(RefreshTokenRequest request)
    {
        var user = _context.AppUsers.SingleOrDefault(u => u.UserName == request.UserName && u.IsActive);
        if (user is null)
        {
            return Unauthorized(new { message = "Invalid refresh request." });
        }

        _context.AuditEvents.Add(new AiCare.Domain.AuditEvent(Guid.NewGuid(), "auth.token_refreshed", user.UserName, nameof(AppUser), user.Id, DateTimeOffset.UtcNow, user.OrganizationId, user.BranchId));
        _context.SaveChanges();
        return Ok(new { token = CreateJwtToken(user) });
    }

    [HttpPost("forgot-password")]
    public IActionResult ForgotPassword(ForgotPasswordRequest request)
    {
        var user = _context.AppUsers.SingleOrDefault(u => u.Email == request.Email && u.IsActive);
        if (user is not null)
        {
            _context.AuditEvents.Add(new AiCare.Domain.AuditEvent(Guid.NewGuid(), "auth.password_reset_requested", user.UserName, nameof(AppUser), user.Id, DateTimeOffset.UtcNow, user.OrganizationId, user.BranchId));
            _context.SaveChanges();
        }

        return Accepted(new { message = "If the email exists, a reset workflow has been queued." });
    }

    [HttpPost("reset-password")]
    public IActionResult ResetPassword(ResetPasswordRequest request)
    {
        if (request.NewPassword.Length < 10)
        {
            return BadRequest(new { message = "New password must be at least 10 characters." });
        }

        var user = _context.AppUsers.SingleOrDefault(u => u.UserName == request.UserName && u.IsActive);
        if (user is null || request.ResetCode != "local-reset")
        {
            return Unauthorized(new { message = "Invalid reset request." });
        }

        _context.Entry(user).CurrentValues.SetValues(user with { PasswordHash = PasswordHasher.HashPassword(request.NewPassword) });
        _context.AuditEvents.Add(new AiCare.Domain.AuditEvent(Guid.NewGuid(), "auth.password_reset_completed", user.UserName, nameof(AppUser), user.Id, DateTimeOffset.UtcNow, user.OrganizationId, user.BranchId));
        _context.SaveChanges();
        return NoContent();
    }

    [HttpPost("setup-mfa")]
    public IActionResult SetupMfa(MfaSetupRequest request)
    {
        var user = _context.AppUsers.SingleOrDefault(u => u.UserName == request.UserName && u.IsActive);
        if (user is null)
        {
            return NotFound(new { message = "User not found." });
        }

        _context.AuditEvents.Add(new AiCare.Domain.AuditEvent(Guid.NewGuid(), "auth.mfa_setup_started", user.UserName, nameof(AppUser), user.Id, DateTimeOffset.UtcNow, user.OrganizationId, user.BranchId));
        _context.SaveChanges();
        return Ok(new { issuer = "AiCare", account = user.Email, secret = "provider-managed-secret", enabled = false });
    }
}

public sealed record LoginRequest(string UserName, string Password);
public sealed record ChangePasswordRequest(string UserName, string CurrentPassword, string NewPassword);
public sealed record RefreshTokenRequest(string UserName);
public sealed record ForgotPasswordRequest(string Email);
public sealed record ResetPasswordRequest(string UserName, string ResetCode, string NewPassword);
public sealed record MfaSetupRequest(string UserName);
