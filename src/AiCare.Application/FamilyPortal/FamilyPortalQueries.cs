namespace AiCare.Application.FamilyPortal;

public sealed record FamilyPersonSummary(Guid Id, string FullName, string Status, string Risk, string CareNeeds);
public sealed record FamilyVisitItem(Guid Id, DateTimeOffset StartsAt, string VisitType, int DurationMinutes, string Status, string CareWorkerName);
public sealed record FamilyTimelineItem(string Type, string Title, string Detail, DateTimeOffset When);
public sealed record FamilyDocumentItem(Guid Id, string FileName, string Category, DateTimeOffset UploadedAt, string Visibility);
public sealed record FamilyCarePlanItem(Guid Id, string Version, string Status, DateTimeOffset ReviewDueAt);
public sealed record FamilyPortalOverview(
    FamilyPersonSummary Person,
    IReadOnlyCollection<FamilyVisitItem> UpcomingVisits,
    IReadOnlyCollection<FamilyVisitItem> RecentVisits,
    FamilyCarePlanItem? CurrentCarePlan,
    int RecentNotes,
    int OpenIncidents,
    int MedicationEntries,
    int SharedDocuments,
    DateTimeOffset UpdatedAt);

public sealed record FamilyMonthlyReport(
    Guid ServiceUserId,
    string FullName,
    int CompletedVisits,
    int CareNotes,
    int Incidents,
    int MedicationEntries,
    int SharedDocuments,
    DateTimeOffset PeriodFrom,
    DateTimeOffset PeriodTo);

public sealed record FamilyNotificationPreferences(
    bool EmailUpdates,
    bool SmsAlerts,
    bool MonthlyDigest,
    bool IncidentAlerts,
    bool CarePlanSignatureRequests,
    bool CarePlanUpdates,
    bool AppointmentReminders,
    bool VisitUpdates,
    bool DocumentShared,
    bool NewMessages,
    bool ComplaintResponses,
    long Revision);

public sealed record SaveFamilyNotificationPreferencesCommand(
    bool EmailUpdates,
    bool SmsAlerts,
    bool MonthlyDigest,
    bool IncidentAlerts,
    bool CarePlanSignatureRequests,
    bool CarePlanUpdates,
    bool AppointmentReminders,
    bool VisitUpdates,
    bool DocumentShared,
    bool NewMessages,
    bool ComplaintResponses,
    long? ExpectedRevision);

public sealed record SetFamilyDocumentVisibilityCommand(Guid DocumentId, string Visibility, IReadOnlyCollection<Guid> FamilyMemberIds);

public interface IFamilyPortalQueryStore
{
    Task<FamilyPortalOverview?> GetOverviewAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, bool includeIncidents, bool includeMedication, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<FamilyTimelineItem>> GetTimelineAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<FamilyVisitItem>> GetVisitsAsync(Guid organizationId, Guid serviceUserId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<FamilyDocumentItem>> GetDocumentsAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, CancellationToken cancellationToken);
    Task<FamilyMonthlyReport?> GetMonthlyReportAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, bool includeIncidents, bool includeMedication, CancellationToken cancellationToken);
    Task<FamilyNotificationPreferences> GetPreferencesAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, CancellationToken cancellationToken);
    Task<FamilyNotificationPreferences> SavePreferencesAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, SaveFamilyNotificationPreferencesCommand command, CancellationToken cancellationToken);
    Task SetDocumentVisibilityAsync(Guid organizationId, Guid? branchId, string actorName, SetFamilyDocumentVisibilityCommand command, CancellationToken cancellationToken);
}

public interface IFamilyPortalQueryService
{
    Task<FamilyPortalOverview?> GetOverviewAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<FamilyTimelineItem>> GetTimelineAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<FamilyVisitItem>> GetVisitsAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, bool appointmentView, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<FamilyDocumentItem>> GetDocumentsAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, CancellationToken cancellationToken);
    Task<FamilyMonthlyReport?> GetMonthlyReportAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, CancellationToken cancellationToken);
    Task<FamilyNotificationPreferences> GetPreferencesAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, CancellationToken cancellationToken);
    Task<FamilyNotificationPreferences> SavePreferencesAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, SaveFamilyNotificationPreferencesCommand command, CancellationToken cancellationToken);
}

