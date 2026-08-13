namespace AiCare.Domain;

public sealed record ServiceUser(
    Guid Id,
    string FullName,
    DateOnly DateOfBirth,
    string PhoneNumber,
    string CareNeeds,
    string EmergencyContact,
    string PreferredCareWorker,
    RiskLevel Risk,
    string Status,
    string Address,
    string Allergies,
    string MedicalConditions,
    string FundingSource,
    string Gender,
    string PhotoUrl,
    string MobilityStatus,
    string CognitiveStatus,
    string CommunicationNeeds,
    string CulturalPreferences,
    string DietaryRequirements,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record PersonRecord(
    Guid Id,
    Guid ServiceUserId,
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
    DateTimeOffset? DischargedAt,
    DateTimeOffset LastReviewedAt,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record CareAssessment(
    Guid Id,
    Guid ServiceUserId,
    string AssessmentType,
    string TemplateVersion,
    string Status,
    string AnswersJson,
    int Score,
    RiskLevel Risk,
    string Summary,
    string RecommendedActions,
    string CompletedBy,
    DateTimeOffset CompletedAt,
    DateTimeOffset ReviewDueAt,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record CarePlanOutcome(
    Guid Id,
    Guid CarePlanId,
    Guid ServiceUserId,
    string Goal,
    string DesiredOutcome,
    string Interventions,
    string ResponsiblePerson,
    string Measure,
    string Status,
    DateTimeOffset TargetDate,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record CareWorker(
    Guid Id,
    string FullName,
    string Specialization,
    string Availability,
    int AssignedServiceUsers,
    int Utilization,
    string DbsStatus,
    string TrainingCompliance,
    string TravelRadius,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record Visit(
    Guid Id,
    Guid ServiceUserId,
    Guid CareWorkerId,
    DateTimeOffset StartsAt,
    string VisitType,
    int DurationMinutes,
    string RequiredSkills,
    VisitStatus Status,
    DateTimeOffset? CheckedInAt,
    DateTimeOffset? CheckedOutAt,
    decimal? CheckInLatitude,
    decimal? CheckInLongitude,
    decimal? CheckOutLatitude,
    decimal? CheckOutLongitude,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record CarePlan(
    Guid Id,
    Guid ServiceUserId,
    string Version,
    string Status,
    string PersonalCare,
    string MedicationSupport,
    string MobilityAndTransfers,
    string Nutrition,
    DateTimeOffset ReviewDueAt,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record RiskAssessment(
    Guid Id,
    Guid ServiceUserId,
    string Category,
    RiskLevel Risk,
    string MitigationPlan,
    DateTimeOffset ReviewDueAt,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record FamilyMember(
    Guid Id,
    Guid ServiceUserId,
    string FullName,
    string Email,
    string Relationship,
    string AccessLevel,
    string Status,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record DocumentItem(
    Guid Id,
    Guid ServiceUserId,
    string FileName,
    string Category,
    string StoragePath,
    string UploadedBy,
    DateTimeOffset UploadedAt,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record Medication(
    Guid Id,
    Guid ServiceUserId,
    string Name,
    string Dosage,
    string Route,
    string Schedule,
    bool IsPrn,
    string Pharmacy,
    string AllergyWarning,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record MedicationAdministrationRecord(
    Guid Id,
    Guid MedicationId,
    Guid VisitId,
    Guid CareWorkerId,
    DateTimeOffset ScheduledAt,
    DateTimeOffset? AdministeredAt,
    string Outcome,
    string Notes,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record CareNote(
    Guid Id,
    Guid VisitId,
    Guid ServiceUserId,
    Guid CareWorkerId,
    string Summary,
    string PersonalCare,
    string MealsAndHydration,
    string Medication,
    string Concerns,
    bool RequiresReview,
    DateTimeOffset CreatedAt,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record HealthObservation(
    Guid Id,
    Guid VisitId,
    Guid ServiceUserId,
    string ObservationType,
    string Value,
    string Unit,
    string Notes,
    DateTimeOffset RecordedAt,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record Incident(
    Guid Id,
    Guid ServiceUserId,
    Guid? VisitId,
    string Category,
    string Severity,
    string Description,
    string Status,
    DateTimeOffset ReportedAt,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record AiRiskAlert(
    Guid Id,
    Guid ServiceUserId,
    string Signal,
    RiskLevel Risk,
    string Evidence,
    string RecommendedAction,
    bool HumanReviewed,
    DateTimeOffset CreatedAt,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record PayrollRun(
    Guid Id,
    string Period,
    int WorkerCount,
    decimal GrossPay,
    string Status,
    DateTimeOffset CreatedAt,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record Invoice(
    Guid Id,
    Guid ServiceUserId,
    string Funder,
    decimal Amount,
    string Status,
    DateTimeOffset IssuedAt,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record ReportDefinition(
    Guid Id,
    string Name,
    string Category,
    string ExportFormats,
    string Schedule,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record ComplianceItem(
    Guid Id,
    string Area,
    string Requirement,
    string Status,
    DateTimeOffset DueAt,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record UatChecklistItem(
    Guid Id,
    string Journey,
    string Scenario,
    string Status,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record MessageThread(
    Guid Id,
    Guid ServiceUserId,
    Guid CareWorkerId,
    string Subject,
    MessagePriority Priority,
    string LastMessage,
    DateTimeOffset ResponseDueAt,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record NotificationItem(
    Guid Id,
    string Title,
    string Detail,
    DateTimeOffset CreatedAt,
    bool IsRead,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record AdminUser(
    Guid Id,
    string FullName,
    string Email,
    UserRole Role,
    string Status,
    Guid? OrganizationId = null,
    Guid? BranchId = null,
    Guid? CareWorkerId = null,
    Guid? FamilyMemberId = null);

public sealed record AuditEvent(
    Guid Id,
    string Action,
    string Actor,
    string EntityType,
    Guid? EntityId,
    DateTimeOffset CreatedAt,
    Guid? OrganizationId = null,
    Guid? BranchId = null);

public sealed record Organization(
    Guid Id,
    string Name,
    string Plan,
    string Status);

public sealed record Branch(
    Guid Id,
    Guid OrganizationId,
    string Name,
    string Region,
    string Status);
