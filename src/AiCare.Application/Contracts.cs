using AiCare.Domain;

namespace AiCare.Application;

public interface ICareRepository
{
    IReadOnlyCollection<ServiceUser> GetServiceUsers();
    ServiceUser? GetServiceUser(Guid id);
    ServiceUser AddServiceUser(CreateServiceUserRequest request);
    ServiceUser? UpdateServiceUser(Guid id, CreateServiceUserRequest request);
    IReadOnlyCollection<CareWorker> GetCareWorkers();
    CareWorker AddCareWorker(CreateCareWorkerRequest request);
    CareWorker? UpdateCareWorker(Guid id, CreateCareWorkerRequest request);
    IReadOnlyCollection<Visit> GetVisits();
    Visit AddVisit(CreateVisitRequest request);
    Visit? UpdateVisit(Guid id, CreateVisitRequest request);
    Visit? UpdateVisitStatus(Guid id, VisitStatus status);
    Visit? CheckInVisit(Guid id, VisitCheckInRequest request);
    Visit? CheckOutVisit(Guid id, VisitCheckOutRequest request);
    IReadOnlyCollection<CarePlan> GetCarePlans();
    CarePlan AddCarePlan(CreateCarePlanRequest request);
    CarePlan? UpdateCarePlan(Guid id, CreateCarePlanRequest request);
    bool DeleteCarePlan(Guid id);
    IReadOnlyCollection<RiskAssessment> GetRiskAssessments();
    RiskAssessment AddRiskAssessment(CreateRiskAssessmentRequest request);
    RiskAssessment? UpdateRiskAssessment(Guid id, CreateRiskAssessmentRequest request);
    bool DeleteRiskAssessment(Guid id);
    IReadOnlyCollection<FamilyMember> GetFamilyMembers();
    FamilyMember AddFamilyMember(CreateFamilyMemberRequest request);
    IReadOnlyCollection<DocumentItem> GetDocuments();
    DocumentItem AddDocument(CreateDocumentRequest request);
    DocumentItem? UpdateDocument(Guid id, CreateDocumentRequest request);
    bool DeleteDocument(Guid id);
    IReadOnlyCollection<Medication> GetMedications();
    IReadOnlyCollection<MedicationAdministrationRecord> GetMedicationAdministrationRecords();
    IReadOnlyCollection<CareNote> GetCareNotes();
    CareNote AddCareNote(CreateCareNoteRequest request);
    IReadOnlyCollection<HealthObservation> GetHealthObservations();
    IReadOnlyCollection<Incident> GetIncidents();
    Incident AddIncident(CreateIncidentRequest request);
    Incident? UpdateIncident(Guid id, CreateIncidentRequest request);
    bool DeleteIncident(Guid id);
    IReadOnlyCollection<AiRiskAlert> GetAiRiskAlerts();
    IReadOnlyCollection<PayrollRun> GetPayrollRuns();
    PayrollRun GeneratePayrollRun();
    IReadOnlyCollection<Invoice> GetInvoices();
    IReadOnlyCollection<Invoice> GenerateInvoices();
    IReadOnlyCollection<ReportDefinition> GetReports();
    IReadOnlyCollection<ComplianceItem> GetComplianceItems();
    IReadOnlyCollection<UatChecklistItem> GetUatChecklist();
    IReadOnlyCollection<MessageThread> GetMessageThreads();
    MessageThread AddMessageThread(CreateMessageThreadRequest request);
    IReadOnlyCollection<NotificationItem> GetNotifications();
    IReadOnlyCollection<AdminUser> GetAdminUsers();
    AdminUser AddAdminUser(CreateAdminUserRequest request);
    AdminUser? UpdateUserRole(Guid id, UserRole role);
    IReadOnlyCollection<AuditEvent> GetAuditEvents();
    byte[] ExportPdf(string reportName);
}

public interface ITenantContext
{
    Guid OrganizationId { get; }
    Guid? BranchId { get; }
    bool IsPlatformOwner { get; }
    bool IsOrganizationWide { get; }
    bool CanAccess(Guid? organizationId, Guid? branchId);
}

