using System.Data.Common;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[ApiController]
[Authorize(Policy = "Phase1User")]
[Route("api/phase1/service-users/{serviceUserId:guid}")]
public sealed class PersonGovernanceController : ControllerBase
{
    private readonly CareDbContext _context;
    private readonly ITenantContext _tenant;
    private readonly ICurrentUserContext _currentUser;

    public PersonGovernanceController(CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser)
    {
        _context = context;
        _tenant = tenant;
        _currentUser = currentUser;
    }

    [HttpGet("contacts")]
    public async Task<IActionResult> GetContacts(Guid serviceUserId)
    {
        if (!await CanAccessPerson(serviceUserId)) return NotFound();
        return Ok(await QueryAsync(
            """
            select id, service_user_id, contact_type, full_name, relationship, phone_number, email,
                   organization_name, is_primary, is_emergency, created_at, updated_at
            from person_contacts
            where service_user_id = @serviceUserId and organization_id = @organizationId
            order by is_primary desc, is_emergency desc, full_name
            """,
            command => AddPersonParameters(command, serviceUserId),
            reader => new PersonContactResponse(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetBoolean(8), reader.GetBoolean(9), reader.GetDateTime(10), reader.GetDateTime(11))));
    }

    [HttpPost("contacts")]
    public async Task<IActionResult> CreateContact(Guid serviceUserId, UpsertPersonContactRequest request)
    {
        if (!await CanManagePerson(serviceUserId)) return Forbid();
        if (string.IsNullOrWhiteSpace(request.FullName) || string.IsNullOrWhiteSpace(request.ContactType))
            return BadRequest(new { message = "Contact type and full name are required." });

        var id = Guid.NewGuid();
        await ExecuteAsync(
            """
            insert into person_contacts
                (id, service_user_id, organization_id, branch_id, contact_type, full_name, relationship,
                 phone_number, email, organization_name, is_primary, is_emergency, created_at, updated_at)
            values
                (@id, @serviceUserId, @organizationId, @branchId, @contactType, @fullName, @relationship,
                 @phoneNumber, @email, @organizationName, @isPrimary, @isEmergency, now(), now())
            """,
            command =>
            {
                AddPersonParameters(command, serviceUserId);
                Add(command, "id", id);
                Add(command, "branchId", _tenant.BranchId ?? TenantDefaults.BranchId);
                Add(command, "contactType", request.ContactType.Trim());
                Add(command, "fullName", request.FullName.Trim());
                Add(command, "relationship", request.Relationship?.Trim() ?? "");
                Add(command, "phoneNumber", request.PhoneNumber?.Trim() ?? "");
                Add(command, "email", request.Email?.Trim() ?? "");
                Add(command, "organizationName", request.OrganizationName?.Trim() ?? "");
                Add(command, "isPrimary", request.IsPrimary);
                Add(command, "isEmergency", request.IsEmergency);
            });
        AddAudit("person_contact.created", "PersonContact", id);
        await _context.SaveChangesAsync();
        return Created($"/api/phase1/service-users/{serviceUserId}/contacts/{id}", new { id });
    }

