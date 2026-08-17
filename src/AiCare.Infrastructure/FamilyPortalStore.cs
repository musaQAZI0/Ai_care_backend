using System.Data;
using AiCare.Application.FamilyPortal;
using AiCare.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace AiCare.Infrastructure;

public sealed class FamilyPortalStore : IFamilyPortalStore
{
    private readonly CareDbContext _db;

    public FamilyPortalStore(CareDbContext db)
    {
        _db = db;
    }

    public async Task<FamilyAccessSnapshot?> GetAccessAsync(Guid organizationId, Guid familyMemberId, CancellationToken cancellationToken)
    {
        var member = await _db.FamilyMembers.AsNoTracking()
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == familyMemberId, cancellationToken);
        if (member is null) return null;

        var row = await ReadGrantAsync(organizationId, familyMemberId, cancellationToken);
        var invitation = await ReadLatestInvitationAsync(organizationId, familyMemberId, cancellationToken);
        if (row is null)
        {
            return new FamilyAccessSnapshot(member.Id, member.ServiceUserId, member.FullName, member.Email, member.Relationship,
                member.Relationship, "Pending", "PendingVerification", null, null, 0, Array.Empty<string>(), invitation.Status, invitation.ExpiresAt);
        }

        var permissions = await ReadPermissionsAsync(row.Value.Id, cancellationToken);
        return new FamilyAccessSnapshot(member.Id, member.ServiceUserId, member.FullName, member.Email, member.Relationship,
            row.Value.AuthorityType, row.Value.VerificationStatus,
            EffectiveStatus(row.Value.AccessStatus, row.Value.ValidFrom, row.Value.ValidUntil, DateTimeOffset.UtcNow),
            row.Value.ValidFrom, row.Value.ValidUntil, row.Value.Revision, permissions, invitation.Status, invitation.ExpiresAt);
    }

    public async Task<FamilyAccessSnapshot> ConfigureAccessAsync(Guid organizationId, Guid? branchId, Guid actorUserId, string actorName, ConfigureFamilyAccessCommand command, CancellationToken cancellationToken)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            var member = await _db.FamilyMembers.AsNoTracking()
                .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == command.FamilyMemberId, cancellationToken)
                ?? throw new InvalidOperationException("Family member was not found in this organization.");

            var existing = await ReadGrantAsync(organizationId, member.Id, cancellationToken);
            if (command.ExpectedRevision is not null && existing is not null && existing.Value.Revision != command.ExpectedRevision.Value)
                throw new InvalidOperationException("Family access was changed by another user. Refresh and try again.");

            var grantId = existing?.Id ?? Guid.NewGuid();
            var nextRevision = existing is null ? 1 : existing.Value.Revision + 1;
            var verified = string.Equals(command.VerificationStatus, "Verified", StringComparison.OrdinalIgnoreCase);
            var accessStatus = verified ? "Active" : "PendingVerification";
            var verifiedAt = verified ? DateTimeOffset.UtcNow : (DateTimeOffset?)null;

            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                insert into family_access_grants (
                    id, family_member_id, service_user_id, authority_type, verification_status, access_status,
                    verified_by_user_id, verified_by, verified_at, valid_from, valid_until, revision,
                    created_at, updated_at, organization_id, branch_id)
                values ({grantId}, {member.Id}, {member.ServiceUserId}, {command.AuthorityType}, {command.VerificationStatus}, {accessStatus},
                    {actorUserId}, {actorName}, {verifiedAt}, {command.ValidFrom}, {command.ValidUntil}, {nextRevision},
                    now(), now(), {organizationId}, {branchId})
                on conflict (organization_id, family_member_id, service_user_id) do update set
                    authority_type = excluded.authority_type,
                    verification_status = excluded.verification_status,
                    access_status = excluded.access_status,
                    verified_by_user_id = excluded.verified_by_user_id,
                    verified_by = excluded.verified_by,
                    verified_at = excluded.verified_at,
                    valid_from = excluded.valid_from,
                    valid_until = excluded.valid_until,
                    revision = excluded.revision,
                    updated_at = now(),
                    branch_id = excluded.branch_id
                """, cancellationToken);

            await _db.Database.ExecuteSqlInterpolatedAsync($"delete from family_access_permissions where access_grant_id = {grantId}", cancellationToken);
            foreach (var permission in command.Permissions.Distinct(StringComparer.Ordinal))
                await _db.Database.ExecuteSqlInterpolatedAsync($"insert into family_access_permissions(access_grant_id, permission) values ({grantId}, {permission})", cancellationToken);

            AddAudit(organizationId, branchId, actorName, "family.access_configured", "FamilyMember", member.Id);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await GetAccessAsync(organizationId, member.Id, cancellationToken))!;
        });
    }

    public async Task<FamilyInvitationResult> CreateInvitationAsync(Guid organizationId, Guid? branchId, Guid actorUserId, string actorName, Guid familyMemberId, string tokenHash, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            var member = await _db.FamilyMembers.AsNoTracking()
                .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == familyMemberId, cancellationToken)
                ?? throw new InvalidOperationException("Family member was not found.");

            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                update family_portal_invitations set status = 'Revoked', revoked_at = now()
                where organization_id = {organizationId} and family_member_id = {familyMemberId} and status in ('Pending','Sent')
                """, cancellationToken);

            var id = Guid.NewGuid();
            var sentStatus = "Sent";
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                insert into family_portal_invitations
                    (id, family_member_id, token_hash, email, status, expires_at, created_at, created_by_user_id, created_by, organization_id, branch_id)
                values ({id}, {familyMemberId}, {tokenHash}, {member.Email}, {sentStatus}, {expiresAt}, now(), {actorUserId}, {actorName}, {organizationId}, {branchId})
                """, cancellationToken);

            AddAudit(organizationId, branchId, actorName, "family.invited", "FamilyMember", familyMemberId);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new FamilyInvitationResult(id, sentStatus, expiresAt, null);
        });
    }

    public async Task<FamilyInvitationValidation> ValidateInvitationAsync(string tokenHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateCommand(connection);
        command.CommandText = """
            select i.status, i.expires_at, f."FullName", o."Name"
            from family_portal_invitations i
            join "FamilyMembers" f on f."Id" = i.family_member_id
            join "Organizations" o on o."Id" = i.organization_id
            where i.token_hash = @token
            limit 1
            """;
        command.Parameters.AddWithValue("token", tokenHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new(false, "Invalid", "This invitation is invalid or is no longer available.", null, null, null);

        var status = reader.GetString(0);
        var expiresAt = reader.GetFieldValue<DateTimeOffset>(1);
        var fullName = reader.GetString(2);
        var providerName = reader.GetString(3);
        if (expiresAt <= now)
            return new(false, "Expired", "This invitation has expired. Please ask the care provider to send a new invitation.", fullName, providerName, expiresAt);
        if (status is not ("Pending" or "Sent"))
            return new(false, status, "This invitation has already been used or revoked.", fullName, providerName, expiresAt);
        return new(true, status, "Invitation is valid.", fullName, providerName, expiresAt);
    }

    public async Task AcceptInvitationAsync(string tokenHash, string password, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            var connection = await OpenConnectionAsync(cancellationToken);

            Guid invitationId;
            Guid familyMemberId;
            Guid organizationId;
            Guid? branchId;
            string email;
            string fullName;
            string status;
            DateTimeOffset expiresAt;
            await using (var command = CreateCommand(connection))
            {
                command.CommandText = """
                    select i.id, i.family_member_id, i.organization_id, i.branch_id, i.email, f."FullName", i.status, i.expires_at
                    from family_portal_invitations i
                    join "FamilyMembers" f on f."Id" = i.family_member_id
                    where i.token_hash = @token
                    for update
                    """;
                command.Parameters.AddWithValue("token", tokenHash);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Invitation is invalid.");
                invitationId = reader.GetGuid(0);
                familyMemberId = reader.GetGuid(1);
                organizationId = reader.GetGuid(2);
                branchId = reader.IsDBNull(3) ? null : reader.GetGuid(3);
                email = reader.GetString(4);
                fullName = reader.GetString(5);
                status = reader.GetString(6);
                expiresAt = reader.GetFieldValue<DateTimeOffset>(7);
            }

            if (expiresAt <= now) throw new InvalidOperationException("Invitation has expired.");
            if (status is not ("Pending" or "Sent")) throw new InvalidOperationException("Invitation has already been used or revoked.");

            var grant = await ReadGrantAsync(organizationId, familyMemberId, cancellationToken);
            if (grant is null || !string.Equals(grant.Value.VerificationStatus, "Verified", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Family authority is not verified.");
            if (EffectiveStatus(grant.Value.AccessStatus, grant.Value.ValidFrom, grant.Value.ValidUntil, now) is "Expired" or "Revoked")
                throw new InvalidOperationException("Family access is no longer active.");

            var existingUser = await _db.AppUsers.FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.FamilyMemberId == familyMemberId, cancellationToken);
            var passwordHash = PasswordHasher.HashPassword(password);
            if (existingUser is null)
                _db.AppUsers.Add(new AppUser(Guid.NewGuid(), email, email, passwordHash, UserRole.FamilyMember, true, organizationId, branchId, null, familyMemberId));
            else
                _db.Entry(existingUser).CurrentValues.SetValues(existingUser with { UserName = email, Email = email, PasswordHash = passwordHash, IsActive = true });
            await _db.SaveChangesAsync(cancellationToken);

            await _db.Database.ExecuteSqlInterpolatedAsync($"update family_portal_invitations set status = 'Accepted', accepted_at = {now} where id = {invitationId}", cancellationToken);
            await _db.Database.ExecuteSqlInterpolatedAsync($"update family_access_grants set access_status = 'Active', updated_at = now(), revision = revision + 1 where organization_id = {organizationId} and family_member_id = {familyMemberId}", cancellationToken);
            AddAudit(organizationId, branchId, fullName, "family.invitation_accepted", "FamilyMember", familyMemberId);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    public async Task SetAccessStatusAsync(Guid organizationId, Guid actorUserId, string actorName, Guid familyMemberId, string status, CancellationToken cancellationToken)
    {
        var grant = await ReadGrantAsync(organizationId, familyMemberId, cancellationToken)
            ?? throw new InvalidOperationException("Family access was not found.");
        if (status == "Active" && !string.Equals(grant.VerificationStatus, "Verified", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only verified family authority can be restored to active access.");
        if (status == "Active" && EffectiveStatus("Active", grant.ValidFrom, grant.ValidUntil, DateTimeOffset.UtcNow) == "Expired")
            throw new InvalidOperationException("Expired family access cannot be restored until its validity period is updated.");

        var verificationStatus = status == "Revoked" ? "Revoked" : null;
        var changed = verificationStatus is null
            ? await _db.Database.ExecuteSqlInterpolatedAsync($"update family_access_grants set access_status = {status}, revision = revision + 1, updated_at = now() where organization_id = {organizationId} and family_member_id = {familyMemberId}", cancellationToken)
            : await _db.Database.ExecuteSqlInterpolatedAsync($"update family_access_grants set access_status = {status}, verification_status = {verificationStatus}, revision = revision + 1, updated_at = now() where organization_id = {organizationId} and family_member_id = {familyMemberId}", cancellationToken);
        if (changed == 0) throw new InvalidOperationException("Family access was not found.");

        if (status == "Revoked")
            await _db.Database.ExecuteSqlInterpolatedAsync($"update family_portal_invitations set status = 'Revoked', revoked_at = now() where organization_id = {organizationId} and family_member_id = {familyMemberId} and status in ('Pending','Sent')", cancellationToken);

        var user = await _db.AppUsers.FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.FamilyMemberId == familyMemberId, cancellationToken);
        if (user is not null)
            _db.Entry(user).CurrentValues.SetValues(user with { IsActive = status == "Active" });

        AddAudit(organizationId, null, actorName, $"family.access_{status.ToLowerInvariant()}", "FamilyMember", familyMemberId);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<FamilyPortalPerson>> GetAuthorizedPeopleAsync(Guid organizationId, Guid familyMemberId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var access = await GetAccessAsync(organizationId, familyMemberId, cancellationToken);
        if (access is null || access.AccessStatus != "Active" || access.VerificationStatus != "Verified") return Array.Empty<FamilyPortalPerson>();
        var person = await _db.ServiceUsers.AsNoTracking().FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == access.ServiceUserId, cancellationToken);
        return person is null ? Array.Empty<FamilyPortalPerson>() : new[] { new FamilyPortalPerson(person.Id, person.FullName, access.Relationship, access.Permissions) };
    }

    public async Task<bool> HasPermissionAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, string permission, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var grant = await ReadGrantAsync(organizationId, familyMemberId, cancellationToken);
        if (grant is null || grant.Value.ServiceUserId != serviceUserId || grant.Value.VerificationStatus != "Verified") return false;
        if (EffectiveStatus(grant.Value.AccessStatus, grant.Value.ValidFrom, grant.Value.ValidUntil, now) != "Active") return false;
        var permissions = await ReadPermissionsAsync(grant.Value.Id, cancellationToken);
        return permissions.Contains(permission, StringComparer.Ordinal);
    }

    public async Task<FamilyFeedbackResult> SubmitFeedbackAsync(Guid organizationId, Guid? branchId, Guid familyMemberId, FamilyFeedbackInput input, CancellationToken cancellationToken)
    {
        if (!await HasPermissionAsync(organizationId, familyMemberId, input.ServiceUserId, FamilyPermissions.SubmitFeedback, DateTimeOffset.UtcNow, cancellationToken))
            throw new UnauthorizedAccessException("Family access is not authorized for feedback on this person.");

        var id = Guid.NewGuid();
        var submittedAt = DateTimeOffset.UtcNow;
        var submittedStatus = "Submitted";
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into family_feedback_cases
                (id, service_user_id, family_member_id, type, subject, description, priority, status, submitted_at, response_due_at, resolution, organization_id, branch_id)
            values ({id}, {input.ServiceUserId}, {familyMemberId}, {input.Type}, {input.Subject}, {input.Description}, {input.Priority}, {submittedStatus},
                {submittedAt}, {submittedAt.AddDays(input.Type == "Complaint" ? 2 : 5)}, '', {organizationId}, {branchId})
            """, cancellationToken);
        AddAudit(organizationId, branchId, familyMemberId.ToString(), "family.feedback_submitted", "FamilyFeedbackCase", id);
        await _db.SaveChangesAsync(cancellationToken);
        return new FamilyFeedbackResult(id, submittedStatus, submittedAt);
    }

    private async Task<(Guid Id, Guid ServiceUserId, string AuthorityType, string VerificationStatus, string AccessStatus, DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil, long Revision)?> ReadGrantAsync(Guid organizationId, Guid familyMemberId, CancellationToken cancellationToken)
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateCommand(connection);
        command.CommandText = """
            select id, service_user_id, authority_type, verification_status, access_status, valid_from, valid_until, revision
            from family_access_grants where organization_id = @organization and family_member_id = @family limit 1
            """;
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("family", familyMemberId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return (reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6), reader.GetInt64(7));
    }

    private async Task<IReadOnlyCollection<string>> ReadPermissionsAsync(Guid grantId, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateCommand(connection);
        command.CommandText = "select permission from family_access_permissions where access_grant_id = @grant order by permission";
        command.Parameters.AddWithValue("grant", grantId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }

    private async Task<(string Status, DateTimeOffset? ExpiresAt)> ReadLatestInvitationAsync(Guid organizationId, Guid familyMemberId, CancellationToken cancellationToken)
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateCommand(connection);
        command.CommandText = "select status, expires_at from family_portal_invitations where organization_id = @organization and family_member_id = @family order by created_at desc limit 1";
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("family", familyMemberId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1)) : ("NotInvited", null);
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private NpgsqlCommand CreateCommand(NpgsqlConnection connection)
    {
        var command = connection.CreateCommand();
        if (_db.Database.CurrentTransaction is not null)
            command.Transaction = (NpgsqlTransaction)_db.Database.CurrentTransaction.GetDbTransaction();
        return command;
    }

    private static string EffectiveStatus(string accessStatus, DateTimeOffset? validFrom, DateTimeOffset? validUntil, DateTimeOffset now)
    {
        if (accessStatus is "Suspended" or "Revoked" or "PendingVerification") return accessStatus;
        if (validFrom is not null && validFrom > now) return "PendingVerification";
        if (validUntil is not null && validUntil <= now) return "Expired";
        return accessStatus;
    }

    private void AddAudit(Guid organizationId, Guid? branchId, string actor, string action, string entityType, Guid entityId)
        => _db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), action, string.IsNullOrWhiteSpace(actor) ? "system" : actor, entityType, entityId, DateTimeOffset.UtcNow, organizationId, branchId));
}
