using AiCare.Application;
using AiCare.Domain;
using System.Globalization;

namespace AiCare.Infrastructure;

public sealed class InMemoryCareRepository : ICareRepository
{
    private readonly List<ServiceUser> _serviceUsers;
    private readonly List<CareWorker> _careWorkers;
    private readonly List<Visit> _visits;
    private readonly List<CarePlan> _carePlans;
    private readonly List<RiskAssessment> _riskAssessments;
    private readonly List<FamilyMember> _familyMembers;
    private readonly List<DocumentItem> _documents;
    private readonly List<Medication> _medications;
    private readonly List<MedicationAdministrationRecord> _mar;
    private readonly List<CareNote> _careNotes;
    private readonly List<HealthObservation> _observations;
    private readonly List<Incident> _incidents;
    private readonly List<AiRiskAlert> _aiRiskAlerts;
    private readonly List<PayrollRun> _payrollRuns;
    private readonly List<Invoice> _invoices;
    private readonly List<ReportDefinition> _reports;
    private readonly List<ComplianceItem> _complianceItems;
    private readonly List<UatChecklistItem> _uatChecklist;
    private readonly List<MessageThread> _messages;
    private readonly List<NotificationItem> _notifications;
    private readonly List<AdminUser> _adminUsers;
    private readonly List<AuditEvent> _auditEvents = [];