    [HttpPut("contacts/{id:guid}")]
    public async Task<IActionResult> UpdateContact(Guid serviceUserId, Guid id, UpsertPersonContactRequest request)
    {
        if (!await CanManagePerson(serviceUserId)) return Forbid();
        var affected = await ExecuteAsync(
            """
            update person_contacts set
                contact_type=@contactType, full_name=@fullName, relationship=@relationship,
                phone_number=@phoneNumber, email=@email, organization_name=@organizationName,
                is_primary=@isPrimary, is_emergency=@isEmergency, updated_at=now()
            where id=@id and service_user_id=@serviceUserId and organization_id=@organizationId
            """,
            command =>
            {
                AddPersonParameters(command, serviceUserId);
                Add(command, "id", id);
                Add(command, "contactType", request.ContactType.Trim());
                Add(command, "fullName", request.FullName.Trim());
                Add(command, "relationship", request.Relationship?.Trim() ?? "");
                Add(command, "phoneNumber", request.PhoneNumber?.Trim() ?? "");
                Add(command, "email", request.Email?.Trim() ?? "");
                Add(command, "organizationName", request.OrganizationName?.Trim() ?? "");
                Add(command, "isPrimary", request.IsPrimary);
                Add(command, "isEmergency", request.IsEmergency);
            });
        if (affected == 0) return NotFound();
        AddAudit("person_contact.updated", "PersonContact", id);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("contacts/{id:guid}")]
    public async Task<IActionResult> DeleteContact(Guid serviceUserId, Guid id)
    {
        if (!await CanManagePerson(serviceUserId)) return Forbid();
        var affected = await ExecuteAsync(
            "delete from person_contacts where id=@id and service_user_id=@serviceUserId and organization_id=@organizationId",
            command => { AddPersonParameters(command, serviceUserId); Add(command, "id", id); });
        if (affected == 0) return NotFound();
        AddAudit("person_contact.deleted", "PersonContact", id);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("consents")]
    public async Task<IActionResult> GetConsents(Guid serviceUserId)
    {
        if (!await CanAccessPerson(serviceUserId)) return NotFound();
        return Ok(await QueryAsync(
            """
            select id, service_user_id, consent_type, scope, status, capacity_basis, decision_maker,
                   evidence_reference, effective_from, expires_at, withdrawn_at, withdrawal_reason, created_at
            from consent_records
            where service_user_id=@serviceUserId and organization_id=@organizationId
            order by created_at desc
            """,
            command => AddPersonParameters(command, serviceUserId),
            reader => new ConsentRecordResponse(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetDateTime(8),
                reader.IsDBNull(9) ? null : reader.GetDateTime(9), reader.IsDBNull(10) ? null : reader.GetDateTime(10),
                reader.GetString(11), reader.GetDateTime(12))));
    }

    [HttpPost("consents")]
    public async Task<IActionResult> CreateConsent(Guid serviceUserId, CreateConsentRecordRequest request)
    {
        if (!await CanManagePerson(serviceUserId)) return Forbid();
        if (string.IsNullOrWhiteSpace(request.ConsentType) || string.IsNullOrWhiteSpace(request.Scope))
            return BadRequest(new { message = "Consent type and scope are required." });

        var id = Guid.NewGuid();
        await ExecuteAsync(
            """
            insert into consent_records
              (id, service_user_id, organization_id, branch_id, consent_type, scope, status, capacity_basis,
               decision_maker, evidence_reference, effective_from, expires_at, withdrawn_at, withdrawal_reason, created_at)
            values
              (@id,@serviceUserId,@organizationId,@branchId,@consentType,@scope,'Active',@capacityBasis,
               @decisionMaker,@evidenceReference,@effectiveFrom,@expiresAt,null,'',now())
            """,
            command =>
            {
                AddPersonParameters(command, serviceUserId);
                Add(command, "id", id);
                Add(command, "branchId", _tenant.BranchId ?? TenantDefaults.BranchId);
                Add(command, "consentType", request.ConsentType.Trim());
                Add(command, "scope", request.Scope.Trim());
                Add(command, "capacityBasis", request.CapacityBasis?.Trim() ?? "Not recorded");
                Add(command, "decisionMaker", request.DecisionMaker?.Trim() ?? "");
                Add(command, "evidenceReference", request.EvidenceReference?.Trim() ?? "");
                Add(command, "effectiveFrom", request.EffectiveFrom.UtcDateTime);
                Add(command, "expiresAt", request.ExpiresAt?.UtcDateTime);
            });
        AddAudit("consent.created", "ConsentRecord", id);
        await _context.SaveChangesAsync();
        return Created($"/api/phase1/service-users/{serviceUserId}/consents/{id}", new { id });
    }

