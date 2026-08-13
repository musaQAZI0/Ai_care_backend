using AiCare.Application;
using AiCare.Domain;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text;

namespace AiCare.Infrastructure;

public sealed class EfCoreCareRepository : ICareRepository
{
    private readonly CareDbContext _context;
    private readonly ITenantContext _tenant;
    private readonly ICurrentUserContext _currentUser;

    public EfCoreCareRepository(CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser)
    {
        _context = context;
        _tenant = tenant;
        _currentUser = currentUser;
    }

    public IReadOnlyCollection<ServiceUser> GetServiceUsers() => Visible(_context.ServiceUsers).ToList();

    public ServiceUser? GetServiceUser(Guid id)
    {
        var serviceUser = _context.ServiceUsers.Find(id);
        return serviceUser is not null && IsVisible(serviceUser.OrganizationId, serviceUser.BranchId) ? serviceUser : null;
    }

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
            request.DietaryRequirements,
            _tenant.OrganizationId,
            _tenant.BranchId ?? TenantDefaults.BranchId);

        _context.ServiceUsers.Add(serviceUser);
        AddAudit("service_user.onboarded", "system", nameof(ServiceUser), serviceUser.Id);
        _context.SaveChanges();
        return serviceUser;
    }

    public ServiceUser? UpdateServiceUser(Guid id, CreateServiceUserRequest request)
    {
        var serviceUser = _context.ServiceUsers.Find(id);
        if (serviceUser is null || !IsVisible(serviceUser.OrganizationId, serviceUser.BranchId))
        {
            return null;
        }

        var updated = serviceUser with
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
        _context.ServiceUsers.Update(updated);
        AddAudit("service_user.updated", "system", nameof(ServiceUser), id);
        _context.SaveChanges();
        return updated;
    }

    public IReadOnlyCollection<CareWorker> GetCareWorkers() => Visible(_context.CareWorkers).ToList();

    public CareWorker AddCareWorker(CreateCareWorkerRequest request)
    {
        var careWorker = new CareWorker(Guid.NewGuid(), request.FullName, request.Specialization, request.Availability, 0, 0, request.DbsStatus, request.TrainingCompliance, request.TravelRadius, _tenant.OrganizationId, _tenant.BranchId ?? TenantDefaults.BranchId);
        _context.CareWorkers.Add(careWorker);
        AddAudit("care_worker.added", "system", nameof(CareWorker), careWorker.Id);
        _context.SaveChanges();
        return careWorker;
    }

    public CareWorker? UpdateCareWorker(Guid id, CreateCareWorkerRequest request)
    {
        var careWorker = _context.CareWorkers.Find(id);
        if (careWorker is null || !IsVisible(careWorker.OrganizationId, careWorker.BranchId))
        {
            return null;
        }

        var updated = careWorker with
        {
            FullName = request.FullName,
            Specialization = request.Specialization,
            Availability = request.Availability,
            DbsStatus = request.DbsStatus,
            TrainingCompliance = request.TrainingCompliance,
            TravelRadius = request.TravelRadius
        };
        _context.CareWorkers.Update(updated);
        AddAudit("care_worker.updated", "system", nameof(CareWorker), id);
        _context.SaveChanges();
        return updated;
    }

    public IReadOnlyCollection<Visit> GetVisits() => Visible(_context.Visits).ToList();

    public Visit AddVisit(CreateVisitRequest request)
    {
        var visit = new Visit(
            Guid.NewGuid(),
            request.ServiceUserId,
            request.CareWorkerId,
            request.StartsAt,
            request.VisitType,
            request.DurationMinutes,
            request.RequiredSkills,
            VisitStatus.Scheduled,
            null,
            null,
            null,
            null,
            null,
            null,
            _tenant.OrganizationId,
            _tenant.BranchId ?? TenantDefaults.BranchId);

        _context.Visits.Add(visit);
        AddAudit("visit.scheduled", "system", nameof(Visit), visit.Id);
        _context.SaveChanges();
        return visit;
    }

    public Visit? UpdateVisit(Guid id, CreateVisitRequest request)
    {
        var visit = _context.Visits.Find(id);
        if (visit is null || !IsVisible(visit.OrganizationId, visit.BranchId))
        {
            return null;
        }

        var updated = visit with
        {
            ServiceUserId = request.ServiceUserId,
            CareWorkerId = request.CareWorkerId,
            StartsAt = request.StartsAt,
            VisitType = request.VisitType,
            DurationMinutes = request.DurationMinutes,
            RequiredSkills = request.RequiredSkills
        };
        _context.Visits.Update(updated);
        AddAudit("visit.updated", "system", nameof(Visit), id);
        _context.SaveChanges();
        return updated;
    }

    public Visit? UpdateVisitStatus(Guid id, VisitStatus status)
    {
        var visit = _context.Visits.Find(id);
        if (visit is null || !IsVisible(visit.OrganizationId, visit.BranchId))
        {
            return null;
        }

        visit = visit with { Status = status };
        _context.Visits.Update(visit);
        AddAudit("visit.status_updated", "system", nameof(Visit), id);
        _context.SaveChanges();
        return visit;
    }

    public Visit? CheckInVisit(Guid id, VisitCheckInRequest request)
    {
        var visit = _context.Visits.Find(id);
        if (visit is null || !IsVisible(visit.OrganizationId, visit.BranchId))
        {
            return null;
        }

        var updated = visit with
        {
            Status = VisitStatus.InProgress,
            CheckedInAt = DateTimeOffset.Now,
            CheckInLatitude = request.Latitude,
            CheckInLongitude = request.Longitude
        };
        _context.Visits.Update(updated);
        AddAudit("visit.checked_in", "system", nameof(Visit), id);
        _context.SaveChanges();
        return updated;
    }

    public Visit? CheckOutVisit(Guid id, VisitCheckOutRequest request)
    {
        var visit = _context.Visits.Find(id);
        if (visit is null || !IsVisible(visit.OrganizationId, visit.BranchId))
        {
            return null;
        }

        var updated = visit with
        {
            Status = VisitStatus.Completed,
            CheckedOutAt = DateTimeOffset.Now,
            CheckOutLatitude = request.Latitude,
            CheckOutLongitude = request.Longitude
        };
        _context.Visits.Update(updated);
        AddAudit("visit.checked_out", "system", nameof(Visit), id);
        _context.SaveChanges();
        return updated;
    }

    public IReadOnlyCollection<CarePlan> GetCarePlans() => Visible(_context.CarePlans).ToList();

    public CarePlan AddCarePlan(CreateCarePlanRequest request)
    {
        var carePlan = new CarePlan(Guid.NewGuid(), request.ServiceUserId, "v1", "Draft", request.PersonalCare, request.MedicationSupport, request.MobilityAndTransfers, request.Nutrition, request.ReviewDueAt, _tenant.OrganizationId, _tenant.BranchId ?? TenantDefaults.BranchId);
        _context.CarePlans.Add(carePlan);
        AddAudit("care_plan.created", "system", nameof(CarePlan), carePlan.Id);
        _context.SaveChanges();
        return carePlan;
    }

    public CarePlan? UpdateCarePlan(Guid id, CreateCarePlanRequest request)
    {
        var carePlan = _context.CarePlans.Find(id);
        if (carePlan is null || !IsVisible(carePlan.OrganizationId, carePlan.BranchId))
        {
            return null;
        }

        var updated = carePlan with
        {
            ServiceUserId = request.ServiceUserId,
            Status = "Updated",
            PersonalCare = request.PersonalCare,
            MedicationSupport = request.MedicationSupport,
            MobilityAndTransfers = request.MobilityAndTransfers,
            Nutrition = request.Nutrition,
            ReviewDueAt = request.ReviewDueAt
        };
        _context.CarePlans.Update(updated);
        AddAudit("care_plan.updated", "system", nameof(CarePlan), id);
        _context.SaveChanges();
        return updated;
    }

    public bool DeleteCarePlan(Guid id)
    {
        var carePlan = _context.CarePlans.Find(id);
        if (carePlan is null || !IsVisible(carePlan.OrganizationId, carePlan.BranchId))
        {
            return false;
        }

        _context.CarePlans.Remove(carePlan);
        AddAudit("care_plan.deleted", "system", nameof(CarePlan), id);
        _context.SaveChanges();
        return true;
    }

    public IReadOnlyCollection<RiskAssessment> GetRiskAssessments() => Visible(_context.RiskAssessments).ToList();

    public RiskAssessment AddRiskAssessment(CreateRiskAssessmentRequest request)
    {
        var risk = new RiskAssessment(Guid.NewGuid(), request.ServiceUserId, request.Category, request.Risk, request.MitigationPlan, request.ReviewDueAt, _tenant.OrganizationId, _tenant.BranchId ?? TenantDefaults.BranchId);
        _context.RiskAssessments.Add(risk);
        AddAudit("risk_assessment.created", "system", nameof(RiskAssessment), risk.Id);
        _context.SaveChanges();
        return risk;
    }

    public RiskAssessment? UpdateRiskAssessment(Guid id, CreateRiskAssessmentRequest request)
    {
        var risk = _context.RiskAssessments.Find(id);
        if (risk is null || !IsVisible(risk.OrganizationId, risk.BranchId))
        {
            return null;
        }

        var updated = risk with
        {
            ServiceUserId = request.ServiceUserId,
            Category = request.Category,
            Risk = request.Risk,
            MitigationPlan = request.MitigationPlan,
            ReviewDueAt = request.ReviewDueAt
        };
        _context.RiskAssessments.Update(updated);
        AddAudit("risk_assessment.updated", "system", nameof(RiskAssessment), id);
        _context.SaveChanges();
        return updated;
    }

    public bool DeleteRiskAssessment(Guid id)
    {
        var risk = _context.RiskAssessments.Find(id);
        if (risk is null || !IsVisible(risk.OrganizationId, risk.BranchId))
        {
            return false;
        }

        _context.RiskAssessments.Remove(risk);
        AddAudit("risk_assessment.deleted", "system", nameof(RiskAssessment), id);
        _context.SaveChanges();
        return true;
    }

    public IReadOnlyCollection<FamilyMember> GetFamilyMembers() => Visible(_context.FamilyMembers).ToList();

    public FamilyMember AddFamilyMember(CreateFamilyMemberRequest request)
    {
        var family = new FamilyMember(Guid.NewGuid(), request.ServiceUserId, request.FullName, request.Email, request.Relationship, request.AccessLevel, "Invited", _tenant.OrganizationId, _tenant.BranchId ?? TenantDefaults.BranchId);
        _context.FamilyMembers.Add(family);
        AddAudit("family_member.invited", "system", nameof(FamilyMember), family.Id);
        _context.SaveChanges();
        return family;
    }

    public IReadOnlyCollection<DocumentItem> GetDocuments() => Visible(_context.Documents).ToList();

    public DocumentItem AddDocument(CreateDocumentRequest request)
    {
        var document = new DocumentItem(Guid.NewGuid(), request.ServiceUserId, request.FileName, request.Category, request.StoragePath, request.UploadedBy, DateTimeOffset.Now, _tenant.OrganizationId, _tenant.BranchId ?? TenantDefaults.BranchId);
        _context.Documents.Add(document);
        AddAudit("document.uploaded", "system", nameof(DocumentItem), document.Id);
        _context.SaveChanges();
        return document;
    }

    public DocumentItem? UpdateDocument(Guid id, CreateDocumentRequest request)
    {
        var document = _context.Documents.Find(id);
        if (document is null || !IsVisible(document.OrganizationId, document.BranchId))
        {
            return null;
        }

        var updated = document with
        {
            ServiceUserId = request.ServiceUserId,
            FileName = request.FileName,
            Category = request.Category,
            StoragePath = request.StoragePath,
            UploadedBy = request.UploadedBy
        };
        _context.Documents.Update(updated);
        AddAudit("document.updated", "system", nameof(DocumentItem), id);
        _context.SaveChanges();
        return updated;
    }

    public bool DeleteDocument(Guid id)
    {
        var document = _context.Documents.Find(id);
        if (document is null || !IsVisible(document.OrganizationId, document.BranchId))
        {
            return false;
        }

        _context.Documents.Remove(document);
        AddAudit("document.deleted", "system", nameof(DocumentItem), id);
        _context.SaveChanges();
        return true;
    }

    public IReadOnlyCollection<Medication> GetMedications() => Visible(_context.Medications).ToList();

    public IReadOnlyCollection<MedicationAdministrationRecord> GetMedicationAdministrationRecords() => Visible(_context.MedicationAdministrationRecords).ToList();

    public IReadOnlyCollection<CareNote> GetCareNotes() => Visible(_context.CareNotes).ToList();

    public CareNote AddCareNote(CreateCareNoteRequest request)
    {
        var note = new CareNote(Guid.NewGuid(), request.VisitId, request.ServiceUserId, request.CareWorkerId, request.Summary, request.PersonalCare, request.MealsAndHydration, request.Medication, request.Concerns, request.RequiresReview, DateTimeOffset.Now, _tenant.OrganizationId, _tenant.BranchId ?? TenantDefaults.BranchId);
        _context.CareNotes.Add(note);
        AddAudit("care_note.created", "system", nameof(CareNote), note.Id);
        _context.SaveChanges();
        return note;
    }

    public IReadOnlyCollection<HealthObservation> GetHealthObservations() => Visible(_context.HealthObservations).ToList();

    public IReadOnlyCollection<Incident> GetIncidents() => Visible(_context.Incidents).ToList();

    public Incident AddIncident(CreateIncidentRequest request)
    {
        var incident = new Incident(Guid.NewGuid(), request.ServiceUserId, request.VisitId, request.Category, request.Severity, request.Description, "Reported", DateTimeOffset.Now, _tenant.OrganizationId, _tenant.BranchId ?? TenantDefaults.BranchId);
        _context.Incidents.Add(incident);
        AddAudit("incident.reported", "system", nameof(Incident), incident.Id);
        _context.SaveChanges();
        return incident;
    }

    public Incident? UpdateIncident(Guid id, CreateIncidentRequest request)
    {
        var incident = _context.Incidents.Find(id);
        if (incident is null || !IsVisible(incident.OrganizationId, incident.BranchId))
        {
            return null;
        }

        var updated = incident with
        {
            ServiceUserId = request.ServiceUserId,
            VisitId = request.VisitId,
            Category = request.Category,
            Severity = request.Severity,
            Description = request.Description,
            Status = "Updated"
        };
        _context.Incidents.Update(updated);
        AddAudit("incident.updated", "system", nameof(Incident), id);
        _context.SaveChanges();
        return updated;
    }

    public bool DeleteIncident(Guid id)
    {
        var incident = _context.Incidents.Find(id);
        if (incident is null || !IsVisible(incident.OrganizationId, incident.BranchId))
        {
            return false;
        }

        _context.Incidents.Remove(incident);
        AddAudit("incident.deleted", "system", nameof(Incident), id);
        _context.SaveChanges();
        return true;
    }

    public IReadOnlyCollection<AiRiskAlert> GetAiRiskAlerts() => Visible(_context.AiRiskAlerts).ToList();

    public IReadOnlyCollection<PayrollRun> GetPayrollRuns() => Visible(_context.PayrollRuns).ToList();

    public PayrollRun GeneratePayrollRun()
    {
        var now = DateTimeOffset.UtcNow;
        var completedVisits = Visible(_context.Visits).Count(visit => visit.Status == VisitStatus.Completed);
        var payroll = new PayrollRun(Guid.NewGuid(), $"{now:yyyy}-W{ISOWeek.GetWeekOfYear(now.DateTime):00}", Visible(_context.CareWorkers).Count(), completedVisits * 42.50m, "Generated", now, _tenant.OrganizationId, _tenant.BranchId ?? TenantDefaults.BranchId);
        _context.PayrollRuns.Add(payroll);
        AddAudit("payroll.generated", "system", nameof(PayrollRun), payroll.Id);
        _context.SaveChanges();
        return payroll;
    }

    public IReadOnlyCollection<Invoice> GetInvoices() => Visible(_context.Invoices).ToList();

    public IReadOnlyCollection<Invoice> GenerateInvoices()
    {
        var now = DateTimeOffset.UtcNow;
        var generated = Visible(_context.ServiceUsers)
            .Select(serviceUser => new Invoice(Guid.NewGuid(), serviceUser.Id, serviceUser.FundingSource, 360.00m, "Generated", now, serviceUser.OrganizationId, serviceUser.BranchId))
            .ToList();
        _context.Invoices.AddRange(generated);
        AddAudit("invoices.generated", "system", nameof(Invoice), null);
        _context.SaveChanges();
        return generated;
    }

    public IReadOnlyCollection<ReportDefinition> GetReports() => Visible(_context.Reports).ToList();

    public IReadOnlyCollection<ComplianceItem> GetComplianceItems() => Visible(_context.ComplianceItems).ToList();

    public IReadOnlyCollection<UatChecklistItem> GetUatChecklist() => Visible(_context.UatChecklist).ToList();

    public IReadOnlyCollection<MessageThread> GetMessageThreads() => Visible(_context.MessageThreads).ToList();

    public MessageThread AddMessageThread(CreateMessageThreadRequest request)
    {
        var thread = new MessageThread(
            Guid.NewGuid(),
            request.ServiceUserId,
            request.CareWorkerId,
            request.Subject,
            request.Priority,
            request.LastMessage,
            DateTimeOffset.Now.AddMinutes(30),
            _tenant.OrganizationId,
            _tenant.BranchId ?? TenantDefaults.BranchId);

        _context.MessageThreads.Add(thread);
        AddAudit("message.thread_created", "system", nameof(MessageThread), thread.Id);
        _context.SaveChanges();
        return thread;
    }

    public IReadOnlyCollection<NotificationItem> GetNotifications() => Visible(_context.Notifications).ToList();

    public IReadOnlyCollection<AdminUser> GetAdminUsers() => _context.AppUsers
        .AsNoTracking()
        .Where(user => _tenant.IsPlatformOwner || user.OrganizationId == _tenant.OrganizationId && (_tenant.IsOrganizationWide || _tenant.BranchId == null || user.BranchId == null || user.BranchId == _tenant.BranchId))
        .Select(user => new AdminUser(user.Id, user.UserName, user.Email, user.Role, user.IsActive ? "Active" : "Suspended", user.OrganizationId, user.BranchId, user.CareWorkerId, user.FamilyMemberId))
        .ToList();

    public AdminUser AddAdminUser(CreateAdminUserRequest request)
    {
        var normalizedUserName = request.UserName.Trim();
        var normalizedEmail = request.Email.Trim();
        if (_context.AppUsers.Any(user => user.UserName == normalizedUserName || user.Email == normalizedEmail))
        {
            throw new InvalidOperationException("A user with that username or email already exists.");
        }

        var organizationId = request.OrganizationId ?? _tenant.OrganizationId;
        var branchId = request.BranchId;
        if (!_tenant.IsPlatformOwner && organizationId != _tenant.OrganizationId)
        {
            throw new InvalidOperationException("You cannot create users outside your organization.");
        }

        if (!_tenant.IsOrganizationWide && _tenant.BranchId is not null && branchId != _tenant.BranchId)
        {
            throw new InvalidOperationException("You cannot create users outside your branch.");
        }

        var user = new AppUser(
            Guid.NewGuid(),
            normalizedUserName,
            normalizedEmail,
            PasswordHasher.HashPassword(request.Password),
            request.Role,
            true,
            organizationId,
            branchId,
            request.CareWorkerId,
            request.FamilyMemberId);

        _context.AppUsers.Add(user);
        AddAudit("admin.user_created", "system", nameof(AppUser), user.Id);
        _context.SaveChanges();
        return new AdminUser(user.Id, user.UserName, user.Email, user.Role, "Active", user.OrganizationId, user.BranchId, user.CareWorkerId, user.FamilyMemberId);
    }

    public AdminUser? UpdateUserRole(Guid id, UserRole role)
    {
        var user = _context.AppUsers.Find(id);
        if (user is null || !IsVisible(user.OrganizationId, user.BranchId))
        {
            return null;
        }

        var updated = user with { Role = role };
        _context.AppUsers.Update(updated);
        AddAudit("admin.role_updated", "system", nameof(AppUser), id);
        _context.SaveChanges();
        return new AdminUser(updated.Id, updated.UserName, updated.Email, updated.Role, updated.IsActive ? "Active" : "Suspended", updated.OrganizationId, updated.BranchId, updated.CareWorkerId, updated.FamilyMemberId);
    }

    public IReadOnlyCollection<AuditEvent> GetAuditEvents() => Visible(_context.AuditEvents).ToList();

    public byte[] ExportPdf(string reportName)
    {
        var text = $"AiCare {reportName} PDF export\nGenerated: {DateTimeOffset.Now:u}\nService users: {Visible(_context.ServiceUsers).Count()}\nVisits: {Visible(_context.Visits).Count()}\nIncidents: {Visible(_context.Incidents).Count()}\n";
        return CreateSimplePdf(text);
    }

    private void AddAudit(string action, string actor, string entityType, Guid? entityId)
    {
        var resolvedActor = string.IsNullOrWhiteSpace(_currentUser.UserName) ? actor : _currentUser.UserName;
        _context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), action, resolvedActor, entityType, entityId, DateTimeOffset.UtcNow, _tenant.OrganizationId, _tenant.BranchId ?? TenantDefaults.BranchId));
        _context.SaveChanges();
    }

    private IQueryable<T> Visible<T>(DbSet<T> set)
        where T : class
    {
        var organizationId = _tenant.OrganizationId;
        var branchId = _tenant.BranchId;

        var query = set.AsNoTracking();
        if (_tenant.IsPlatformOwner)
        {
            return query;
        }

        query = query.Where(item => EF.Property<Guid?>(item, "OrganizationId") == null || EF.Property<Guid?>(item, "OrganizationId") == organizationId);
        if (_tenant.IsOrganizationWide || branchId is null)
        {
            return query;
        }

        return query.Where(item => EF.Property<Guid?>(item, "BranchId") == null || EF.Property<Guid?>(item, "BranchId") == branchId);
    }

    private bool IsVisible(Guid? organizationId, Guid? branchId) => _tenant.CanAccess(organizationId, branchId);

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
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
        {
            builder.Append(offset.ToString("0000000000")).Append(" 00000 n \n");
        }
        builder.Append("trailer << /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
