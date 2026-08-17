using System.Data;
using AiCare.Application.FamilyPortal;
using AiCare.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace AiCare.Infrastructure;

public sealed class FamilyPortalQueryStore(CareDbContext db) : IFamilyPortalQueryStore
{
    public async Task<FamilyPortalOverview?> GetOverviewAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, bool includeIncidents, bool includeMedication, CancellationToken cancellationToken)
    {
        var person = await db.ServiceUsers.AsNoTracking()
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == serviceUserId, cancellationToken);
        if (person is null) return null;

        var visits = await GetVisitsAsync(organizationId, serviceUserId, cancellationToken);
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var recentNotes = await db.CareNotes.AsNoTracking()
            .CountAsync(x => x.OrganizationId == organizationId && x.ServiceUserId == serviceUserId && x.CreatedAt >= since, cancellationToken);
        var incidents = includeIncidents
            ? await db.Incidents.AsNoTracking().CountAsync(x => x.OrganizationId == organizationId && x.ServiceUserId == serviceUserId && x.Status != "Closed", cancellationToken)
            : 0;
        var medicationEntries = includeMedication ? await CountMedicationEntriesAsync(organizationId, serviceUserId, since, cancellationToken) : 0;

        return new FamilyPortalOverview(
            new FamilyPersonSummary(person.Id, person.FullName, person.Status, person.Risk.ToString(), person.CareNeeds),
            visits.Where(x => x.StartsAt >= DateTimeOffset.UtcNow).OrderBy(x => x.StartsAt).Take(5).ToArray(),
            visits.OrderByDescending(x => x.StartsAt).Take(5).ToArray(),
            null,
            recentNotes,
            incidents,
            medicationEntries,
            0,
            DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyCollection<FamilyTimelineItem>> GetTimelineAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken)
    {
        var items = new List<FamilyTimelineItem>();
        if (permissions.Contains(FamilyPermissions.ViewVisits) || permissions.Contains(FamilyPermissions.ViewAppointments))
        {
            var visits = await GetVisitsAsync(organizationId, serviceUserId, cancellationToken);
            items.AddRange(visits.Select(x => new FamilyTimelineItem("Visit", x.VisitType, x.Status, x.StartsAt)));
        }

        var notes = await db.CareNotes.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.ServiceUserId == serviceUserId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(30)
            .Select(x => new FamilyTimelineItem("Care note", x.Summary, "Care update", x.CreatedAt))
            .ToListAsync(cancellationToken);
        items.AddRange(notes);

        if (permissions.Contains(FamilyPermissions.ViewIncidentSummary))
        {
            var incidents = await db.Incidents.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.ServiceUserId == serviceUserId)
                .OrderByDescending(x => x.ReportedAt)
                .Take(20)
                .Select(x => new FamilyTimelineItem("Incident", x.Category, x.Status, x.ReportedAt))
                .ToListAsync(cancellationToken);
            items.AddRange(incidents);
        }

        if (permissions.Contains(FamilyPermissions.ViewDocuments))
        {
            var documents = await GetDocumentsAsync(organizationId, familyMemberId, serviceUserId, cancellationToken);
            items.AddRange(documents.Select(x => new FamilyTimelineItem("Document", x.FileName, x.Category, x.UploadedAt)));
        }

        if (permissions.Contains(FamilyPermissions.ViewCarePlan))
        {
            var plans = await db.CarePlans.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.ServiceUserId == serviceUserId)
                .OrderByDescending(x => x.ReviewDueAt)
                .Take(10)
                .Select(x => new FamilyTimelineItem("Care plan", x.Version, x.Status, x.ReviewDueAt))
                .ToListAsync(cancellationToken);
            items.AddRange(plans);
        }

        return items.OrderByDescending(x => x.When).Take(100).ToArray();
    }

    public async Task<IReadOnlyCollection<FamilyVisitItem>> GetVisitsAsync(Guid organizationId, Guid serviceUserId, CancellationToken cancellationToken)
    {
        var visits = await db.Visits.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.ServiceUserId == serviceUserId)
            .OrderByDescending(x => x.StartsAt)
            .Take(100)
            .ToListAsync(cancellationToken);
        var workerIds = visits.Select(x => x.CareWorkerId).Distinct().ToArray();
        var workers = await db.CareWorkers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && workerIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.FullName, cancellationToken);
        return visits.Select(x => new FamilyVisitItem(
            x.Id,
            x.StartsAt,
            x.VisitType,
            x.DurationMinutes,
            x.Status.ToString(),
            workers.TryGetValue(x.CareWorkerId, out var workerName) ? workerName : "Care team")).ToArray();
    }

    public async Task<IReadOnlyCollection<FamilyDocumentItem>> GetDocumentsAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, CancellationToken cancellationToken)
    {
        var result = new List<FamilyDocumentItem>();
        var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateCommand(connection);
        command.CommandText = """
            select d."Id", d."FileName", d."Category", d."UploadedAt", v.visibility
            from "Documents" d
            join family_document_visibility v on v.document_id = d."Id" and v.organization_id = @organization
            left join family_document_grants g on g.document_id = d."Id" and g.family_member_id = @family and g.organization_id = @organization
            where d."OrganizationId" = @organization
              and d."ServiceUserId" = @person
              and (v.visibility = 'ServiceUserAndRepresentative' or (v.visibility = 'ExplicitFamilyAccess' and g.family_member_id is not null))
            order by d."UploadedAt" desc
            """;
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("family", familyMemberId);
        command.Parameters.AddWithValue("person", serviceUserId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new FamilyDocumentItem(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3), reader.GetString(4)));
        return result;
    }

    public async Task<FamilyMonthlyReport?> GetMonthlyReportAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, bool includeIncidents, bool includeMedication, CancellationToken cancellationToken)
    {
        var person = await db.ServiceUsers.AsNoTracking().FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == serviceUserId, cancellationToken);
        if (person is null) return null;
        var to = DateTimeOffset.UtcNow;
        var from = to.AddDays(-30);
        var completedVisits = await db.Visits.AsNoTracking().CountAsync(x => x.OrganizationId == organizationId && x.ServiceUserId == serviceUserId && x.StartsAt >= from && x.Status == VisitStatus.Completed, cancellationToken);
        var notes = await db.CareNotes.AsNoTracking().CountAsync(x => x.OrganizationId == organizationId && x.ServiceUserId == serviceUserId && x.CreatedAt >= from, cancellationToken);
        var incidents = includeIncidents ? await db.Incidents.AsNoTracking().CountAsync(x => x.OrganizationId == organizationId && x.ServiceUserId == serviceUserId && x.ReportedAt >= from, cancellationToken) : 0;
        var medicationEntries = includeMedication ? await CountMedicationEntriesAsync(organizationId, serviceUserId, from, cancellationToken) : 0;
        return new FamilyMonthlyReport(serviceUserId, person.FullName, completedVisits, notes, incidents, medicationEntries, 0, from, to);
    }

    public async Task<FamilyNotificationPreferences> GetPreferencesAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, CancellationToken cancellationToken)
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateCommand(connection);
        command.CommandText = """
            select email_updates, sms_alerts, monthly_digest, incident_alerts, care_plan_signature_requests,
                   care_plan_updates, appointment_reminders, visit_updates, document_shared, new_messages,
                   complaint_responses, revision
            from family_notification_preferences
            where organization_id = @organization and family_member_id = @family and service_user_id = @person
            """;
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("family", familyMemberId);
        command.Parameters.AddWithValue("person", serviceUserId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return Defaults();
        return new FamilyNotificationPreferences(
            reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4),
            reader.GetBoolean(5), reader.GetBoolean(6), reader.GetBoolean(7), reader.GetBoolean(8), reader.GetBoolean(9),
            reader.GetBoolean(10), reader.GetInt64(11));
    }

    public async Task<FamilyNotificationPreferences> SavePreferencesAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, SaveFamilyNotificationPreferencesCommand command, CancellationToken cancellationToken)
    {
        var existing = await GetPreferencesAsync(organizationId, familyMemberId, serviceUserId, cancellationToken);
        if (command.ExpectedRevision is not null && existing.Revision > 0 && command.ExpectedRevision.Value != existing.Revision)
            throw new InvalidOperationException("Notification preferences were changed elsewhere. Refresh and try again.");
        var nextRevision = existing.Revision <= 0 ? 1 : existing.Revision + 1;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into family_notification_preferences (
                family_member_id, service_user_id, email_updates, sms_alerts, monthly_digest, incident_alerts,
                care_plan_signature_requests, care_plan_updates, appointment_reminders, visit_updates, document_shared,
                new_messages, complaint_responses, revision, updated_at, organization_id)
            values ({familyMemberId}, {serviceUserId}, {command.EmailUpdates}, {command.SmsAlerts}, {command.MonthlyDigest}, {command.IncidentAlerts},
                {command.CarePlanSignatureRequests}, {command.CarePlanUpdates}, {command.AppointmentReminders}, {command.VisitUpdates}, {command.DocumentShared},
                {command.NewMessages}, {command.ComplaintResponses}, {nextRevision}, now(), {organizationId})
            on conflict (family_member_id, service_user_id) do update set
                email_updates = excluded.email_updates,
                sms_alerts = excluded.sms_alerts,
                monthly_digest = excluded.monthly_digest,
                incident_alerts = excluded.incident_alerts,
                care_plan_signature_requests = excluded.care_plan_signature_requests,
                care_plan_updates = excluded.care_plan_updates,
                appointment_reminders = excluded.appointment_reminders,
                visit_updates = excluded.visit_updates,
                document_shared = excluded.document_shared,
                new_messages = excluded.new_messages,
                complaint_responses = excluded.complaint_responses,
                revision = excluded.revision,
                updated_at = now(),
                organization_id = excluded.organization_id
            """, cancellationToken);
        db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "family.preferences_updated", familyMemberId.ToString(), "FamilyMember", familyMemberId, DateTimeOffset.UtcNow, organizationId, null));
        await db.SaveChangesAsync(cancellationToken);
        return new FamilyNotificationPreferences(command.EmailUpdates, command.SmsAlerts, command.MonthlyDigest, command.IncidentAlerts,
            command.CarePlanSignatureRequests, command.CarePlanUpdates, command.AppointmentReminders, command.VisitUpdates,
            command.DocumentShared, command.NewMessages, command.ComplaintResponses, nextRevision);
    }

    public async Task SetDocumentVisibilityAsync(Guid organizationId, Guid? branchId, string actorName, SetFamilyDocumentVisibilityCommand command, CancellationToken cancellationToken)
    {
        if (command.Visibility is not ("InternalOnly" or "ServiceUserAndRepresentative" or "ExplicitFamilyAccess"))
            throw new InvalidOperationException("Unsupported family document visibility.");
        var document = await db.Documents.AsNoTracking().FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == command.DocumentId, cancellationToken)
            ?? throw new KeyNotFoundException("Document was not found.");
        if (command.Visibility == "ExplicitFamilyAccess" && command.FamilyMemberIds.Count == 0)
            throw new InvalidOperationException("At least one family member is required for explicit document access.");

        var distinctFamilyIds = command.FamilyMemberIds.Distinct().ToArray();
        if (distinctFamilyIds.Length > 0)
        {
            var validCount = await db.FamilyMembers.AsNoTracking().CountAsync(x => x.OrganizationId == organizationId && x.ServiceUserId == document.ServiceUserId && distinctFamilyIds.Contains(x.Id), cancellationToken);
            if (validCount != distinctFamilyIds.Length) throw new InvalidOperationException("Every selected family member must belong to the document's person and organization.");
        }

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                insert into family_document_visibility(document_id, visibility, organization_id, updated_at)
                values ({command.DocumentId}, {command.Visibility}, {organizationId}, now())
                on conflict (document_id) do update set visibility = excluded.visibility, organization_id = excluded.organization_id, updated_at = now()
                """, cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"delete from family_document_grants where document_id = {command.DocumentId}", cancellationToken);
            if (command.Visibility == "ExplicitFamilyAccess")
            {
                foreach (var familyMemberId in distinctFamilyIds)
                    await db.Database.ExecuteSqlInterpolatedAsync($"insert into family_document_grants(document_id, family_member_id, organization_id) values ({command.DocumentId}, {familyMemberId}, {organizationId})", cancellationToken);
            }
            db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "family.document_visibility_changed", actorName, "DocumentItem", command.DocumentId, DateTimeOffset.UtcNow, organizationId, branchId));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    private async Task<int> CountMedicationEntriesAsync(Guid organizationId, Guid serviceUserId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var medicationIds = await db.Medications.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.ServiceUserId == serviceUserId).Select(x => x.Id).ToArrayAsync(cancellationToken);
        if (medicationIds.Length == 0) return 0;
        return await db.MedicationAdministrationRecords.AsNoTracking().CountAsync(x => x.OrganizationId == organizationId && medicationIds.Contains(x.MedicationId) && (x.AdministeredAt ?? x.ScheduledAt) >= since, cancellationToken);
    }

    private static FamilyNotificationPreferences Defaults() => new(true, false, true, true, true, true, true, false, true, true, true, 0);

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private NpgsqlCommand CreateCommand(NpgsqlConnection connection)
    {
        var command = connection.CreateCommand();
        if (db.Database.CurrentTransaction is not null)
            command.Transaction = (NpgsqlTransaction)db.Database.CurrentTransaction.GetDbTransaction();
        return command;
    }
}