    [HttpPost("consents/{id:guid}/withdraw")]
    public async Task<IActionResult> WithdrawConsent(Guid serviceUserId, Guid id, WithdrawConsentRequest request)
    {
        if (!await CanManagePerson(serviceUserId)) return Forbid();
        if (string.IsNullOrWhiteSpace(request.Reason)) return BadRequest(new { message = "Withdrawal reason is required." });
        var affected = await ExecuteAsync(
            """
            update consent_records set status='Withdrawn', withdrawn_at=now(), withdrawal_reason=@reason
            where id=@id and service_user_id=@serviceUserId and organization_id=@organizationId and status <> 'Withdrawn'
            """,
            command => { AddPersonParameters(command, serviceUserId); Add(command, "id", id); Add(command, "reason", request.Reason.Trim()); });
        if (affected == 0) return NotFound();
        AddAudit("consent.withdrawn", "ConsentRecord", id);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("funding")]
    public async Task<IActionResult> GetFunding(Guid serviceUserId)
    {
        if (!await CanAccessPerson(serviceUserId)) return NotFound();
        return Ok(await QueryAsync(
            """
            select id, service_user_id, funding_source, funder_name, contract_reference, care_package_type,
                   authorized_hours_per_week, hourly_rate, valid_from, valid_to, status, notes, created_at, updated_at
            from funding_arrangements
            where service_user_id=@serviceUserId and organization_id=@organizationId
            order by valid_from desc
            """,
            command => AddPersonParameters(command, serviceUserId),
            reader => new FundingArrangementResponse(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetDecimal(6), reader.GetDecimal(7), reader.GetDateTime(8), reader.IsDBNull(9) ? null : reader.GetDateTime(9),
                reader.GetString(10), reader.GetString(11), reader.GetDateTime(12), reader.GetDateTime(13))));
    }

    [HttpPost("funding")]
    public async Task<IActionResult> CreateFunding(Guid serviceUserId, CreateFundingArrangementRequest request)
    {
        if (!await CanManagePerson(serviceUserId)) return Forbid();
        if (string.IsNullOrWhiteSpace(request.FundingSource) || request.AuthorizedHoursPerWeek < 0 || request.HourlyRate < 0)
            return BadRequest(new { message = "Funding source is required and financial values cannot be negative." });
        var id = Guid.NewGuid();
        await ExecuteAsync(
            """
            insert into funding_arrangements
              (id,service_user_id,organization_id,branch_id,funding_source,funder_name,contract_reference,care_package_type,
               authorized_hours_per_week,hourly_rate,valid_from,valid_to,status,notes,created_at,updated_at)
            values
              (@id,@serviceUserId,@organizationId,@branchId,@fundingSource,@funderName,@contractReference,@carePackageType,
               @authorizedHours,@hourlyRate,@validFrom,@validTo,@status,@notes,now(),now())
            """,
            command =>
            {
                AddPersonParameters(command, serviceUserId);
                Add(command, "id", id);
                Add(command, "branchId", _tenant.BranchId ?? TenantDefaults.BranchId);
                Add(command, "fundingSource", request.FundingSource.Trim());
                Add(command, "funderName", request.FunderName?.Trim() ?? "");
                Add(command, "contractReference", request.ContractReference?.Trim() ?? "");
                Add(command, "carePackageType", request.CarePackageType?.Trim() ?? "");
                Add(command, "authorizedHours", request.AuthorizedHoursPerWeek);
                Add(command, "hourlyRate", request.HourlyRate);
                Add(command, "validFrom", request.ValidFrom.UtcDateTime);
                Add(command, "validTo", request.ValidTo?.UtcDateTime);
                Add(command, "status", string.IsNullOrWhiteSpace(request.Status) ? "Active" : request.Status.Trim());
                Add(command, "notes", request.Notes?.Trim() ?? "");
            });
        AddAudit("funding.created", "FundingArrangement", id);
        await _context.SaveChangesAsync();
        return Created($"/api/phase1/service-users/{serviceUserId}/funding/{id}", new { id });
    }

    [HttpGet("governance-summary")]
    public async Task<IActionResult> GetGovernanceSummary(Guid serviceUserId)
    {
        if (!await CanAccessPerson(serviceUserId)) return NotFound();
        var contactCount = await ScalarAsync("select count(*) from person_contacts where service_user_id=@serviceUserId and organization_id=@organizationId", serviceUserId);
        var activeConsents = await ScalarAsync("select count(*) from consent_records where service_user_id=@serviceUserId and organization_id=@organizationId and status='Active'", serviceUserId);
        var activeFunding = await ScalarAsync("select count(*) from funding_arrangements where service_user_id=@serviceUserId and organization_id=@organizationId and status='Active'", serviceUserId);
        return Ok(new { contactCount, activeConsents, activeFunding });
    }