    public InMemoryCareRepository()
    {
        var ayeshaId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var hamzaId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var saraId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var omarId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        _careWorkers =
        [
            new(saraId, "Sara Malik", "Dementia and medication support", "Mon-Wed, 07:00-15:00", 18, 82, "Valid until 2027-05-12", "92% complete", "8 miles"),
            new(omarId, "Omar Shah", "Mobility support and reablement", "Tue-Fri, 10:00-18:00", 21, 76, "Valid until 2027-02-08", "88% complete", "12 miles")
        ];

        _serviceUsers =
        [
            new(ayeshaId, "Ayesha Khan", new DateOnly(1946, 4, 12), "+92 300 1111111", "Personal care, meal prompts, medication reminders", "Ali Khan", "Sara Malik", RiskLevel.High, "Falls review due", "Flat 5, Maple View Apartments, Karachi", "None", "Type 2 diabetes; reduced mobility", "Private", "Female", "", "Walking aid", "Mild dementia", "Prefers Urdu and short instructions", "Halal meals; family prayer routine", "Soft diet"),
            new(hamzaId, "Hamza Ali", new DateOnly(1951, 8, 2), "+92 300 2222222", "Post-discharge reablement and wound observation", "Nadia Ali", "Omar Shah", RiskLevel.Medium, "Stable", "House 12, Garden Avenue, Lahore", "Penicillin", "Post-op recovery", "Local authority", "Male", "", "Independent with supervision", "No known impairment", "Speaks Punjabi and Urdu", "Male worker preferred for personal care", "High protein meals")
        ];

        _visits =
        [
            new(Guid.NewGuid(), ayeshaId, saraId, DateTimeOffset.Now.Date.AddHours(9).AddMinutes(30), "Domiciliary morning care", 45, "Medication support; dementia care", VisitStatus.InProgress, DateTimeOffset.Now.AddMinutes(-12), null, 24.8607m, 67.0011m, null, null),
            new(Guid.NewGuid(), hamzaId, omarId, DateTimeOffset.Now.Date.AddHours(10).AddMinutes(15), "Reablement visit", 60, "Moving and handling", VisitStatus.Scheduled, null, null, null, null, null, null)
        ];
        var ayeshaVisitId = _visits[0].Id;
        var hamzaVisitId = _visits[1].Id;

        _carePlans =
        [
            new(Guid.NewGuid(), ayeshaId, "v2", "Awaiting family approval", "Prompt washing, dressing, and continence care.", "Prompt tablets at breakfast and record refusals.", "Use walking frame and supervise transfers.", "Soft halal breakfast and 1.5L fluid target.", DateTimeOffset.Now.AddDays(14)),
            new(Guid.NewGuid(), hamzaId, "v1", "Active", "Support showering twice weekly.", "Medication self-managed; observe side effects.", "Short indoor walks after lunch.", "High protein meals while wound heals.", DateTimeOffset.Now.AddDays(30))
        ];

        _riskAssessments =
        [
            new(Guid.NewGuid(), ayeshaId, "Falls", RiskLevel.High, "Keep walking frame within reach; review night lighting.", DateTimeOffset.Now.AddDays(7)),
            new(Guid.NewGuid(), hamzaId, "Skin integrity", RiskLevel.Medium, "Photograph wound weekly with consent and escalate redness.", DateTimeOffset.Now.AddDays(10))
        ];

        _familyMembers =
        [
            new(Guid.NewGuid(), ayeshaId, "Ali Khan", "ali.family@aicare.local", "Son", "Approve", "Active"),
            new(Guid.NewGuid(), hamzaId, "Nadia Ali", "nadia.family@aicare.local", "Daughter", "Communicate", "Invited")
        ];

        _documents =
        [
            new(Guid.NewGuid(), ayeshaId, "Ayesha care plan v2.pdf", "Care plan", "/documents/demo/ayesha-care-plan-v2.pdf", "Rida Hassan", DateTimeOffset.Now.AddDays(-2)),
            new(Guid.NewGuid(), hamzaId, "DBS certificate Omar.pdf", "Compliance", "/documents/demo/omar-dbs.pdf", "Admin User", DateTimeOffset.Now.AddDays(-8))
        ];

        _medications =
        [
            new(Guid.NewGuid(), ayeshaId, "Metformin", "500mg", "Oral", "08:00, 20:00", false, "City Pharmacy", "Check nausea or dizziness"),
            new(Guid.NewGuid(), hamzaId, "Paracetamol", "500mg", "Oral", "PRN up to 4 times daily", true, "Garden Pharmacy", "Avoid overdose")
        ];

        _mar =
        [
            new(Guid.NewGuid(), _medications[0].Id, ayeshaVisitId, saraId, DateTimeOffset.Now.Date.AddHours(9), DateTimeOffset.Now.Date.AddHours(9).AddMinutes(8), "Administered", "Taken with breakfast"),
            new(Guid.NewGuid(), _medications[1].Id, hamzaVisitId, omarId, DateTimeOffset.Now.Date.AddHours(11), null, "Scheduled", "PRN only if pain reported")
        ];

        _careNotes =
        [
            new(Guid.NewGuid(), ayeshaVisitId, ayeshaId, saraId, "Morning personal care completed; transfer was unsteady.", "Washed and dressed with one-person support.", "Ate soft breakfast and drank tea.", "Metformin administered.", "Falls risk indicator repeated.", true, DateTimeOffset.Now.AddMinutes(-5)),
            new(Guid.NewGuid(), hamzaVisitId, hamzaId, omarId, "Reablement visit prepared.", "Shower support planned.", "High-protein lunch reminder.", "No MAR due unless PRN requested.", "Wound review due today.", false, DateTimeOffset.Now.AddMinutes(-30))
        ];

        _observations =
        [
            new(Guid.NewGuid(), ayeshaVisitId, ayeshaId, "Mood", "Anxious", "", "Settled after breakfast.", DateTimeOffset.Now.AddMinutes(-6)),
            new(Guid.NewGuid(), hamzaVisitId, hamzaId, "Pain", "4", "/10", "Improved since yesterday.", DateTimeOffset.Now.AddMinutes(-35))
        ];

        _incidents =
        [
            new(Guid.NewGuid(), ayeshaId, ayeshaVisitId, "Falls concern", "Medium", "Unsteady transfer, no fall occurred.", "Manager review", DateTimeOffset.Now.AddMinutes(-4))
        ];

        _aiRiskAlerts =
        [
            new(Guid.NewGuid(), ayeshaId, "Repeated falls language in care notes", RiskLevel.High, "Unsteady transfer mentioned in 3 notes this week.", "Review falls assessment and night lighting.", false, DateTimeOffset.Now.AddMinutes(-3)),
            new(Guid.NewGuid(), hamzaId, "Skin integrity follow-up", RiskLevel.Medium, "Wound photo and redness trend require review.", "Schedule nurse review if redness increases.", false, DateTimeOffset.Now.AddHours(-1))
        ];

        _payrollRuns =
        [
            new(Guid.NewGuid(), "2026-W32", 24, 18420.50m, "Draft", DateTimeOffset.Now.AddHours(-4))
        ];

        _invoices =
        [
            new(Guid.NewGuid(), ayeshaId, "Private", 540.00m, "Ready", DateTimeOffset.Now.AddDays(-1)),
            new(Guid.NewGuid(), hamzaId, "Local authority", 720.00m, "Generated", DateTimeOffset.Now.AddDays(-1))
        ];

        _reports =
        [
            new(Guid.NewGuid(), "Visit completion report", "Operational", "PDF, CSV, Excel", "Weekly"),
            new(Guid.NewGuid(), "CQC evidence pack", "Regulatory", "PDF, Excel", "Monthly"),
            new(Guid.NewGuid(), "Payroll export", "Financial", "CSV", "Fortnightly")
        ];

        _complianceItems =
        [
            new(Guid.NewGuid(), "Security", "MFA for all staff", "Planned", DateTimeOffset.Now.AddDays(30)),
            new(Guid.NewGuid(), "Backups", "Restore test evidence", "Due", DateTimeOffset.Now.AddDays(14)),
            new(Guid.NewGuid(), "Data protection", "Retention and deletion workflow", "In progress", DateTimeOffset.Now.AddDays(21))
        ];

        _uatChecklist =
        [
            new(Guid.NewGuid(), "Care Worker", "Check in, view plan, record note, check out", "Ready for pilot"),
            new(Guid.NewGuid(), "Coordinator", "Register service user, plan care, schedule visit", "Ready for pilot"),
            new(Guid.NewGuid(), "Family Member", "Accept invite, view timeline, message team", "Scaffolded"),
            new(Guid.NewGuid(), "Care Manager", "Review AI risks, incidents, compliance dashboard", "Scaffolded")
        ];

        _messages =
        [
            new(Guid.NewGuid(), ayeshaId, saraId, "Falls risk concern", MessagePriority.High, "Care worker noted unsteady transfer after breakfast.", DateTimeOffset.Now.AddMinutes(8)),
            new(Guid.NewGuid(), hamzaId, omarId, "Wound observation update", MessagePriority.Medium, "Photo logged and pain level improved from 7 to 4.", DateTimeOffset.Now.AddMinutes(34))
        ];

        _notifications =
        [
            new(Guid.NewGuid(), "Visit checked in", "Sara Malik checked in for Ayesha Khan's morning care.", DateTimeOffset.Now.AddMinutes(-4), false),
            new(Guid.NewGuid(), "Risk alert", "PASSgenius flagged repeated falls indicators for manager review.", DateTimeOffset.Now.AddMinutes(-11), false)
        ];

        _adminUsers =
        [
            new(saraId, "Sara Malik", "sara@digitalcare.local", UserRole.CareWorker, "Active"),
            new(omarId, "Omar Shah", "omar@digitalcare.local", UserRole.CareWorker, "Active"),
            new(Guid.NewGuid(), "Rida Hassan", "rida@digitalcare.local", UserRole.CareCoordinator, "Invited"),
            new(Guid.NewGuid(), "Admin User", "admin@digitalcare.local", UserRole.Administrator, "Active")
        ];
    }