public sealed class FamilyPortalQueryService(IFamilyPortalService access, IFamilyPortalQueryStore store) : IFamilyPortalQueryService
{
    public async Task<FamilyPortalOverview?> GetOverviewAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, CancellationToken cancellationToken)
    {
        var permissions = await GetPermissionsAsync(organizationId, familyMemberId, serviceUserId, FamilyPermissions.ViewCareSummary, cancellationToken);
        return await store.GetOverviewAsync(organizationId, familyMemberId, serviceUserId,
            permissions.Contains(FamilyPermissions.ViewIncidentSummary),
            permissions.Contains(FamilyPermissions.ViewMedicationSummary), cancellationToken);
    }

    public async Task<IReadOnlyCollection<FamilyTimelineItem>> GetTimelineAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, CancellationToken cancellationToken)
    {
        var permissions = await GetPermissionsAsync(organizationId, familyMemberId, serviceUserId, FamilyPermissions.ViewTimeline, cancellationToken);
        return await store.GetTimelineAsync(organizationId, familyMemberId, serviceUserId, permissions, cancellationToken);
    }

    public async Task<IReadOnlyCollection<FamilyVisitItem>> GetVisitsAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, bool appointmentView, CancellationToken cancellationToken)
    {
        await access.EnsurePermissionAsync(organizationId, familyMemberId, serviceUserId,
            appointmentView ? FamilyPermissions.ViewAppointments : FamilyPermissions.ViewVisits, cancellationToken);
        return await store.GetVisitsAsync(organizationId, serviceUserId, cancellationToken);
    }

    public async Task<IReadOnlyCollection<FamilyDocumentItem>> GetDocumentsAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, CancellationToken cancellationToken)
    {
        await access.EnsurePermissionAsync(organizationId, familyMemberId, serviceUserId, FamilyPermissions.ViewDocuments, cancellationToken);
        return await store.GetDocumentsAsync(organizationId, familyMemberId, serviceUserId, cancellationToken);
    }

    public async Task<FamilyMonthlyReport?> GetMonthlyReportAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, CancellationToken cancellationToken)
    {
        var permissions = await GetPermissionsAsync(organizationId, familyMemberId, serviceUserId, FamilyPermissions.ViewCareSummary, cancellationToken);
        return await store.GetMonthlyReportAsync(organizationId, familyMemberId, serviceUserId,
            permissions.Contains(FamilyPermissions.ViewIncidentSummary),
            permissions.Contains(FamilyPermissions.ViewMedicationSummary), cancellationToken);
    }

    public async Task<FamilyNotificationPreferences> GetPreferencesAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, CancellationToken cancellationToken)
    {
        await access.EnsurePermissionAsync(organizationId, familyMemberId, serviceUserId, FamilyPermissions.ViewCareSummary, cancellationToken);
        return await store.GetPreferencesAsync(organizationId, familyMemberId, serviceUserId, cancellationToken);
    }

    public async Task<FamilyNotificationPreferences> SavePreferencesAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, SaveFamilyNotificationPreferencesCommand command, CancellationToken cancellationToken)
    {
        await access.EnsurePermissionAsync(organizationId, familyMemberId, serviceUserId, FamilyPermissions.ViewCareSummary, cancellationToken);
        return await store.SavePreferencesAsync(organizationId, familyMemberId, serviceUserId, command, cancellationToken);
    }

    private async Task<IReadOnlyCollection<string>> GetPermissionsAsync(Guid organizationId, Guid familyMemberId, Guid serviceUserId, string required, CancellationToken cancellationToken)
    {
        await access.EnsurePermissionAsync(organizationId, familyMemberId, serviceUserId, required, cancellationToken);
        var snapshot = await access.GetAccessAsync(organizationId, familyMemberId, cancellationToken)
            ?? throw new UnauthorizedAccessException("Family access was not found.");
        if (snapshot.ServiceUserId != serviceUserId) throw new UnauthorizedAccessException("Family access is not authorized for this person.");
        return snapshot.Permissions;
    }
}