public interface ICurrentUserContext
{
    Guid? UserId { get; }
    string UserName { get; }
    UserRole? Role { get; }
    bool IsAdministrator { get; }
    bool IsCareManager { get; }
    bool IsCareCoordinator { get; }
    bool IsCareWorker { get; }
    bool IsFamilyMember { get; }
    bool IsBackOffice { get; }
    Guid? CareWorkerId { get; }
    bool HasAnyRole(params UserRole[] roles);
}

public sealed record CreateServiceUserRequest(
    string FullName,
    DateOnly DateOfBirth,
    string PhoneNumber,
    string CareNeeds,
    string EmergencyContact,
    string PreferredCareWorker,
    string Address = "",
    string Allergies = "",
    string MedicalConditions = "",
    string FundingSource = "",
    string Gender = "",
    string PhotoUrl = "",
    string MobilityStatus = "",
    string CognitiveStatus = "",
    string CommunicationNeeds = "",
    string CulturalPreferences = "",
    string DietaryRequirements = "");

public sealed record UpsertPersonRecordRequest(
    string PreferredName,
    string Pronouns,
    string HealthIdentifier,
    string GpDetails,
    string PharmacyDetails,
    string LegalRepresentative,
    string ConsentStatus,
    string MentalCapacityStatus,
    string CommunicationPassport,
    string PersonalHistory,
    string WhatMattersToMe,
    string DesiredOutcomes,
    string AdvanceCareWishes,
    DateTimeOffset? AdmittedAt,
    DateTimeOffset? DischargedAt);

public sealed record CreateCareAssessmentRequest(
    Guid ServiceUserId,
    string AssessmentType,
    string TemplateVersion,
    string AnswersJson,
    int Score,
    RiskLevel Risk,
    string Summary,
    string RecommendedActions,
    string CompletedBy,
    DateTimeOffset ReviewDueAt);

public sealed record CreateCarePlanOutcomeRequest(
    Guid CarePlanId,
    Guid ServiceUserId,
    string Goal,
    string DesiredOutcome,
    string Interventions,
    string ResponsiblePerson,
    string Measure,
    DateTimeOffset TargetDate);

public sealed record CreateCareWorkerRequest(
    string FullName,
    string Specialization,
    string Availability,
    string DbsStatus = "Pending",
    string TrainingCompliance = "Not started",
    string TravelRadius = "10 miles");

public sealed record CreateVisitRequest(
    Guid ServiceUserId,
    Guid CareWorkerId,
    DateTimeOffset StartsAt,
    string VisitType,
    int DurationMinutes = 30,
    string RequiredSkills = "");

public sealed record VisitCheckInRequest(decimal Latitude, decimal Longitude);

public sealed record VisitCheckOutRequest(decimal Latitude, decimal Longitude);

public sealed record UpdateVisitStatusRequest(VisitStatus Status);

public sealed record CreateCarePlanRequest(
    Guid ServiceUserId,
    string PersonalCare,
    string MedicationSupport,
    string MobilityAndTransfers,
    string Nutrition,
    DateTimeOffset ReviewDueAt);

public sealed record CreateRiskAssessmentRequest(
    Guid ServiceUserId,
    string Category,
    RiskLevel Risk,
    string MitigationPlan,
    DateTimeOffset ReviewDueAt);

public sealed record CreateFamilyMemberRequest(
    Guid ServiceUserId,
    string FullName,
    string Email,
    string Relationship,
    string AccessLevel);

public sealed record CreateDocumentRequest(
    Guid ServiceUserId,
    string FileName,
    string Category,
    string StoragePath,
    string UploadedBy);

public sealed record CreateCareNoteRequest(
    Guid VisitId,
    Guid ServiceUserId,
    Guid CareWorkerId,
    string Summary,
    string PersonalCare,
    string MealsAndHydration,
    string Medication,
    string Concerns,
    bool RequiresReview);

public sealed record CreateIncidentRequest(
    Guid ServiceUserId,
    Guid? VisitId,
    string Category,
    string Severity,
    string Description);

public sealed record CreateMessageThreadRequest(
    Guid ServiceUserId,
    Guid CareWorkerId,
    string Subject,
    MessagePriority Priority,
    string LastMessage);

public sealed record UpdateUserRoleRequest(UserRole Role);

public sealed record CreateAdminUserRequest(
    string UserName,
    string Email,
    string Password,
    UserRole Role,
    Guid? OrganizationId = null,
    Guid? BranchId = null,
    Guid? CareWorkerId = null);
