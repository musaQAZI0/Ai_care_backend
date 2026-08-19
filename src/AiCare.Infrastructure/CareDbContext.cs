using AiCare.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Infrastructure;

public sealed class CareDbContext : DbContext
{
    public CareDbContext(DbContextOptions<CareDbContext> options)
        : base(options)
    {
    }

    public DbSet<ServiceUser> ServiceUsers => Set<ServiceUser>();
    public DbSet<PersonRecord> PersonRecords => Set<PersonRecord>();
    public DbSet<CareAssessment> CareAssessments => Set<CareAssessment>();
    public DbSet<CarePlanOutcome> CarePlanOutcomes => Set<CarePlanOutcome>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<CareWorker> CareWorkers => Set<CareWorker>();
    public DbSet<Visit> Visits => Set<Visit>();
    public DbSet<CarePlan> CarePlans => Set<CarePlan>();
    public DbSet<RiskAssessment> RiskAssessments => Set<RiskAssessment>();
    public DbSet<FamilyMember> FamilyMembers => Set<FamilyMember>();
    public DbSet<DocumentItem> Documents => Set<DocumentItem>();
    public DbSet<Medication> Medications => Set<Medication>();
    public DbSet<MedicationAdministrationRecord> MedicationAdministrationRecords => Set<MedicationAdministrationRecord>();
    public DbSet<CareNote> CareNotes => Set<CareNote>();
    public DbSet<HealthObservation> HealthObservations => Set<HealthObservation>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<AiRiskAlert> AiRiskAlerts => Set<AiRiskAlert>();
    public DbSet<PayrollRun> PayrollRuns => Set<PayrollRun>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<ReportDefinition> Reports => Set<ReportDefinition>();
    public DbSet<ComplianceItem> ComplianceItems => Set<ComplianceItem>();
    public DbSet<UatChecklistItem> UatChecklist => Set<UatChecklistItem>();
    public DbSet<MessageThread> MessageThreads => Set<MessageThread>();
    public DbSet<NotificationItem> Notifications => Set<NotificationItem>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<RetentionPolicy> RetentionPolicies => Set<RetentionPolicy>();
    public DbSet<DataGovernanceRequest> DataGovernanceRequests => Set<DataGovernanceRequest>();

