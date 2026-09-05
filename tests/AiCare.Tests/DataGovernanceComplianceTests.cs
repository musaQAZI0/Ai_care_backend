using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiCare.Tests;

public sealed class DataGovernanceComplianceTests : IClassFixture<AiCareApiFactory>
{
    private readonly AiCareApiFactory _factory;

    public DataGovernanceComplianceTests(AiCareApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AdministratorCanCreateRetentionPolicyAndAuditIsRecorded()
    {
        var client = _factory.CreateClient();
        await Login(client, "admin", "AdminPassword123!");

        var response = await client.PutAsJsonAsync("/api/phase1/data-governance/retention-policies/CareRecords", new
        {
            retentionDays = 2920,
            legalBasis = "Care record retention schedule",
            dispositionAction = "Review",
            isActive = true,
            reviewDueAt = DateTimeOffset.UtcNow.AddYears(1)
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Contains(db.RetentionPolicies, x => x.OrganizationId == TenantDefaults.OrganizationId && x.DataCategory == "CareRecords");
        Assert.Contains(db.AuditEvents, x => x.Action == "governance.retention_policy_updated");
    }

    [Fact]
    public async Task SubjectExportIsTenantScopedAndAudited()
    {
        var client = _factory.CreateClient();
        await Login(client, "admin", "AdminPassword123!");

        var response = await client.GetAsync($"/api/phase1/data-governance/service-users/{TestIds.ServiceUserId}/export");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Test Service User", body);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Contains(db.DataGovernanceRequests, x => x.ServiceUserId == TestIds.ServiceUserId && x.RequestType == "SubjectAccessExport" && x.Status == "Completed");
        Assert.Contains(db.AuditEvents, x => x.Action == "governance.subject_exported" && x.EntityId == TestIds.ServiceUserId);
    }

    [Fact]
    public async Task ActiveServiceUserCannotBeAnonymized()
    {
        var client = _factory.CreateClient();
        await Login(client, "admin", "AdminPassword123!");

        var response = await client.PostAsJsonAsync($"/api/phase1/data-governance/service-users/{TestIds.ServiceUserId}/anonymize", new
        {
            confirmation = "ANONYMIZE",
            reason = "Retention period completed"
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal("Test Service User", db.ServiceUsers.Single(x => x.Id == TestIds.ServiceUserId).FullName);
    }

    [Fact]
    public async Task ServiceUserDeleteArchivesInsteadOfHardDeleting()
    {
        var personId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.ServiceUsers.Add(new ServiceUser(personId, "Archive Test Person", new DateOnly(1970, 1, 1), "+10000001000", "Closed package", "Archive contact", "Archive Worker", RiskLevel.Low, "Discharged", "Archive address", "None", "None", "Private", "Not specified", "", "Independent", "None", "None", "None", "None", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        await Login(client, "admin", "AdminPassword123!");

        var response = await client.DeleteAsync($"/api/phase1/service-users/{personId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        var archived = verifyDb.ServiceUsers.Single(x => x.Id == personId);
        Assert.Equal("Archived", archived.Status);
        Assert.Contains(verifyDb.AuditEvents, x => x.Action == "service_user.archived" && x.EntityId == personId);
    }


    [Fact]
    public async Task VisitDeleteCancelsInsteadOfHardDeleting()
    {
        var client = _factory.CreateClient();
        await Login(client, "admin", "AdminPassword123!");

        var response = await client.DeleteAsync($"/api/phase1/visits/{TestIds.VisitId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var verify = _factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal(VisitStatus.Cancelled, db.Visits.Single(x => x.Id == TestIds.VisitId).Status);
        Assert.Contains(db.AuditEvents, x => x.Action == "visit.cancelled" && x.EntityId == TestIds.VisitId);
    }

    [Fact]
    public async Task CompletedVisitDeleteRequiresCorrectionInsteadOfDeleting()
    {
        var visitId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Visits.Add(new Visit(visitId, TestIds.ServiceUserId, TestIds.WorkerId, DateTimeOffset.UtcNow.AddDays(-1), "Completed archive test", 30, "Personal care", VisitStatus.Completed, null, null, null, null, null, null, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        await Login(client, "admin", "AdminPassword123!");

        var response = await client.DeleteAsync($"/api/phase1/visits/{visitId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal(VisitStatus.Completed, verifyDb.Visits.Single(x => x.Id == visitId).Status);
    }

    [Fact]
    public async Task FamilyMemberDeleteRevokesInsteadOfHardDeleting()
    {
        var familyId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var setupDb = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            setupDb.FamilyMembers.Add(new FamilyMember(familyId, TestIds.ServiceUserId, "Delete Governance Family", "delete.governance.family@test.local", "Daughter", "Read", "Active", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await setupDb.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        await Login(client, "admin", "AdminPassword123!");

        var response = await client.DeleteAsync($"/api/phase1/family-members/{familyId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var verify = _factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal("Revoked", db.FamilyMembers.Single(x => x.Id == familyId).Status);
        Assert.Contains(db.AuditEvents, x => x.Action == "family_member.revoked" && x.EntityId == familyId);
    }


    [Fact]
    public async Task CareWorkerDeleteArchivesAndDeactivatesLinkedLogin()
    {
        var workerId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var setupDb = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            setupDb.CareWorkers.Add(new CareWorker(workerId, "Archive Worker", "Care", "Flexible", 2, 50, "Valid", "Compliant", "10 miles", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            setupDb.AppUsers.Add(new AppUser(userId, $"archive.worker.{workerId:N}", $"archive.worker.{workerId:N}@test.local", PasswordHasher.HashPassword("AdminPassword123!"), UserRole.CareWorker, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId, workerId, null));
            await setupDb.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        await Login(client, "admin", "AdminPassword123!");

        var response = await client.DeleteAsync($"/api/phase1/care-workers/{workerId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        var worker = verifyDb.CareWorkers.Single(x => x.Id == workerId);
        Assert.Equal("Archived", worker.Availability);
        Assert.False(verifyDb.AppUsers.Single(x => x.Id == userId).IsActive);
        Assert.Contains(verifyDb.AuditEvents, x => x.Action == "care_worker.archived" && x.EntityId == workerId);
    }

    [Fact]
    public async Task CareWorkerDeleteIsBlockedWhenFutureActiveVisitsExist()
    {
        var workerId = Guid.NewGuid();
        var visitId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var setupDb = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            setupDb.CareWorkers.Add(new CareWorker(workerId, "Busy Archive Worker", "Care", "Flexible", 1, 20, "Valid", "Compliant", "10 miles", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            setupDb.Visits.Add(new Visit(visitId, TestIds.ServiceUserId, workerId, DateTimeOffset.UtcNow.AddDays(3), "Future active visit", 30, "Personal care", VisitStatus.Scheduled, null, null, null, null, null, null, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await setupDb.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        await Login(client, "admin", "AdminPassword123!");

        var response = await client.DeleteAsync($"/api/phase1/care-workers/{workerId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.NotEqual("Archived", verifyDb.CareWorkers.Single(x => x.Id == workerId).Availability);
    }

    [Fact]
    public async Task CarePlanDeleteArchivesInsteadOfHardDeleting()
    {
        var carePlanId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var setupDb = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            setupDb.CarePlans.Add(new CarePlan(carePlanId, TestIds.ServiceUserId, "1", "Active", "Personal", "Medication", "Mobility", "Nutrition", DateTimeOffset.UtcNow.AddDays(30), TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await setupDb.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        await Login(client, "admin", "AdminPassword123!");

        var response = await client.DeleteAsync($"/api/phase1/care-plans/{carePlanId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal("Archived", verifyDb.CarePlans.Single(x => x.Id == carePlanId).Status);
        Assert.Contains(verifyDb.AuditEvents, x => x.Action == "care_plan.archived" && x.EntityId == carePlanId);
    }

    [Fact]
    public async Task RiskAssessmentDeleteArchivesInsteadOfHardDeleting()
    {
        var riskId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var setupDb = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            setupDb.RiskAssessments.Add(new RiskAssessment(riskId, TestIds.ServiceUserId, "Falls", RiskLevel.Medium, "Use walking frame", DateTimeOffset.UtcNow.AddDays(30), TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await setupDb.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        await Login(client, "admin", "AdminPassword123!");

        var response = await client.DeleteAsync($"/api/phase1/risk-assessments/{riskId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.StartsWith("[Archived]", verifyDb.RiskAssessments.Single(x => x.Id == riskId).MitigationPlan);
        Assert.Contains(verifyDb.AuditEvents, x => x.Action == "risk_assessment.archived" && x.EntityId == riskId);
    }


    [Fact]
    public async Task IncidentDeleteArchivesInsteadOfHardDeleting()
    {
        var incidentId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var setupDb = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            setupDb.Incidents.Add(new Incident(incidentId, TestIds.ServiceUserId, TestIds.VisitId, "Medication", "Medium", "Governance archive test", "Closed", DateTimeOffset.UtcNow, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await setupDb.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        await Login(client, "admin", "AdminPassword123!");

        var response = await client.DeleteAsync($"/api/phase1/incidents/{incidentId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal("Archived", verifyDb.Incidents.Single(x => x.Id == incidentId).Status);
        Assert.Contains(verifyDb.AuditEvents, x => x.Action == "incident.archived" && x.EntityId == incidentId);
    }


    [Fact]
    public async Task DocumentDeleteWithdrawsInsteadOfHardDeleting()
    {
        var documentId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var setupDb = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            setupDb.Documents.Add(new DocumentItem(documentId, TestIds.ServiceUserId, "withdraw-test.pdf", "Care plan", "local://withdraw-test.pdf", "Admin", DateTimeOffset.UtcNow, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await setupDb.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        await Login(client, "admin", "AdminPassword123!");

        var response = await client.DeleteAsync($"/api/phase1/documents/{documentId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        var document = verifyDb.Documents.Single(x => x.Id == documentId);
        Assert.Equal("Withdrawn", document.Category);
        Assert.StartsWith("[Withdrawn]", document.FileName);
        Assert.Contains(verifyDb.AuditEvents, x => x.Action == "document.withdrawn" && x.EntityId == documentId);
    }

    [Fact]
    public async Task MedicationDeleteDiscontinuesWhenNoAdministrationHistory()
    {
        var medicationId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var setupDb = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            setupDb.Medications.Add(new Medication(medicationId, TestIds.ServiceUserId, "Archive med", "5mg", "Oral", "Morning", false, "Pharmacy", "None", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await setupDb.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        await Login(client, "admin", "AdminPassword123!");

        var response = await client.DeleteAsync($"/api/phase1/medications/{medicationId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        var medication = verifyDb.Medications.Single(x => x.Id == medicationId);
        Assert.Equal("Discontinued", medication.Schedule);
        Assert.StartsWith("[Discontinued]", medication.AllergyWarning);
        Assert.Contains(verifyDb.AuditEvents, x => x.Action == "medication.discontinued" && x.EntityId == medicationId);
    }

    [Fact]
    public async Task MedicationDeleteIsBlockedWhenAdministrationHistoryExists()
    {
        var client = _factory.CreateClient();
        await Login(client, "admin", "AdminPassword123!");

        var response = await client.DeleteAsync($"/api/phase1/medications/{TestIds.MedicationId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.NotEqual("Discontinued", verifyDb.Medications.Single(x => x.Id == TestIds.MedicationId).Schedule);
    }

    [Fact]
    public async Task CareNoteDeleteWithdrawsInsteadOfHardDeleting()
    {
        var noteId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var setupDb = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            setupDb.CareNotes.Add(new CareNote(noteId, TestIds.VisitId, TestIds.ServiceUserId, TestIds.WorkerId, "Original note", "Done", "Meal", "None", "None", false, DateTimeOffset.UtcNow, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await setupDb.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        await Login(client, "admin", "AdminPassword123!");

        var response = await client.DeleteAsync($"/api/phase1/care-notes/{noteId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        var note = verifyDb.CareNotes.Single(x => x.Id == noteId);
        Assert.StartsWith("[Corrected/withdrawn]", note.Summary);
        Assert.True(note.RequiresReview);
        Assert.Contains(verifyDb.AuditEvents, x => x.Action == "care_note.withdrawn" && x.EntityId == noteId);
    }

    [Fact]
    public async Task NotificationDeleteDismissesInsteadOfHardDeleting()
    {
        var notificationId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var setupDb = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            setupDb.Notifications.Add(new NotificationItem(notificationId, "Governance notification", "Archive test", DateTimeOffset.UtcNow, false, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await setupDb.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        await Login(client, "admin", "AdminPassword123!");

        var response = await client.DeleteAsync($"/api/phase1/notifications/{notificationId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.True(verifyDb.Notifications.Single(x => x.Id == notificationId).IsRead);
        Assert.Contains(verifyDb.AuditEvents, x => x.Action == "notification.dismissed" && x.EntityId == notificationId);
    }


    [Fact]
    public void ExistingAuditEventCannotBeModifiedOrDeleted()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        var audit = new AuditEvent(Guid.NewGuid(), "governance.test", "tester", "ServiceUser", TestIds.ServiceUserId, DateTimeOffset.UtcNow, TenantDefaults.OrganizationId, TenantDefaults.BranchId);
        db.AuditEvents.Add(audit);
        db.SaveChanges();

        var tracked = db.AuditEvents.Single(x => x.Id == audit.Id);
        db.Entry(tracked).State = Microsoft.EntityFrameworkCore.EntityState.Deleted;

        var ex = Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        Assert.Contains("immutable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task Login(HttpClient client, string userName, string password)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName, password });
        login.EnsureSuccessStatusCode();
        var payload = await login.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(payload?.Token);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", payload.Token);
    }

    private sealed record LoginResponse(string Token);
}