    public IReadOnlyCollection<ServiceUser> GetServiceUsers() => _serviceUsers;

    public ServiceUser? GetServiceUser(Guid id) => _serviceUsers.FirstOrDefault(serviceUser => serviceUser.Id == id);

    public ServiceUser AddServiceUser(CreateServiceUserRequest request)
    {
        var serviceUser = new ServiceUser(
            Guid.NewGuid(),
            request.FullName,
            request.DateOfBirth,
            request.PhoneNumber,
            request.CareNeeds,
            request.EmergencyContact,
            request.PreferredCareWorker,
            RiskLevel.Medium,
            "Onboarded",
            request.Address,
            request.Allergies,
            request.MedicalConditions,
            request.FundingSource,
            request.Gender,
            request.PhotoUrl,
            request.MobilityStatus,
            request.CognitiveStatus,
            request.CommunicationNeeds,
            request.CulturalPreferences,
            request.DietaryRequirements);
        _serviceUsers.Add(serviceUser);
        AddAudit("service_user.onboarded", "system", nameof(ServiceUser), serviceUser.Id);
        return serviceUser;
    }

    public ServiceUser? UpdateServiceUser(Guid id, CreateServiceUserRequest request)
    {
        var index = _serviceUsers.FindIndex(serviceUser => serviceUser.Id == id);
        if (index < 0)
        {
            return null;
        }

        _serviceUsers[index] = _serviceUsers[index] with
        {
            FullName = request.FullName,
            DateOfBirth = request.DateOfBirth,
            PhoneNumber = request.PhoneNumber,
            CareNeeds = request.CareNeeds,
            EmergencyContact = request.EmergencyContact,
            PreferredCareWorker = request.PreferredCareWorker,
            Address = request.Address,
            Allergies = request.Allergies,
            MedicalConditions = request.MedicalConditions,
            FundingSource = request.FundingSource,
            Gender = request.Gender,
            PhotoUrl = request.PhotoUrl,
            MobilityStatus = request.MobilityStatus,
            CognitiveStatus = request.CognitiveStatus,
            CommunicationNeeds = request.CommunicationNeeds,
            CulturalPreferences = request.CulturalPreferences,
            DietaryRequirements = request.DietaryRequirements
        };
        AddAudit("service_user.updated", "system", nameof(ServiceUser), id);
        return _serviceUsers[index];
    }