    public override int SaveChanges()
    {
        EnforceImmutableAuditEvents();
        return base.SaveChanges();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceImmutableAuditEvents();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        EnforceImmutableAuditEvents();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceImmutableAuditEvents();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void EnforceImmutableAuditEvents()
    {
        var forbidden = ChangeTracker.Entries<AuditEvent>()
            .FirstOrDefault(entry => entry.State is EntityState.Modified or EntityState.Deleted);
        if (forbidden is not null)
        {
            throw new InvalidOperationException("Audit events are immutable and cannot be modified or deleted.");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ServiceUser>(builder =>
        {
            builder.HasKey(serviceUser => serviceUser.Id);
            builder.HasIndex(serviceUser => serviceUser.OrganizationId);
            builder.HasIndex(serviceUser => new { serviceUser.OrganizationId, serviceUser.BranchId });
            builder.Property(serviceUser => serviceUser.OrganizationId).IsRequired();
            builder.Property(serviceUser => serviceUser.BranchId).IsRequired();
            builder.Property(serviceUser => serviceUser.FullName).IsRequired();
            builder.Property(serviceUser => serviceUser.PhoneNumber).IsRequired();
            builder.Property(serviceUser => serviceUser.CareNeeds).IsRequired();
            builder.Property(serviceUser => serviceUser.PreferredCareWorker).IsRequired();
            builder.Property(serviceUser => serviceUser.Risk).HasConversion<string>().IsRequired();
            builder.Property(serviceUser => serviceUser.Status).IsRequired();
            builder.Property(serviceUser => serviceUser.Address).IsRequired(false);
            builder.Property(serviceUser => serviceUser.Allergies).IsRequired(false);
            builder.Property(serviceUser => serviceUser.MedicalConditions).IsRequired(false);
            builder.Property(serviceUser => serviceUser.FundingSource).IsRequired(false);
            builder.Property(serviceUser => serviceUser.Gender).IsRequired(false);
            builder.Property(serviceUser => serviceUser.PhotoUrl).IsRequired(false);
            builder.Property(serviceUser => serviceUser.MobilityStatus).IsRequired(false);
            builder.Property(serviceUser => serviceUser.CognitiveStatus).IsRequired(false);
            builder.Property(serviceUser => serviceUser.CommunicationNeeds).IsRequired(false);
            builder.Property(serviceUser => serviceUser.CulturalPreferences).IsRequired(false);
            builder.Property(serviceUser => serviceUser.DietaryRequirements).IsRequired(false);
        });

        modelBuilder.Entity<PersonRecord>(builder =>
        {
            builder.HasKey(record => record.Id);
            builder.HasIndex(record => new { record.OrganizationId, record.BranchId, record.ServiceUserId }).IsUnique();
            builder.Property(record => record.WhatMattersToMe).IsRequired(false);
            builder.Property(record => record.DesiredOutcomes).IsRequired(false);
        });

        modelBuilder.Entity<CareAssessment>(builder =>
        {
            builder.HasKey(assessment => assessment.Id);
            builder.HasIndex(assessment => new { assessment.OrganizationId, assessment.BranchId, assessment.ServiceUserId });
            builder.Property(assessment => assessment.AssessmentType).IsRequired();
            builder.Property(assessment => assessment.AnswersJson).HasColumnType("jsonb").IsRequired();
            builder.Property(assessment => assessment.Risk).HasConversion<string>().IsRequired();
        });

        modelBuilder.Entity<CarePlanOutcome>(builder =>
        {
            builder.HasKey(outcome => outcome.Id);
            builder.HasIndex(outcome => new { outcome.OrganizationId, outcome.BranchId, outcome.ServiceUserId });
            builder.Property(outcome => outcome.Goal).IsRequired();
            builder.Property(outcome => outcome.DesiredOutcome).IsRequired();
        });

        modelBuilder.Entity<CareWorker>(builder =>
        {
            builder.HasKey(careWorker => careWorker.Id);
            builder.HasIndex(careWorker => careWorker.OrganizationId);
            builder.HasIndex(careWorker => new { careWorker.OrganizationId, careWorker.BranchId });
            builder.Property(careWorker => careWorker.OrganizationId).IsRequired();
            builder.Property(careWorker => careWorker.BranchId).IsRequired();
            builder.Property(careWorker => careWorker.FullName).IsRequired();
            builder.Property(careWorker => careWorker.Specialization).IsRequired();
            builder.Property(careWorker => careWorker.Availability).IsRequired();
            builder.Property(careWorker => careWorker.DbsStatus).IsRequired();
            builder.Property(careWorker => careWorker.TrainingCompliance).IsRequired();
            builder.Property(careWorker => careWorker.TravelRadius).IsRequired();
        });

        modelBuilder.Entity<Visit>(builder =>
        {
            builder.HasKey(visit => visit.Id);
            builder.HasIndex(visit => visit.OrganizationId);
            builder.HasIndex(visit => new { visit.OrganizationId, visit.BranchId });
            builder.HasIndex(visit => new { visit.OrganizationId, visit.ServiceUserId });
            builder.Property(visit => visit.OrganizationId).IsRequired();
            builder.Property(visit => visit.BranchId).IsRequired();
            builder.Property(visit => visit.VisitType).IsRequired();
            builder.Property(visit => visit.RequiredSkills).IsRequired(false);
            builder.Property(visit => visit.Status).HasConversion<string>().IsRequired();
        });

        modelBuilder.Entity<CarePlan>(builder =>
        {
            builder.HasKey(carePlan => carePlan.Id);
            builder.HasIndex(carePlan => new { carePlan.OrganizationId, carePlan.BranchId });
            builder.Property(carePlan => carePlan.Version).IsRequired();
            builder.Property(carePlan => carePlan.Status).IsRequired();
        });

        modelBuilder.Entity<RiskAssessment>(builder =>
        {
            builder.HasKey(risk => risk.Id);
            builder.HasIndex(risk => new { risk.OrganizationId, risk.BranchId });
            builder.Property(risk => risk.Category).IsRequired();
            builder.Property(risk => risk.Risk).HasConversion<string>().IsRequired();
        });

        modelBuilder.Entity<FamilyMember>(builder =>
        {
            builder.HasKey(family => family.Id);
            builder.Property(family => family.FullName).IsRequired();
            builder.Property(family => family.Email).IsRequired();
            builder.Property(family => family.AccessLevel).IsRequired();
        });

        modelBuilder.Entity<DocumentItem>(builder =>
        {
            builder.HasKey(document => document.Id);
            builder.HasIndex(document => new { document.OrganizationId, document.BranchId });
            builder.Property(document => document.FileName).IsRequired();
            builder.Property(document => document.Category).IsRequired();
            builder.Property(document => document.StoragePath).IsRequired();
        });

        modelBuilder.Entity<Medication>(builder =>
        {
            builder.HasKey(medication => medication.Id);
            builder.Property(medication => medication.Name).IsRequired();
            builder.Property(medication => medication.Dosage).IsRequired();
            builder.Property(medication => medication.Schedule).IsRequired();
        });

        modelBuilder.Entity<MedicationAdministrationRecord>(builder =>
        {
            builder.HasKey(record => record.Id);
            builder.Property(record => record.Outcome).IsRequired();
        });

        modelBuilder.Entity<CareNote>(builder =>
        {
            builder.HasKey(note => note.Id);
            builder.HasIndex(note => new { note.OrganizationId, note.BranchId });
            builder.HasIndex(note => new { note.OrganizationId, note.ServiceUserId });
            builder.Property(note => note.Summary).IsRequired();
        });

        modelBuilder.Entity<HealthObservation>(builder =>
        {
            builder.HasKey(observation => observation.Id);
            builder.Property(observation => observation.ObservationType).IsRequired();
            builder.Property(observation => observation.Value).IsRequired();
        });

        modelBuilder.Entity<Incident>(builder =>
        {
            builder.HasKey(incident => incident.Id);
            builder.HasIndex(incident => new { incident.OrganizationId, incident.BranchId });
            builder.Property(incident => incident.Category).IsRequired();
            builder.Property(incident => incident.Severity).IsRequired();
            builder.Property(incident => incident.Status).IsRequired();
        });

        modelBuilder.Entity<AiRiskAlert>(builder =>
        {
            builder.HasKey(alert => alert.Id);
            builder.Property(alert => alert.Signal).IsRequired();
            builder.Property(alert => alert.Risk).HasConversion<string>().IsRequired();
        });

        modelBuilder.Entity<PayrollRun>(builder =>
        {
            builder.HasKey(payroll => payroll.Id);
            builder.Property(payroll => payroll.Period).IsRequired();
            builder.Property(payroll => payroll.Status).IsRequired();
        });

        modelBuilder.Entity<Invoice>(builder =>
        {
            builder.HasKey(invoice => invoice.Id);
            builder.Property(invoice => invoice.Funder).IsRequired();
            builder.Property(invoice => invoice.Status).IsRequired();
        });

        modelBuilder.Entity<ReportDefinition>(builder =>
        {
            builder.HasKey(report => report.Id);
            builder.Property(report => report.Name).IsRequired();
            builder.Property(report => report.Category).IsRequired();
        });

        modelBuilder.Entity<ComplianceItem>(builder =>
        {
            builder.HasKey(item => item.Id);
            builder.Property(item => item.Area).IsRequired();
            builder.Property(item => item.Requirement).IsRequired();
            builder.Property(item => item.Status).IsRequired();
        });

        modelBuilder.Entity<UatChecklistItem>(builder =>
        {
            builder.HasKey(item => item.Id);
            builder.Property(item => item.Journey).IsRequired();
            builder.Property(item => item.Scenario).IsRequired();
            builder.Property(item => item.Status).IsRequired();
        });

        modelBuilder.Entity<MessageThread>(builder =>
        {
            builder.HasKey(message => message.Id);
            builder.Property(message => message.Subject).IsRequired();
            builder.Property(message => message.Priority).HasConversion<string>().IsRequired();
            builder.Property(message => message.LastMessage).IsRequired();
        });

        modelBuilder.Entity<NotificationItem>(builder =>
        {
            builder.HasKey(notification => notification.Id);
            builder.Property(notification => notification.Title).IsRequired();
            builder.Property(notification => notification.Detail).IsRequired();
        });

        modelBuilder.Entity<AdminUser>(builder =>
        {
            builder.HasKey(user => user.Id);
            builder.Property(user => user.FullName).IsRequired();
            builder.Property(user => user.Email).IsRequired();
            builder.Property(user => user.Role).HasConversion<string>().IsRequired();
            builder.Property(user => user.Status).IsRequired();
        });

        modelBuilder.Entity<AppUser>(builder =>
        {
            builder.HasKey(user => user.Id);
            builder.HasIndex(user => user.OrganizationId);
            builder.HasIndex(user => new { user.OrganizationId, user.BranchId });
            builder.Property(user => user.OrganizationId).IsRequired();
            builder.Property(user => user.BranchId).IsRequired(false);
            builder.Property(user => user.UserName).IsRequired();
            builder.Property(user => user.Email).IsRequired();
            builder.Property(user => user.PasswordHash).IsRequired();
            builder.Property(user => user.Role).HasConversion<string>().IsRequired();
            builder.Property(user => user.IsActive).IsRequired();
            builder.Property(user => user.CareWorkerId).IsRequired(false);
            builder.Property(user => user.FamilyMemberId).IsRequired(false);
            builder.HasData(new AppUser(
                Guid.Parse("99999999-9999-9999-9999-999999999999"),
                "admin",
                "admin@aicare.local",
                "pbkdf2$210000$16$QWlDYXJlU2VlZFNhbHQwMQ==$b+nMBoR9kN4AUCjzZxYCfMBhIRK5uSsdpEgykuK9AWs=",
                UserRole.Administrator,
                true,
                TenantDefaults.OrganizationId,
                null));
        });

        modelBuilder.Entity<Organization>(builder =>
        {
            builder.HasKey(organization => organization.Id);
            builder.Property(organization => organization.Name).IsRequired();
            builder.Property(organization => organization.Plan).IsRequired();
            builder.Property(organization => organization.Status).IsRequired();
            builder.HasData(new Organization(TenantDefaults.OrganizationId, "AiCare Default Organization", "Enterprise", "Active"));
        });

        modelBuilder.Entity<Branch>(builder =>
        {
            builder.HasKey(branch => branch.Id);
            builder.Property(branch => branch.OrganizationId).IsRequired();
            builder.Property(branch => branch.Name).IsRequired();
            builder.Property(branch => branch.Region).IsRequired();
            builder.Property(branch => branch.Status).IsRequired();
            builder.HasData(new Branch(TenantDefaults.BranchId, TenantDefaults.OrganizationId, "Main Branch", "Primary", "Active"));
        });

        modelBuilder.Entity<AuditEvent>(builder =>
        {
            builder.HasKey(audit => audit.Id);
            builder.Property(audit => audit.Action).IsRequired();
            builder.Property(audit => audit.Actor).IsRequired();
            builder.Property(audit => audit.EntityType).IsRequired();
        });

        modelBuilder.Entity<RetentionPolicy>(builder =>
        {
            builder.HasKey(policy => policy.Id);
            builder.HasIndex(policy => new { policy.OrganizationId, policy.DataCategory }).IsUnique();
            builder.Property(policy => policy.DataCategory).IsRequired();
            builder.Property(policy => policy.LegalBasis).IsRequired();
            builder.Property(policy => policy.DispositionAction).IsRequired();
            builder.Property(policy => policy.OrganizationId).IsRequired();
        });

        modelBuilder.Entity<DataGovernanceRequest>(builder =>
        {
            builder.HasKey(request => request.Id);
            builder.HasIndex(request => new { request.OrganizationId, request.ServiceUserId, request.RequestedAt });
            builder.Property(request => request.RequestType).IsRequired();
            builder.Property(request => request.Status).IsRequired();
            builder.Property(request => request.RequestedBy).IsRequired();
            builder.Property(request => request.Reason).IsRequired();
            builder.Property(request => request.OrganizationId).IsRequired();
        });

        base.OnModelCreating(modelBuilder);
    }
}
