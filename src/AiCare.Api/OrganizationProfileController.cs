using System.Data;
using System.Data.Common;
using System.Net.Mail;
using System.Security.Cryptography;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AiCare.Api;

[ApiController, Authorize(Policy = "Phase1User", Roles = "Administrator,CareManager")]
[Route("api/phase1/admin/company-profile")]
public sealed class OrganizationProfileController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken token)
    {
        var organization = await db.Organizations.AsNoTracking().SingleAsync(item => item.Id == tenant.OrganizationId, token);
        CompanyProfileRow? profile = null;
        await using (var command = await Command("""
            select legal_name,display_name,address,business_phone,contact_email,logo_content_type,logo_bytes,updated_by,updated_at
            from organization_profiles where organization_id=@organization
            """, token))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            if (await reader.ReadAsync(token))
                profile = new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : (byte[])reader[6], reader.GetString(7), reader.GetFieldValue<DateTimeOffset>(8));
        }

        var branches = await db.Branches.AsNoTracking()
            .Where(item => item.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || item.Id == tenant.BranchId))
            .OrderBy(item => item.Name)
            .Select(item => new { item.Id, item.Name, item.Region, item.Status })
            .ToListAsync(token);
        return Ok(new
        {
            legalName = profile?.LegalName ?? organization.Name,
            displayName = profile?.DisplayName ?? organization.Name,
            address = profile?.Address ?? "",
            businessPhone = profile?.BusinessPhone ?? "",
            contactEmail = profile?.ContactEmail ?? "",
            logoContentType = profile?.LogoContentType,
            logoBase64 = profile?.LogoBytes is null ? null : Convert.ToBase64String(profile.LogoBytes),
            updatedBy = profile?.UpdatedBy,
            updatedAt = profile?.UpdatedAt,
            branches
        });
    }

    [HttpPut, Authorize(Roles = "Administrator")]
    public async Task<IActionResult> Save(CompanyProfileRequest request, CancellationToken token)
    {
        if (!tenant.IsOrganizationWide) return Forbid();
        var legalName = Clean(request.LegalName, 200);
        var displayName = Clean(request.DisplayName, 200);
        var address = CleanOptional(request.Address, 1000);
        var phone = CleanOptional(request.BusinessPhone, 80);
        var email = CleanOptional(request.ContactEmail, 254);
        if (legalName is null || displayName is null || address is null || phone is null || email is null || !ValidEmail(email))
            return BadRequest(new { message = "Enter valid legal/display names, address, business phone and contact email." });

        byte[]? replacementLogo = null;
        string? replacementType = null;
        if (!string.IsNullOrWhiteSpace(request.LogoBase64) || !string.IsNullOrWhiteSpace(request.LogoContentType))
        {
            if (request.RemoveLogo || request.LogoContentType is not ("image/png" or "image/jpeg") || string.IsNullOrWhiteSpace(request.LogoBase64))
                return BadRequest(new { message = "Provide one PNG or JPEG logo, or remove the existing logo." });
            try { replacementLogo = Convert.FromBase64String(request.LogoBase64); }
            catch (FormatException) { return BadRequest(new { message = "The logo is not valid base64 data." }); }
            if (replacementLogo.Length is 0 or > 1_048_576) return BadRequest(new { message = "The logo must be no larger than 1 MB." });
            replacementType = request.LogoContentType;
        }

        return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await using var existing = await Command("""
                select logo_content_type,logo_bytes,logo_sha256 from organization_profiles
                where organization_id=@organization for update
                """, token);
            await using var reader = await existing.ExecuteReaderAsync(token);
            string? logoType = null;
            byte[]? logoBytes = null;
            string? logoHash = null;
            if (await reader.ReadAsync(token))
            {
                logoType = reader.IsDBNull(0) ? null : reader.GetString(0);
                logoBytes = reader.IsDBNull(1) ? null : (byte[])reader[1];
                logoHash = reader.IsDBNull(2) ? null : reader.GetString(2);
            }
            await reader.DisposeAsync();
            if (request.RemoveLogo) { logoType = null; logoBytes = null; logoHash = null; }
            else if (replacementLogo is not null)
            {
                logoType = replacementType;
                logoBytes = replacementLogo;
                logoHash = Convert.ToHexString(SHA256.HashData(replacementLogo)).ToLowerInvariant();
            }

            await using var save = await Command("""
                insert into organization_profiles(organization_id,legal_name,display_name,address,business_phone,contact_email,
                    logo_content_type,logo_bytes,logo_sha256,updated_by)
                values(@organization,@legal,@display,@address,@phone,@email,@logoType,@logoBytes,@logoHash,@actor)
                on conflict(organization_id) do update set legal_name=excluded.legal_name,display_name=excluded.display_name,
                    address=excluded.address,business_phone=excluded.business_phone,contact_email=excluded.contact_email,
                    logo_content_type=excluded.logo_content_type,logo_bytes=excluded.logo_bytes,logo_sha256=excluded.logo_sha256,
                    updated_by=excluded.updated_by,updated_at=now()
                """, token);
            Add(save, "legal", legalName); Add(save, "display", displayName); Add(save, "address", address);
            Add(save, "phone", phone); Add(save, "email", email); Add(save, "logoType", logoType);
            Add(save, "logoBytes", logoBytes); Add(save, "logoHash", logoHash); Add(save, "actor", user.UserName);
            await save.ExecuteNonQueryAsync(token);

            var organization = await db.Organizations.SingleAsync(item => item.Id == tenant.OrganizationId, token);
            db.Entry(organization).CurrentValues.SetValues(organization with { Name = displayName });
            db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "admin.company_profile_saved", user.UserName, "Organization",
                tenant.OrganizationId, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return NoContent();
        });
    }

    private async Task<DbCommand> Command(string sql, CancellationToken token)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(token);
        var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = sql;
        Add(command, "organization", tenant.OrganizationId);
        return command;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        if (command.Parameters.Contains(name)) return;
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; command.Parameters.Add(parameter);
    }
    private static string? Clean(string? value, int max) => string.IsNullOrWhiteSpace(value) || value.Trim().Length > max ? null : value.Trim();
    private static string? CleanOptional(string? value, int max) => value?.Trim().Length > max ? null : value?.Trim() ?? "";
    private static bool ValidEmail(string value) { if (value.Length == 0) return true; try { return new MailAddress(value).Address == value; } catch { return false; } }
    private sealed record CompanyProfileRow(string LegalName, string DisplayName, string Address, string BusinessPhone, string ContactEmail,
        string? LogoContentType, byte[]? LogoBytes, string UpdatedBy, DateTimeOffset UpdatedAt);
}

public sealed record CompanyProfileRequest(string LegalName, string DisplayName, string? Address, string? BusinessPhone,
    string? ContactEmail, string? LogoContentType, string? LogoBase64, bool RemoveLogo = false);