    public IReadOnlyCollection<CareWorker> GetCareWorkers() => _careWorkers;

    public CareWorker AddCareWorker(CreateCareWorkerRequest request)
    {
        var careWorker = new CareWorker(Guid.NewGuid(), request.FullName, request.Specialization, request.Availability, 0, 0, request.DbsStatus, request.TrainingCompliance, request.TravelRadius);
        _careWorkers.Add(careWorker);
        AddAudit("care_worker.added", "system", nameof(CareWorker), careWorker.Id);
        return careWorker;
    }

    public CareWorker? UpdateCareWorker(Guid id, CreateCareWorkerRequest request)
    {
        var index = _careWorkers.FindIndex(worker => worker.Id == id);
        if (index < 0)
        {
            return null;
        }

        _careWorkers[index] = _careWorkers[index] with
        {
            FullName = request.FullName,
            Specialization = request.Specialization,
            Availability = request.Availability,
            DbsStatus = request.DbsStatus,
            TrainingCompliance = request.TrainingCompliance,
            TravelRadius = request.TravelRadius
        };
        AddAudit("care_worker.updated", "system", nameof(CareWorker), id);
        return _careWorkers[index];
    }

    public IReadOnlyCollection<Visit> GetVisits() => _visits;

    public Visit AddVisit(CreateVisitRequest request)
    {
        var visit = new Visit(Guid.NewGuid(), request.ServiceUserId, request.CareWorkerId, request.StartsAt, request.VisitType, request.DurationMinutes, request.RequiredSkills, VisitStatus.Scheduled, null, null, null, null, null, null);
        _visits.Add(visit);
        AddAudit("visit.scheduled", "system", nameof(Visit), visit.Id);
        return visit;
    }

    public Visit? UpdateVisit(Guid id, CreateVisitRequest request)
    {
        var index = _visits.FindIndex(visit => visit.Id == id);
        if (index < 0)
        {
            return null;
        }

        _visits[index] = _visits[index] with
        {
            ServiceUserId = request.ServiceUserId,
            CareWorkerId = request.CareWorkerId,
            StartsAt = request.StartsAt,
            VisitType = request.VisitType,
            DurationMinutes = request.DurationMinutes,
            RequiredSkills = request.RequiredSkills
        };
        AddAudit("visit.updated", "system", nameof(Visit), id);
        return _visits[index];
    }

    public Visit? UpdateVisitStatus(Guid id, VisitStatus status)
    {
        var index = _visits.FindIndex(visit => visit.Id == id);
        if (index < 0)
        {
            return null;
        }

        _visits[index] = _visits[index] with { Status = status };
        AddAudit("visit.status_updated", "system", nameof(Visit), id);
        return _visits[index];
    }