    private async Task<bool> CanAccessPerson(Guid serviceUserId)
    {
        var person = await _context.ServiceUsers.AsNoTracking().FirstOrDefaultAsync(item => item.Id == serviceUserId && item.OrganizationId == _tenant.OrganizationId);
        if (person is null || !_tenant.CanAccess(person.OrganizationId, person.BranchId)) return false;
        if (!_currentUser.IsFamilyMember) return true;
        if (_currentUser.FamilyMemberId is null) return false;
        return await _context.FamilyMembers.AsNoTracking().AnyAsync(item => item.Id == _currentUser.FamilyMemberId && item.ServiceUserId == serviceUserId && item.OrganizationId == _tenant.OrganizationId);
    }

    private async Task<bool> CanManagePerson(Guid serviceUserId)
    {
        if (!_currentUser.HasAnyRole(UserRole.Administrator, UserRole.CareManager, UserRole.CareCoordinator)) return false;
        return await CanAccessPerson(serviceUserId);
    }

    private void AddAudit(string action, string entityType, Guid entityId) =>
        _context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), action, _currentUser.UserName, entityType, entityId, DateTimeOffset.UtcNow, _tenant.OrganizationId, _tenant.BranchId ?? TenantDefaults.BranchId));

    private void AddPersonParameters(DbCommand command, Guid serviceUserId)
    {
        Add(command, "serviceUserId", serviceUserId);
        Add(command, "organizationId", _tenant.OrganizationId);
    }

    private async Task<int> ScalarAsync(string sql, Guid serviceUserId)
    {
        var connection = _context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddPersonParameters(command, serviceUserId);
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    private async Task<int> ExecuteAsync(string sql, Action<DbCommand> configure)
    {
        var connection = _context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure(command);
        return await command.ExecuteNonQueryAsync();
    }

    private async Task<List<T>> QueryAsync<T>(string sql, Action<DbCommand> configure, Func<DbDataReader, T> map)
    {
        var connection = _context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure(command);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<T>();
        while (await reader.ReadAsync()) result.Add(map(reader));
        return result;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}

public sealed record UpsertPersonContactRequest(string ContactType, string FullName, string? Relationship, string? PhoneNumber, string? Email, string? OrganizationName, bool IsPrimary, bool IsEmergency);
public sealed record CreateConsentRecordRequest(string ConsentType, string Scope, string? CapacityBasis, string? DecisionMaker, string? EvidenceReference, DateTimeOffset EffectiveFrom, DateTimeOffset? ExpiresAt);
public sealed record WithdrawConsentRequest(string Reason);
public sealed record CreateFundingArrangementRequest(string FundingSource, string? FunderName, string? ContractReference, string? CarePackageType, decimal AuthorizedHoursPerWeek, decimal HourlyRate, DateTimeOffset ValidFrom, DateTimeOffset? ValidTo, string? Status, string? Notes);
public sealed record PersonContactResponse(Guid Id, Guid ServiceUserId, string ContactType, string FullName, string Relationship, string PhoneNumber, string Email, string OrganizationName, bool IsPrimary, bool IsEmergency, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record ConsentRecordResponse(Guid Id, Guid ServiceUserId, string ConsentType, string Scope, string Status, string CapacityBasis, string DecisionMaker, string EvidenceReference, DateTime EffectiveFrom, DateTime? ExpiresAt, DateTime? WithdrawnAt, string WithdrawalReason, DateTime CreatedAt);
public sealed record FundingArrangementResponse(Guid Id, Guid ServiceUserId, string FundingSource, string FunderName, string ContractReference, string CarePackageType, decimal AuthorizedHoursPerWeek, decimal HourlyRate, DateTime ValidFrom, DateTime? ValidTo, string Status, string Notes, DateTime CreatedAt, DateTime UpdatedAt);
