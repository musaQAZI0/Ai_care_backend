using System.Data;
using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AiCare.Api;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly CareDbContext _context;
    private readonly JwtOptions _jwtOptions;
    private readonly IWebHostEnvironment _environment;

    public AuthController(CareDbContext context, IOptions<JwtOptions> jwtOptions, IWebHostEnvironment environment)
    {
        _context = context;
        _jwtOptions = jwtOptions.Value;
        _environment = environment;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var user = await _context.AppUsers.SingleOrDefaultAsync(u => u.UserName == request.UserName && u.IsActive, cancellationToken);
        if (user is not null && await IsLockedOut(user.Id, cancellationToken))
            return StatusCode(423, new { message = "Account temporarily locked after repeated failed sign-in attempts." });

        if (user is null || !PasswordHasher.VerifyPassword(request.Password, user.PasswordHash))
        {
            if (user is not null) await RecordFailedLogin(user.Id, cancellationToken);
            return Unauthorized(new { message = "Invalid credentials." });
        }

        var mfa = await GetMfaState(user.Id, cancellationToken);
        if (mfa.Enabled && !VerifyTotp(mfa.Secret, request.MfaCode))
            return Unauthorized(new { message = "A valid authenticator code is required.", mfaRequired = true });

        await ClearFailedLogins(user.Id, cancellationToken);
        var refreshToken = CreateOpaqueToken();
        await StoreRefreshToken(user.Id, refreshToken, null, cancellationToken);
        var token = CreateJwtToken(user);
        return Ok(new { token, refreshToken, expiresInMinutes = _jwtOptions.TokenLifetimeMinutes });
    }

    private string CreateJwtToken(AppUser user)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.UniqueName, user.UserName),
            new(ClaimTypes.Role, user.Role.ToString()),
            new(ClaimTypes.Email, user.Email),
            new("organization_id", (user.OrganizationId ?? TenantDefaults.OrganizationId).ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        if (user.BranchId is not null) claims.Add(new Claim("branch_id", user.BranchId.Value.ToString()));
        if (user.CareWorkerId is not null) claims.Add(new Claim("care_worker_id", user.CareWorkerId.Value.ToString()));
        if (user.FamilyMemberId is not null) claims.Add(new Claim("family_member_id", user.FamilyMemberId.Value.ToString()));
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOptions.SigningKey));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(_jwtOptions.Issuer, _jwtOptions.Audience, claims, expires: DateTime.UtcNow.AddMinutes(_jwtOptions.TokenLifetimeMinutes), signingCredentials: credentials));
    }

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword)) return BadRequest(new { message = "Username, current password, and new password are required." });
        if (!StrongPassword(request.NewPassword)) return BadRequest(new { message = "New password must be at least 12 characters and contain upper, lower, number and symbol characters." });
        var user = await _context.AppUsers.SingleOrDefaultAsync(u => u.UserName == request.UserName && u.IsActive, cancellationToken);
        if (user is null || !PasswordHasher.VerifyPassword(request.CurrentPassword, user.PasswordHash)) return Unauthorized(new { message = "Invalid current password." });
        _context.Entry(user).CurrentValues.SetValues(user with { PasswordHash = PasswordHasher.HashPassword(request.NewPassword) });
        await RevokeAllRefreshTokens(user.Id, cancellationToken);
        _context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "auth.password_changed", user.UserName, nameof(AppUser), user.Id, DateTimeOffset.UtcNow, user.OrganizationId, user.BranchId));
        await _context.SaveChangesAsync(cancellationToken);
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
        return Ok(new { id = user.Id, userName = user.UserName, email = user.Email, role = user.Role.ToString(), organizationId = user.OrganizationId ?? TenantDefaults.OrganizationId, branchId = user.BranchId, familyMemberId = user.FamilyMemberId, careWorkerId = user.CareWorkerId });
    }

    [HttpPost("refresh-token")]
    public async Task<IActionResult> RefreshToken(RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken)) return Unauthorized(new { message = "Invalid refresh request." });
        var tokenHash = HashToken(request.RefreshToken);
        var record = await FindRefreshToken(tokenHash, cancellationToken);
        if (record is null || record.ExpiresAt <= DateTimeOffset.UtcNow || record.RevokedAt is not null) return Unauthorized(new { message = "Refresh token is invalid or expired." });
        var user = await _context.AppUsers.SingleOrDefaultAsync(u => u.Id == record.UserId && u.IsActive, cancellationToken);
        if (user is null) return Unauthorized(new { message = "Invalid refresh request." });
        var replacement = CreateOpaqueToken(); var replacementHash = HashToken(replacement);
        await RevokeRefreshToken(record.Id, replacementHash, cancellationToken);
        await StoreRefreshToken(user.Id, replacement, null, cancellationToken);
        _context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "auth.token_rotated", user.UserName, nameof(AppUser), user.Id, DateTimeOffset.UtcNow, user.OrganizationId, user.BranchId));
        await _context.SaveChangesAsync(cancellationToken);
        return Ok(new { token = CreateJwtToken(user), refreshToken = replacement, expiresInMinutes = _jwtOptions.TokenLifetimeMinutes });
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(LogoutRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.RefreshToken)) await RevokeRefreshTokenByHash(HashToken(request.RefreshToken), cancellationToken);
        return NoContent();
    }

    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        var user = await _context.AppUsers.SingleOrDefaultAsync(u => u.Email == request.Email && u.IsActive, cancellationToken);
        string? developmentToken = null;
        if (user is not null)
        {
            var resetToken = CreateOpaqueToken();
            if (await StorePasswordResetToken(user.Id, resetToken, cancellationToken) && (_environment.IsDevelopment() || _environment.IsEnvironment("Testing"))) developmentToken = resetToken;
            _context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "auth.password_reset_requested", user.UserName, nameof(AppUser), user.Id, DateTimeOffset.UtcNow, user.OrganizationId, user.BranchId));
            await _context.SaveChangesAsync(cancellationToken);
        }
        return Accepted(new { message = "If the email exists, a password reset request has been created.", developmentResetToken = developmentToken });
    }

    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        if (!StrongPassword(request.NewPassword)) return BadRequest(new { message = "New password must be at least 12 characters and contain upper, lower, number and symbol characters." });
        var reset = await FindPasswordResetToken(HashToken(request.ResetToken), cancellationToken);
        if (reset is null || reset.ExpiresAt <= DateTimeOffset.UtcNow || reset.UsedAt is not null) return Unauthorized(new { message = "Reset token is invalid or expired." });
        var user = await _context.AppUsers.SingleOrDefaultAsync(u => u.Id == reset.UserId && u.IsActive, cancellationToken);
        if (user is null) return Unauthorized(new { message = "Invalid reset request." });
        _context.Entry(user).CurrentValues.SetValues(user with { PasswordHash = PasswordHasher.HashPassword(request.NewPassword) });
        await MarkPasswordResetUsed(reset.Id, cancellationToken); await RevokeAllRefreshTokens(user.Id, cancellationToken); await ClearFailedLogins(user.Id, cancellationToken);
        _context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "auth.password_reset_completed", user.UserName, nameof(AppUser), user.Id, DateTimeOffset.UtcNow, user.OrganizationId, user.BranchId));
        await _context.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [Authorize]
    [HttpPost("setup-mfa")]
    public async Task<IActionResult> SetupMfa(CancellationToken cancellationToken)
    {
        var user = CurrentUser(); if (user is null) return Unauthorized();
        var secret = GenerateBase32Secret();
        if (!await SaveMfaSecret(user.Id, secret, false, cancellationToken)) return StatusCode(503, new { message = "MFA storage is not available until the security migration is applied." });
        _context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "auth.mfa_setup_started", user.UserName, nameof(AppUser), user.Id, DateTimeOffset.UtcNow, user.OrganizationId, user.BranchId)); await _context.SaveChangesAsync(cancellationToken);
        var issuer = Uri.EscapeDataString("AiCare"); var account = Uri.EscapeDataString(user.Email);
        return Ok(new { secret, otpauthUri = $"otpauth://totp/{issuer}:{account}?secret={secret}&issuer={issuer}&digits=6&period=30" });
    }

    [Authorize]
    [HttpPost("verify-mfa")]
    public async Task<IActionResult> VerifyMfa(MfaVerifyRequest request, CancellationToken cancellationToken)
    {
        var user = CurrentUser(); if (user is null) return Unauthorized();
        var state = await GetMfaState(user.Id, cancellationToken); if (string.IsNullOrWhiteSpace(state.Secret) || !VerifyTotp(state.Secret, request.Code)) return BadRequest(new { message = "Authenticator code is invalid." });
        await SaveMfaSecret(user.Id, state.Secret, true, cancellationToken);
        _context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "auth.mfa_enabled", user.UserName, nameof(AppUser), user.Id, DateTimeOffset.UtcNow, user.OrganizationId, user.BranchId)); await _context.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private AppUser? CurrentUser() { var subject = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier); return Guid.TryParse(subject,out var id)?_context.AppUsers.SingleOrDefault(x=>x.Id==id&&x.IsActive):null; }
    private static bool StrongPassword(string value) => value.Length >= 12 && value.Any(char.IsUpper) && value.Any(char.IsLower) && value.Any(char.IsDigit) && value.Any(ch => !char.IsLetterOrDigit(ch));
    private static string CreateOpaqueToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).Replace("+","-").Replace("/","_").TrimEnd('=');
    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private async Task<bool> IsLockedOut(Guid userId,CancellationToken ct){try{var row=await QuerySecurity(userId,ct);return row.LockoutUntil>DateTimeOffset.UtcNow;}catch{return false;}}
    private async Task RecordFailedLogin(Guid userId,CancellationToken ct){try{await Execute("insert into auth_user_security(user_id,failed_attempts,updated_at) values(@id,1,now()) on conflict(user_id) do update set failed_attempts=auth_user_security.failed_attempts+1,lockout_until=case when auth_user_security.failed_attempts+1>=5 then now()+interval '15 minutes' else auth_user_security.lockout_until end,updated_at=now()",c=>Add(c,"id",userId),ct);}catch{}}
    private async Task ClearFailedLogins(Guid userId,CancellationToken ct){try{await Execute("insert into auth_user_security(user_id,failed_attempts,lockout_until,updated_at) values(@id,0,null,now()) on conflict(user_id) do update set failed_attempts=0,lockout_until=null,updated_at=now()",c=>Add(c,"id",userId),ct);}catch{}}
    private async Task<(bool Enabled,string Secret)> GetMfaState(Guid userId,CancellationToken ct){try{var x=await QuerySecurity(userId,ct);return(x.MfaEnabled,x.MfaSecret??"");}catch{return(false,"");}}
    private async Task<(int FailedAttempts,DateTimeOffset? LockoutUntil,string? MfaSecret,bool MfaEnabled)> QuerySecurity(Guid id,CancellationToken ct){var connection=_context.Database.GetDbConnection();var opened=connection.State!=ConnectionState.Open;if(opened)await connection.OpenAsync(ct);try{await using var cmd=connection.CreateCommand();cmd.CommandText="select failed_attempts,lockout_until,mfa_secret,mfa_enabled from auth_user_security where user_id=@id";Add(cmd,"id",id);await using var r=await cmd.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return(0,null,null,false);return(r.GetInt32(0),r.IsDBNull(1)?null:new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(1),DateTimeKind.Utc)),r.IsDBNull(2)?null:r.GetString(2),r.GetBoolean(3));}finally{if(opened)await connection.CloseAsync();}}
    private async Task<bool> SaveMfaSecret(Guid id,string secret,bool enabled,CancellationToken ct){try{await Execute("insert into auth_user_security(user_id,mfa_secret,mfa_enabled,updated_at) values(@id,@secret,@enabled,now()) on conflict(user_id) do update set mfa_secret=@secret,mfa_enabled=@enabled,updated_at=now()",c=>{Add(c,"id",id);Add(c,"secret",secret);Add(c,"enabled",enabled);},ct);return true;}catch{return false;}}

    private async Task StoreRefreshToken(Guid userId,string token,string? ip,CancellationToken ct){try{await Execute("insert into auth_refresh_tokens(id,user_id,token_hash,expires_at,created_at,created_ip) values(@id,@user,@hash,@expires,now(),@ip)",c=>{Add(c,"id",Guid.NewGuid());Add(c,"user",userId);Add(c,"hash",HashToken(token));Add(c,"expires",DateTime.UtcNow.AddDays(7));Add(c,"ip",ip??HttpContext.Connection.RemoteIpAddress?.ToString()??"");},ct);}catch{}}
    private async Task<RefreshTokenRow?> FindRefreshToken(string hash,CancellationToken ct){try{var connection=_context.Database.GetDbConnection();var opened=connection.State!=ConnectionState.Open;if(opened)await connection.OpenAsync(ct);try{await using var cmd=connection.CreateCommand();cmd.CommandText="select id,user_id,expires_at,revoked_at from auth_refresh_tokens where token_hash=@hash";Add(cmd,"hash",hash);await using var r=await cmd.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return null;return new RefreshTokenRow(r.GetGuid(0),r.GetGuid(1),new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(2),DateTimeKind.Utc)),r.IsDBNull(3)?null:new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(3),DateTimeKind.Utc)));}finally{if(opened)await connection.CloseAsync();}}catch{return null;}}
    private Task RevokeRefreshToken(Guid id,string replacement,CancellationToken ct)=>Execute("update auth_refresh_tokens set revoked_at=now(),replaced_by_token_hash=@replacement where id=@id",c=>{Add(c,"replacement",replacement);Add(c,"id",id);},ct);
    private async Task RevokeRefreshTokenByHash(string hash,CancellationToken ct){try{await Execute("update auth_refresh_tokens set revoked_at=now() where token_hash=@hash and revoked_at is null",c=>Add(c,"hash",hash),ct);}catch{}}
    private async Task RevokeAllRefreshTokens(Guid userId,CancellationToken ct){try{await Execute("update auth_refresh_tokens set revoked_at=now() where user_id=@user and revoked_at is null",c=>Add(c,"user",userId),ct);}catch{}}

    private async Task<bool> StorePasswordResetToken(Guid userId,string token,CancellationToken ct){try{await Execute("update password_reset_tokens set used_at=now() where user_id=@user and used_at is null",c=>Add(c,"user",userId),ct);await Execute("insert into password_reset_tokens(id,user_id,token_hash,expires_at,created_at) values(@id,@user,@hash,@expires,now())",c=>{Add(c,"id",Guid.NewGuid());Add(c,"user",userId);Add(c,"hash",HashToken(token));Add(c,"expires",DateTime.UtcNow.AddMinutes(30));},ct);return true;}catch{return false;}}
    private async Task<PasswordResetRow?> FindPasswordResetToken(string hash,CancellationToken ct){try{var connection=_context.Database.GetDbConnection();var opened=connection.State!=ConnectionState.Open;if(opened)await connection.OpenAsync(ct);try{await using var cmd=connection.CreateCommand();cmd.CommandText="select id,user_id,expires_at,used_at from password_reset_tokens where token_hash=@hash";Add(cmd,"hash",hash);await using var r=await cmd.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))return null;return new PasswordResetRow(r.GetGuid(0),r.GetGuid(1),new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(2),DateTimeKind.Utc)),r.IsDBNull(3)?null:new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(3),DateTimeKind.Utc)));}finally{if(opened)await connection.CloseAsync();}}catch{return null;}}
    private Task MarkPasswordResetUsed(Guid id,CancellationToken ct)=>Execute("update password_reset_tokens set used_at=now() where id=@id",c=>Add(c,"id",id),ct);

    private async Task Execute(string sql,Action<DbCommand> bind,CancellationToken ct){var connection=_context.Database.GetDbConnection();var opened=connection.State!=ConnectionState.Open;if(opened)await connection.OpenAsync(ct);try{await using var cmd=connection.CreateCommand();cmd.CommandText=sql;bind(cmd);await cmd.ExecuteNonQueryAsync(ct);}finally{if(opened)await connection.CloseAsync();}}
    private static void Add(DbCommand command,string name,object? value){var p=command.CreateParameter();p.ParameterName=name;p.Value=value??DBNull.Value;command.Parameters.Add(p);}

    private static string GenerateBase32Secret(){const string alphabet="ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";var bytes=RandomNumberGenerator.GetBytes(20);var output=new StringBuilder();int buffer=bytes[0],next=1,bitsLeft=8;while(bitsLeft>0||next<bytes.Length){if(bitsLeft<5){if(next<bytes.Length){buffer=(buffer<<8)|bytes[next++];bitsLeft+=8;}else{buffer<<=5-bitsLeft;bitsLeft=5;}}var index=(buffer>>(bitsLeft-5))&31;bitsLeft-=5;output.Append(alphabet[index]);}return output.ToString();}
    private static byte[] DecodeBase32(string value){const string alphabet="ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";var output=new List<byte>();int buffer=0,bits=0;foreach(var ch in value.ToUpperInvariant()){var v=alphabet.IndexOf(ch);if(v<0)continue;buffer=(buffer<<5)|v;bits+=5;if(bits>=8){output.Add((byte)(buffer>>(bits-8)));bits-=8;buffer&=(1<<bits)-1;}}return output.ToArray();}
    private static bool VerifyTotp(string secret,string? code){if(string.IsNullOrWhiteSpace(secret)||string.IsNullOrWhiteSpace(code)||code.Length!=6)return false;var key=DecodeBase32(secret);var counter=DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30;for(long offset=-1;offset<=1;offset++){var bytes=BitConverter.GetBytes(counter+offset);if(BitConverter.IsLittleEndian)Array.Reverse(bytes);using var hmac=new HMACSHA1(key);var hash=hmac.ComputeHash(bytes);var index=hash[^1]&0x0F;var binary=((hash[index]&0x7f)<<24)|((hash[index+1]&0xff)<<16)|((hash[index+2]&0xff)<<8)|(hash[index+3]&0xff);var expected=(binary%1_000_000).ToString("D6");if(CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected),Encoding.ASCII.GetBytes(code)))return true;}return false;}

    private sealed record RefreshTokenRow(Guid Id,Guid UserId,DateTimeOffset ExpiresAt,DateTimeOffset? RevokedAt);
    private sealed record PasswordResetRow(Guid Id,Guid UserId,DateTimeOffset ExpiresAt,DateTimeOffset? UsedAt);
}

public sealed record LoginRequest(string UserName,string Password,string? MfaCode=null);
public sealed record ChangePasswordRequest(string UserName,string CurrentPassword,string NewPassword);
public sealed record RefreshTokenRequest(string RefreshToken);
public sealed record LogoutRequest(string? RefreshToken);
public sealed record ForgotPasswordRequest(string Email);
public sealed record ResetPasswordRequest(string ResetToken,string NewPassword);
public sealed record MfaVerifyRequest(string Code);