    public Visit? CheckInVisit(Guid id, VisitCheckInRequest request)
    {
        var index = _visits.FindIndex(visit => visit.Id == id);
        if (index < 0)
        {
            return null;
        }

        _visits[index] = _visits[index] with
        {
            Status = VisitStatus.InProgress,
            CheckedInAt = DateTimeOffset.Now,
            CheckInLatitude = request.Latitude,
            CheckInLongitude = request.Longitude
        };
        AddAudit("visit.checked_in", "system", nameof(Visit), id);
        return _visits[index];
    }

    public Visit? CheckOutVisit(Guid id, VisitCheckOutRequest request)
    {
        var index = _visits.FindIndex(visit => visit.Id == id);
        if (index < 0)
        {
            return null;
        }

        _visits[index] = _visits[index] with
        {
            Status = VisitStatus.Completed,
            CheckedOutAt = DateTimeOffset.Now,
            CheckOutLatitude = request.Latitude,
            CheckOutLongitude = request.Longitude
        };
        AddAudit("visit.checked_out", "system", nameof(Visit), id);
        return _visits[index];
    }

    public IReadOnlyCollection<CarePlan> GetCarePlans() => _carePlans;

    public CarePlan AddCarePlan(CreateCarePlanRequest request)
    {
        var carePlan = new CarePlan(Guid.NewGuid(), request.ServiceUserId, "v1", "Draft", request.PersonalCare, request.MedicationSupport, request.MobilityAndTransfers, request.Nutrition, request.ReviewDueAt);
        _carePlans.Insert(0, carePlan);
        AddAudit("care_plan.created", "system", nameof(CarePlan), carePlan.Id);
        return carePlan;
    }

    public CarePlan? UpdateCarePlan(Guid id, CreateCarePlanRequest request)
    {
        var index = _carePlans.FindIndex(carePlan => carePlan.Id == id);
        if (index < 0)
        {
            return null;
        }

        _carePlans[index] = _carePlans[index] with
        {
            ServiceUserId = request.ServiceUserId,
            Status = "Updated",
            PersonalCare = request.PersonalCare,
            MedicationSupport = request.MedicationSupport,
            MobilityAndTransfers = request.MobilityAndTransfers,
            Nutrition = request.Nutrition,
            ReviewDueAt = request.ReviewDueAt
        };
        AddAudit("care_plan.updated", "system", nameof(CarePlan), id);
        return _carePlans[index];
    }

    public bool DeleteCarePlan(Guid id)
    {
        var removed = _carePlans.RemoveAll(carePlan => carePlan.Id == id) > 0;
        if (removed)
        {
            AddAudit("care_plan.deleted", "system", nameof(CarePlan), id);
        }
        return removed;
    }

    public IReadOnlyCollection<RiskAssessment> GetRiskAssessments() => _riskAssessments;

    public RiskAssessment AddRiskAssessment(CreateRiskAssessmentRequest request)
    {
        var risk = new RiskAssessment(Guid.NewGuid(), request.ServiceUserId, request.Category, request.Risk, request.MitigationPlan, request.ReviewDueAt);
        _riskAssessments.Insert(0, risk);
        AddAudit("risk_assessment.created", "system", nameof(RiskAssessment), risk.Id);
        return risk;
    }

    public RiskAssessment? UpdateRiskAssessment(Guid id, CreateRiskAssessmentRequest request)
    {
        var index = _riskAssessments.FindIndex(risk => risk.Id == id);
        if (index < 0)
        {
            return null;
        }

        _riskAssessments[index] = _riskAssessments[index] with
        {
            ServiceUserId = request.ServiceUserId,
            Category = request.Category,
            Risk = request.Risk,
            MitigationPlan = request.MitigationPlan,
            ReviewDueAt = request.ReviewDueAt
        };
        AddAudit("risk_assessment.updated", "system", nameof(RiskAssessment), id);
        return _riskAssessments[index];
    }

    public bool DeleteRiskAssessment(Guid id)
    {
        var removed = _riskAssessments.RemoveAll(risk => risk.Id == id) > 0;
        if (removed)
        {
            AddAudit("risk_assessment.deleted", "system", nameof(RiskAssessment), id);
        }
        return removed;
    }

    public IReadOnlyCollection<FamilyMember> GetFamilyMembers() => _familyMembers;

    public FamilyMember AddFamilyMember(CreateFamilyMemberRequest request)
    {
        var family = new FamilyMember(Guid.NewGuid(), request.ServiceUserId, request.FullName, request.Email, request.Relationship, request.AccessLevel, "Invited");
        _familyMembers.Insert(0, family);
        AddAudit("family_member.invited", "system", nameof(FamilyMember), family.Id);
        return family;
    }

    public IReadOnlyCollection<DocumentItem> GetDocuments() => _documents;

    public DocumentItem AddDocument(CreateDocumentRequest request)
    {
        var document = new DocumentItem(Guid.NewGuid(), request.ServiceUserId, request.FileName, request.Category, request.StoragePath, request.UploadedBy, DateTimeOffset.Now);
        _documents.Insert(0, document);
        AddAudit("document.uploaded", "system", nameof(DocumentItem), document.Id);
        return document;
    }

    public DocumentItem? UpdateDocument(Guid id, CreateDocumentRequest request)
    {
        var index = _documents.FindIndex(document => document.Id == id);
        if (index < 0)
        {
            return null;
        }

        _documents[index] = _documents[index] with
        {
            ServiceUserId = request.ServiceUserId,
            FileName = request.FileName,
            Category = request.Category,
            StoragePath = request.StoragePath,
            UploadedBy = request.UploadedBy
        };
        AddAudit("document.updated", "system", nameof(DocumentItem), id);
        return _documents[index];
    }

    public bool DeleteDocument(Guid id)
    {
        var removed = _documents.RemoveAll(document => document.Id == id) > 0;
        if (removed)
        {
            AddAudit("document.deleted", "system", nameof(DocumentItem), id);
        }
        return removed;
    }

    public IReadOnlyCollection<Medication> GetMedications() => _medications;

    public IReadOnlyCollection<MedicationAdministrationRecord> GetMedicationAdministrationRecords() => _mar;

    public IReadOnlyCollection<CareNote> GetCareNotes() => _careNotes;

    public CareNote AddCareNote(CreateCareNoteRequest request)
    {
        var note = new CareNote(Guid.NewGuid(), request.VisitId, request.ServiceUserId, request.CareWorkerId, request.Summary, request.PersonalCare, request.MealsAndHydration, request.Medication, request.Concerns, request.RequiresReview, DateTimeOffset.Now);
        _careNotes.Insert(0, note);
        AddAudit("care_note.created", "system", nameof(CareNote), note.Id);
        return note;
    }

    public IReadOnlyCollection<HealthObservation> GetHealthObservations() => _observations;

    public IReadOnlyCollection<Incident> GetIncidents() => _incidents;

    public Incident AddIncident(CreateIncidentRequest request)
    {
        var incident = new Incident(Guid.NewGuid(), request.ServiceUserId, request.VisitId, request.Category, request.Severity, request.Description, "Reported", DateTimeOffset.Now);
        _incidents.Insert(0, incident);
        AddAudit("incident.reported", "system", nameof(Incident), incident.Id);
        return incident;
    }

    public Incident? UpdateIncident(Guid id, CreateIncidentRequest request)
    {
        var index = _incidents.FindIndex(incident => incident.Id == id);
        if (index < 0)
        {
            return null;
        }

        _incidents[index] = _incidents[index] with
        {
            ServiceUserId = request.ServiceUserId,
            VisitId = request.VisitId,
            Category = request.Category,
            Severity = request.Severity,
            Description = request.Description,
            Status = "Updated"
        };
        AddAudit("incident.updated", "system", nameof(Incident), id);
        return _incidents[index];
    }

    public bool DeleteIncident(Guid id)
    {
        var removed = _incidents.RemoveAll(incident => incident.Id == id) > 0;
        if (removed)
        {
            AddAudit("incident.deleted", "system", nameof(Incident), id);
        }
        return removed;
    }

    public IReadOnlyCollection<AiRiskAlert> GetAiRiskAlerts() => _aiRiskAlerts;

    public IReadOnlyCollection<PayrollRun> GetPayrollRuns() => _payrollRuns;

    public PayrollRun GeneratePayrollRun()
    {
        var completedVisits = _visits.Count(visit => visit.Status == VisitStatus.Completed);
        var payroll = new PayrollRun(Guid.NewGuid(), $"{DateTimeOffset.Now:yyyy}-W{ISOWeek.GetWeekOfYear(DateTimeOffset.Now.DateTime):00}", _careWorkers.Count, completedVisits * 42.50m, "Generated", DateTimeOffset.Now);
        _payrollRuns.Insert(0, payroll);
        AddAudit("payroll.generated", "system", nameof(PayrollRun), payroll.Id);
        return payroll;
    }

    public IReadOnlyCollection<Invoice> GetInvoices() => _invoices;

    public IReadOnlyCollection<Invoice> GenerateInvoices()
    {
        var generated = _serviceUsers.Select(serviceUser => new Invoice(Guid.NewGuid(), serviceUser.Id, serviceUser.FundingSource, 360.00m, "Generated", DateTimeOffset.Now)).ToList();
        _invoices.InsertRange(0, generated);
        AddAudit("invoices.generated", "system", nameof(Invoice), null);
        return generated;
    }

    public IReadOnlyCollection<ReportDefinition> GetReports() => _reports;

    public IReadOnlyCollection<ComplianceItem> GetComplianceItems() => _complianceItems;

    public IReadOnlyCollection<UatChecklistItem> GetUatChecklist() => _uatChecklist;

    public IReadOnlyCollection<MessageThread> GetMessageThreads() => _messages;

    public MessageThread AddMessageThread(CreateMessageThreadRequest request)
    {
        var thread = new MessageThread(Guid.NewGuid(), request.ServiceUserId, request.CareWorkerId, request.Subject, request.Priority, request.LastMessage, DateTimeOffset.Now.AddMinutes(30));
        _messages.Add(thread);
        AddAudit("message.thread_created", "system", nameof(MessageThread), thread.Id);
        return thread;
    }

    public IReadOnlyCollection<NotificationItem> GetNotifications() => _notifications;

    public IReadOnlyCollection<AdminUser> GetAdminUsers() => _adminUsers;

    public AdminUser AddAdminUser(CreateAdminUserRequest request)
    {
        if (_adminUsers.Any(user => user.Email.Equals(request.Email, StringComparison.OrdinalIgnoreCase) || user.FullName.Equals(request.UserName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("A user with that username or email already exists.");
        }

        var user = new AdminUser(Guid.NewGuid(), request.UserName, request.Email, request.Role, "Active", TenantDefaults.OrganizationId, TenantDefaults.BranchId, request.CareWorkerId, request.FamilyMemberId);
        _adminUsers.Add(user);
        AddAudit("admin.user_created", "system", nameof(AdminUser), user.Id);
        return user;
    }

    public AdminUser? UpdateUserRole(Guid id, UserRole role)
    {
        var index = _adminUsers.FindIndex(user => user.Id == id);
        if (index < 0)
        {
            return null;
        }

        _adminUsers[index] = _adminUsers[index] with { Role = role };
        AddAudit("admin.role_updated", "system", nameof(AdminUser), id);
        return _adminUsers[index];
    }

    public IReadOnlyCollection<AuditEvent> GetAuditEvents() => _auditEvents;

    public byte[] ExportPdf(string reportName)
    {
        var text = $"AiCare {reportName} PDF export\nGenerated: {DateTimeOffset.Now:u}\nService users: {_serviceUsers.Count}\nVisits: {_visits.Count}\nIncidents: {_incidents.Count}\n";
        return CreateSimplePdf(text);
    }

    private void AddAudit(string action, string actor, string entityType, Guid? entityId)
    {
        _auditEvents.Add(new AuditEvent(Guid.NewGuid(), action, actor, entityType, entityId, DateTimeOffset.Now));
    }

    private static byte[] CreateSimplePdf(string text)
    {
        static string Escape(string value) => value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)").Replace("\r", "");
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var content = "BT /F1 12 Tf 50 760 Td " + string.Join(" T* ", lines.Select(line => $"({Escape(line)})")) + " ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"
        };
        var builder = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(System.Text.Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }
        var xref = System.Text.Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
        {
            builder.Append(offset.ToString("0000000000")).Append(" 00000 n \n");
        }
        builder.Append("trailer << /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return System.Text.Encoding.ASCII.GetBytes(builder.ToString());
    }
}
