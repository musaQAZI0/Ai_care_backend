using System.Text;
using AiCare.Application;
using AiCare.Api;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("ReactClient", policy =>
    {
        var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? ["http://127.0.0.1:5173", "http://localhost:5173"];

        policy
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("JwtOptions"));
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Missing DefaultConnection");
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();
builder.Services.AddHttpClient();
builder.Services.AddInfrastructure(connectionString);

var jwtOptions = builder.Configuration.GetSection("JwtOptions").Get<JwtOptions>() ?? throw new InvalidOperationException("Missing JwtOptions");
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey));

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = jwtOptions.Issuer,
        ValidateAudience = true,
        ValidAudience = jwtOptions.Audience,
        ValidateLifetime = true,
        IssuerSigningKey = signingKey,
        ValidateIssuerSigningKey = true,
    };
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Phase1User", policy => policy.RequireRole("ServiceUser", "FamilyMember", "CareWorker", "CareCoordinator", "CareManager", "Administrator", "BackOffice"));
});

builder.Services.AddControllers();

var app = builder.Build();

app.UseCors("ReactClient");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/", () => Results.Ok(new
{
    name = "AiCare API",
    phase = "Social care platform release pivot",
    status = "running"
}));

var phase1 = app.Environment.IsDevelopment()
    ? app.MapGroup("/api/phase1")
    : app.MapGroup("/api/phase1").RequireAuthorization("Phase1User");

phase1.MapGet("/dashboard", (CareDbContext context, ITenantContext tenant) =>
{
    var now = DateTimeOffset.UtcNow;
    var visits = context.Visits.AsNoTracking().Where(item => item.OrganizationId == tenant.OrganizationId).ToList();
    var people = context.ServiceUsers.AsNoTracking().Where(item => item.OrganizationId == tenant.OrganizationId).ToList();
    var workers = context.CareWorkers.AsNoTracking().Where(item => item.OrganizationId == tenant.OrganizationId).ToList();
    var plans = context.CarePlans.AsNoTracking().Where(item => item.OrganizationId == tenant.OrganizationId).ToList();
    var alerts = context.AiRiskAlerts.AsNoTracking().Where(item => item.OrganizationId == tenant.OrganizationId && !item.HumanReviewed).ToList();
    var incidents = context.Incidents.AsNoTracking().Where(item => item.OrganizationId == tenant.OrganizationId && item.Status != "Closed").ToList();
    var completed = visits.Count(item => item.Status == VisitStatus.Completed);
    return Results.Ok(new
    {
        metrics = new[]
        {
            new { label = "Visit completion", value = visits.Count == 0 ? "0%" : $"{Math.Round(completed * 100d / visits.Count)}%", detail = $"{completed} of {visits.Count} recorded visits", tone = "growth" },
            new { label = "Care plan reviews", value = plans.Count(item => item.ReviewDueAt <= now.AddDays(30)).ToString(), detail = $"{plans.Count(item => item.ReviewDueAt < now)} overdue", tone = "warning" },
            new { label = "Risk alerts", value = alerts.Count.ToString(), detail = "awaiting human review", tone = "risk" },
            new { label = "Open incidents", value = incidents.Count.ToString(), detail = "requiring follow-up", tone = "stable" },
        },
        priorityPeople = people.OrderBy(item => item.Risk).Take(8),
        upcomingVisits = visits.Where(item => item.StartsAt >= now.AddDays(-1)).OrderBy(item => item.StartsAt).Take(10),
        workers = workers.Take(8),
        overduePlans = plans.Where(item => item.ReviewDueAt < now).OrderBy(item => item.ReviewDueAt).Take(8),
    });
});

phase1.MapGet("/service-users", (ICareRepository repository) => Results.Ok(repository.GetServiceUsers()));
phase1.MapGet("/service-users/{id:guid}", (Guid id, ICareRepository repository) =>
{
    var serviceUser = repository.GetServiceUser(id);
    return serviceUser is null ? Results.NotFound() : Results.Ok(serviceUser);
});
phase1.MapPost("/service-users", (CreateServiceUserRequest request, ICareRepository repository) =>
{
    if (Missing(request.FullName, request.PhoneNumber, request.CareNeeds, request.EmergencyContact, request.PreferredCareWorker))
    {
        return Results.BadRequest(new { message = "Full name, phone number, care needs, emergency contact, and preferred care worker are required." });
    }

    var serviceUser = repository.AddServiceUser(request);
    return Results.Created($"/api/phase1/service-users/{serviceUser.Id}", serviceUser);
});
phase1.MapPut("/service-users/{id:guid}", (Guid id, CreateServiceUserRequest request, ICareRepository repository) =>
{
    if (Missing(request.FullName, request.PhoneNumber, request.CareNeeds, request.EmergencyContact, request.PreferredCareWorker))
    {
        return Results.BadRequest(new { message = "Full name, phone number, care needs, emergency contact, and preferred care worker are required." });
    }

    var serviceUser = repository.UpdateServiceUser(id, request);
    return serviceUser is null ? Results.NotFound() : Results.Ok(serviceUser);
});

phase1.MapGet("/service-users/{id:guid}/complete-record", (Guid id, CareDbContext context, ITenantContext tenant) =>
{
    var person = context.ServiceUsers.AsNoTracking().FirstOrDefault(item => item.Id == id && item.OrganizationId == tenant.OrganizationId);
    if (person is null || !tenant.CanAccess(person.OrganizationId, person.BranchId)) return Results.NotFound();

    var record = context.PersonRecords.AsNoTracking().FirstOrDefault(item => item.ServiceUserId == id && item.OrganizationId == tenant.OrganizationId);
    var assessments = context.CareAssessments.AsNoTracking().Where(item => item.ServiceUserId == id && item.OrganizationId == tenant.OrganizationId).OrderByDescending(item => item.CompletedAt).ToList();
    var plans = context.CarePlans.AsNoTracking().Where(item => item.ServiceUserId == id && item.OrganizationId == tenant.OrganizationId).OrderByDescending(item => item.Version).ToList();
    var planIds = plans.Select(item => item.Id).ToList();
    var outcomes = context.CarePlanOutcomes.AsNoTracking().Where(item => planIds.Contains(item.CarePlanId) && item.OrganizationId == tenant.OrganizationId).ToList();
    var risks = context.RiskAssessments.AsNoTracking().Where(item => item.ServiceUserId == id && item.OrganizationId == tenant.OrganizationId).ToList();
    var family = context.FamilyMembers.AsNoTracking().Where(item => item.ServiceUserId == id && item.OrganizationId == tenant.OrganizationId).ToList();
    var notes = context.CareNotes.AsNoTracking().Where(item => item.ServiceUserId == id && item.OrganizationId == tenant.OrganizationId).OrderByDescending(item => item.CreatedAt).Take(20).ToList();
    var incidents = context.Incidents.AsNoTracking().Where(item => item.ServiceUserId == id && item.OrganizationId == tenant.OrganizationId).OrderByDescending(item => item.ReportedAt).Take(20).ToList();
    return Results.Ok(new { person, record, assessments, plans, outcomes, risks, family, notes, incidents });
});

phase1.MapPut("/service-users/{id:guid}/person-record", (Guid id, UpsertPersonRecordRequest request, CareDbContext context, ITenantContext tenant) =>
{
    var person = context.ServiceUsers.FirstOrDefault(item => item.Id == id && item.OrganizationId == tenant.OrganizationId);
    if (person is null || !tenant.CanAccess(person.OrganizationId, person.BranchId)) return Results.NotFound();
    var existing = context.PersonRecords.FirstOrDefault(item => item.ServiceUserId == id && item.OrganizationId == tenant.OrganizationId);
    var updated = new PersonRecord(existing?.Id ?? Guid.NewGuid(), id, request.PreferredName, request.Pronouns, request.HealthIdentifier, request.GpDetails, request.PharmacyDetails, request.LegalRepresentative, request.ConsentStatus, request.MentalCapacityStatus, request.CommunicationPassport, request.PersonalHistory, request.WhatMattersToMe, request.DesiredOutcomes, request.AdvanceCareWishes, request.AdmittedAt, request.DischargedAt, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId);
    if (existing is null) context.PersonRecords.Add(updated); else context.Entry(existing).CurrentValues.SetValues(updated);
    context.SaveChanges();
    return Results.Ok(updated);
});

phase1.MapGet("/assessments", (Guid? serviceUserId, CareDbContext context, ITenantContext tenant) => Results.Ok(
    context.CareAssessments.AsNoTracking().Where(item => item.OrganizationId == tenant.OrganizationId && (serviceUserId == null || item.ServiceUserId == serviceUserId)).OrderByDescending(item => item.CompletedAt).ToList()));

phase1.MapPost("/assessments", (CreateCareAssessmentRequest request, CareDbContext context, ITenantContext tenant) =>
{
    try { System.Text.Json.JsonDocument.Parse(request.AnswersJson); } catch { return Results.BadRequest(new { message = "AnswersJson must contain valid JSON." }); }
    var assessment = new CareAssessment(Guid.NewGuid(), request.ServiceUserId, request.AssessmentType, request.TemplateVersion, "Completed", request.AnswersJson, request.Score, request.Risk, request.Summary, request.RecommendedActions, request.CompletedBy, DateTimeOffset.UtcNow, request.ReviewDueAt, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId);
    context.CareAssessments.Add(assessment);
    context.SaveChanges();
    return Results.Created($"/api/phase1/assessments/{assessment.Id}", assessment);
});

phase1.MapPost("/care-plans/{id:guid}/outcomes", (Guid id, CreateCarePlanOutcomeRequest request, CareDbContext context, ITenantContext tenant) =>
{
    var plan = context.CarePlans.AsNoTracking().FirstOrDefault(item => item.Id == id && item.OrganizationId == tenant.OrganizationId);
    if (plan is null || request.CarePlanId != id || request.ServiceUserId != plan.ServiceUserId) return Results.BadRequest(new { message = "The outcome must belong to the selected care plan and person." });
    var outcome = new CarePlanOutcome(Guid.NewGuid(), id, plan.ServiceUserId, request.Goal, request.DesiredOutcome, request.Interventions, request.ResponsiblePerson, request.Measure, "Active", request.TargetDate, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId);
    context.CarePlanOutcomes.Add(outcome);
    context.SaveChanges();
    return Results.Created($"/api/phase1/care-plans/{id}/outcomes/{outcome.Id}", outcome);
});

phase1.MapPost("/care-plans/{id:guid}/approve", (Guid id, CareDbContext context, ITenantContext tenant) =>
{
    var plan = context.CarePlans.FirstOrDefault(item => item.Id == id && item.OrganizationId == tenant.OrganizationId);
    if (plan is null) return Results.NotFound();
    var approved = plan with { Status = "Active" };
    context.Entry(plan).CurrentValues.SetValues(approved);
    context.SaveChanges();
    return Results.Ok(approved);
});

phase1.MapGet("/care-workers", (ICareRepository repository) => Results.Ok(repository.GetCareWorkers()));
phase1.MapGet("/care-workers/{id:guid}", (Guid id, CareDbContext context, ITenantContext tenant) =>
{
    var worker = context.CareWorkers.AsNoTracking().FirstOrDefault(item => item.Id == id && item.OrganizationId == tenant.OrganizationId);
    if (worker is null || !tenant.CanAccess(worker.OrganizationId, worker.BranchId)) return Results.NotFound();
    var visits = context.Visits.AsNoTracking().Where(item => item.CareWorkerId == id && item.OrganizationId == tenant.OrganizationId).OrderByDescending(item => item.StartsAt).Take(25).ToList();
    return Results.Ok(new { worker, visits });
});
phase1.MapPost("/care-workers", (CreateCareWorkerRequest request, ICareRepository repository) =>
{
    if (Missing(request.FullName, request.Specialization, request.Availability))
    {
        return Results.BadRequest(new { message = "Full name, specialization, and availability are required." });
    }

    var careWorker = repository.AddCareWorker(request);
    return Results.Created($"/api/phase1/care-workers/{careWorker.Id}", careWorker);
});
phase1.MapPut("/care-workers/{id:guid}", (Guid id, CreateCareWorkerRequest request, ICareRepository repository) =>
{
    if (Missing(request.FullName, request.Specialization, request.Availability))
    {
        return Results.BadRequest(new { message = "Full name, specialization, and availability are required." });
    }

    var careWorker = repository.UpdateCareWorker(id, request);
    return careWorker is null ? Results.NotFound() : Results.Ok(careWorker);
});

phase1.MapGet("/visits", (ICareRepository repository) => Results.Ok(repository.GetVisits()));
phase1.MapGet("/visits/{id:guid}", (Guid id, CareDbContext context, ITenantContext tenant) =>
{
    var visit = context.Visits.AsNoTracking().FirstOrDefault(item => item.Id == id && item.OrganizationId == tenant.OrganizationId);
    if (visit is null || !tenant.CanAccess(visit.OrganizationId, visit.BranchId)) return Results.NotFound();
    var person = context.ServiceUsers.AsNoTracking().FirstOrDefault(item => item.Id == visit.ServiceUserId);
    var worker = context.CareWorkers.AsNoTracking().FirstOrDefault(item => item.Id == visit.CareWorkerId);
    var notes = context.CareNotes.AsNoTracking().Where(item => item.VisitId == id && item.OrganizationId == tenant.OrganizationId).OrderByDescending(item => item.CreatedAt).ToList();
    var observations = context.HealthObservations.AsNoTracking().Where(item => item.VisitId == id && item.OrganizationId == tenant.OrganizationId).OrderByDescending(item => item.RecordedAt).ToList();
    return Results.Ok(new { visit, person, worker, notes, observations });
});
phase1.MapPost("/visits", (CreateVisitRequest request, ICareRepository repository) =>
{
    if (request.ServiceUserId == Guid.Empty || request.CareWorkerId == Guid.Empty || string.IsNullOrWhiteSpace(request.VisitType) || request.DurationMinutes <= 0)
    {
        return Results.BadRequest(new { message = "Service user, care worker, visit type, and a positive duration are required." });
    }

    var visit = repository.AddVisit(request);
    return Results.Created($"/api/phase1/visits/{visit.Id}", visit);
});
phase1.MapPut("/visits/{id:guid}", (Guid id, CreateVisitRequest request, ICareRepository repository) =>
{
    if (request.ServiceUserId == Guid.Empty || request.CareWorkerId == Guid.Empty || string.IsNullOrWhiteSpace(request.VisitType) || request.DurationMinutes <= 0)
    {
        return Results.BadRequest(new { message = "Service user, care worker, visit type, and a positive duration are required." });
    }

    var visit = repository.UpdateVisit(id, request);
    return visit is null ? Results.NotFound() : Results.Ok(visit);
});
phase1.MapPatch("/visits/{id:guid}/status", (Guid id, UpdateVisitStatusRequest request, ICareRepository repository) =>
{
    var visit = repository.UpdateVisitStatus(id, request.Status);
    return visit is null ? Results.NotFound() : Results.Ok(visit);
});
phase1.MapPost("/visits/{id:guid}/check-in", (Guid id, VisitCheckInRequest request, ICareRepository repository) =>
{
    var visit = repository.CheckInVisit(id, request);
    return visit is null ? Results.NotFound() : Results.Ok(visit);
});
phase1.MapPost("/visits/{id:guid}/check-out", (Guid id, VisitCheckOutRequest request, ICareRepository repository) =>
{
    var visit = repository.CheckOutVisit(id, request);
    return visit is null ? Results.NotFound() : Results.Ok(visit);
});

phase1.MapGet("/care-plans", (ICareRepository repository) => Results.Ok(repository.GetCarePlans()));
phase1.MapPost("/care-plans", (CreateCarePlanRequest request, ICareRepository repository) =>
{
    if (request.ServiceUserId == Guid.Empty || Missing(request.PersonalCare, request.MedicationSupport, request.MobilityAndTransfers, request.Nutrition))
    {
        return Results.BadRequest(new { message = "Service user and care plan details are required." });
    }

    var carePlan = repository.AddCarePlan(request);
    return Results.Created($"/api/phase1/care-plans/{carePlan.Id}", carePlan);
});
phase1.MapPut("/care-plans/{id:guid}", (Guid id, CreateCarePlanRequest request, ICareRepository repository) =>
{
    var carePlan = repository.UpdateCarePlan(id, request);
    return carePlan is null ? Results.NotFound() : Results.Ok(carePlan);
});
phase1.MapDelete("/care-plans/{id:guid}", (Guid id, ICareRepository repository) =>
    repository.DeleteCarePlan(id) ? Results.NoContent() : Results.NotFound());
phase1.MapGet("/risk-assessments", (ICareRepository repository) => Results.Ok(repository.GetRiskAssessments()));
phase1.MapPost("/risk-assessments", (CreateRiskAssessmentRequest request, ICareRepository repository) =>
{
    if (request.ServiceUserId == Guid.Empty || Missing(request.Category, request.MitigationPlan))
    {
        return Results.BadRequest(new { message = "Service user, category, and mitigation plan are required." });
    }

    var risk = repository.AddRiskAssessment(request);
    return Results.Created($"/api/phase1/risk-assessments/{risk.Id}", risk);
});
phase1.MapPut("/risk-assessments/{id:guid}", (Guid id, CreateRiskAssessmentRequest request, ICareRepository repository) =>
{
    var risk = repository.UpdateRiskAssessment(id, request);
    return risk is null ? Results.NotFound() : Results.Ok(risk);
});
phase1.MapDelete("/risk-assessments/{id:guid}", (Guid id, ICareRepository repository) =>
    repository.DeleteRiskAssessment(id) ? Results.NoContent() : Results.NotFound());
phase1.MapGet("/family-members", (ICareRepository repository) => Results.Ok(repository.GetFamilyMembers()));
phase1.MapPost("/family-members", (CreateFamilyMemberRequest request, ICareRepository repository) =>
{
    if (request.ServiceUserId == Guid.Empty || Missing(request.FullName, request.Email, request.Relationship, request.AccessLevel) || !LooksLikeEmail(request.Email))
    {
        return Results.BadRequest(new { message = "Valid family member contact details are required." });
    }

    var familyMember = repository.AddFamilyMember(request);
    return Results.Created($"/api/phase1/family-members/{familyMember.Id}", familyMember);
});
phase1.MapGet("/documents", (ICareRepository repository) => Results.Ok(repository.GetDocuments()));
phase1.MapPost("/documents", (CreateDocumentRequest request, ICareRepository repository) =>
{
    if (request.ServiceUserId == Guid.Empty || Missing(request.FileName, request.Category, request.StoragePath, request.UploadedBy))
    {
        return Results.BadRequest(new { message = "Document file name, category, storage path, and uploader are required." });
    }

    var document = repository.AddDocument(request);
    return Results.Created($"/api/phase1/documents/{document.Id}", document);
});
phase1.MapPost("/documents/upload", async (HttpRequest request, IWebHostEnvironment environment, IConfiguration configuration, IHttpClientFactory httpClientFactory, ICareRepository repository, ITenantContext tenant) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { message = "Multipart form data is required." });
    }

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    var serviceUserIdValue = form["serviceUserId"].ToString();
    var category = form["category"].ToString();
    var uploadedBy = form["uploadedBy"].ToString();
    if (file is null || file.Length == 0 || !Guid.TryParse(serviceUserIdValue, out var serviceUserId) || Missing(category, uploadedBy))
    {
        return Results.BadRequest(new { message = "File, service user, category, and uploader are required." });
    }

    var safeFileName = Path.GetFileName(file.FileName);
    var storedName = $"{Guid.NewGuid()}-{safeFileName}";
    var storagePath = string.Equals(configuration["Storage:Provider"], "Supabase", StringComparison.OrdinalIgnoreCase)
        ? await UploadToSupabaseStorage(file, storedName, configuration, httpClientFactory, tenant)
        : await UploadToLocalStorage(file, storedName, environment);

    var document = repository.AddDocument(new CreateDocumentRequest(serviceUserId, safeFileName, category, storagePath, uploadedBy));
    return Results.Created($"/api/phase1/documents/{document.Id}", document);
});
phase1.MapGet("/documents/{id:guid}/download-url", (Guid id, CareDbContext context, IConfiguration configuration, ITenantContext tenant) =>
{
    var document = context.Documents.AsNoTracking().FirstOrDefault(item => item.Id == id);
    if (document is null || !tenant.CanAccess(document.OrganizationId, document.BranchId))
    {
        return Results.NotFound();
    }

    if (!document.StoragePath.StartsWith("supabase://", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Ok(new { provider = "Local", path = document.StoragePath });
    }

    var publicBaseUrl = configuration["Supabase:PublicFileBaseUrl"];
    if (string.IsNullOrWhiteSpace(publicBaseUrl))
    {
        return Results.Ok(new { provider = "Supabase", objectKey = document.StoragePath.Replace("supabase://", "", StringComparison.OrdinalIgnoreCase), message = "Configure Supabase:PublicFileBaseUrl or signed URL generation before public downloads." });
    }

    return Results.Ok(new { provider = "Supabase", url = $"{publicBaseUrl.TrimEnd('/')}/{document.StoragePath.Replace("supabase://", "", StringComparison.OrdinalIgnoreCase)}" });
});
phase1.MapPut("/documents/{id:guid}", (Guid id, CreateDocumentRequest request, ICareRepository repository) =>
{
    var document = repository.UpdateDocument(id, request);
    return document is null ? Results.NotFound() : Results.Ok(document);
});
phase1.MapDelete("/documents/{id:guid}", (Guid id, ICareRepository repository) =>
    repository.DeleteDocument(id) ? Results.NoContent() : Results.NotFound());
phase1.MapGet("/medications", (ICareRepository repository) => Results.Ok(repository.GetMedications()));
phase1.MapGet("/mar", (ICareRepository repository) => Results.Ok(repository.GetMedicationAdministrationRecords()));
phase1.MapGet("/care-notes", (ICareRepository repository) => Results.Ok(repository.GetCareNotes()));
phase1.MapPost("/care-notes", (CreateCareNoteRequest request, ICareRepository repository) =>
{
    if (request.VisitId == Guid.Empty || request.ServiceUserId == Guid.Empty || request.CareWorkerId == Guid.Empty || Missing(request.Summary))
    {
        return Results.BadRequest(new { message = "Visit, service user, care worker, and summary are required." });
    }

    var note = repository.AddCareNote(request);
    return Results.Created($"/api/phase1/care-notes/{note.Id}", note);
});
phase1.MapGet("/observations", (ICareRepository repository) => Results.Ok(repository.GetHealthObservations()));
phase1.MapGet("/incidents", (ICareRepository repository) => Results.Ok(repository.GetIncidents()));
phase1.MapPost("/incidents", (CreateIncidentRequest request, ICareRepository repository) =>
{
    if (request.ServiceUserId == Guid.Empty || Missing(request.Category, request.Severity, request.Description))
    {
        return Results.BadRequest(new { message = "Service user, category, severity, and description are required." });
    }

    var incident = repository.AddIncident(request);
    return Results.Created($"/api/phase1/incidents/{incident.Id}", incident);
});
phase1.MapPut("/incidents/{id:guid}", (Guid id, CreateIncidentRequest request, ICareRepository repository) =>
{
    var incident = repository.UpdateIncident(id, request);
    return incident is null ? Results.NotFound() : Results.Ok(incident);
});
phase1.MapDelete("/incidents/{id:guid}", (Guid id, ICareRepository repository) =>
    repository.DeleteIncident(id) ? Results.NoContent() : Results.NotFound());
phase1.MapGet("/ai/risk-alerts", (ICareRepository repository) => Results.Ok(repository.GetAiRiskAlerts()));
phase1.MapGet("/payroll-runs", (ICareRepository repository) => Results.Ok(repository.GetPayrollRuns()));
phase1.MapPost("/payroll-runs/generate", (ICareRepository repository) =>
{
    var payroll = repository.GeneratePayrollRun();
    return Results.Created($"/api/phase1/payroll-runs/{payroll.Id}", payroll);
});
phase1.MapGet("/invoices", (ICareRepository repository) => Results.Ok(repository.GetInvoices()));
phase1.MapPost("/invoices/generate", (ICareRepository repository) => Results.Ok(repository.GenerateInvoices()));
phase1.MapGet("/reports", (ICareRepository repository) => Results.Ok(repository.GetReports()));
phase1.MapGet("/reports/{reportName}/pdf", (string reportName, ICareRepository repository) =>
    Results.File(repository.ExportPdf(reportName), "application/pdf", $"{reportName}.pdf"));
phase1.MapGet("/reports/{reportName}/csv", (string reportName, CareDbContext context, ITenantContext tenant) =>
{
    var serviceUsers = context.ServiceUsers.AsNoTracking().AsEnumerable().Where(user => TenantVisible(tenant, user.OrganizationId, user.BranchId)).ToList();
    var careWorkers = context.CareWorkers.AsNoTracking().AsEnumerable().Where(worker => TenantVisible(tenant, worker.OrganizationId, worker.BranchId)).ToList();
    var visits = context.Visits.AsNoTracking().AsEnumerable().Where(visit => TenantVisible(tenant, visit.OrganizationId, visit.BranchId)).ToList();
    var incidents = context.Incidents.AsNoTracking().AsEnumerable().Where(incident => TenantVisible(tenant, incident.OrganizationId, incident.BranchId)).ToList();
    var invoices = context.Invoices.AsNoTracking().AsEnumerable().Where(invoice => TenantVisible(tenant, invoice.OrganizationId, invoice.BranchId)).ToList();
    var rows = new List<string>
    {
        "metric,value",
        $"service_users,{serviceUsers.Count}",
        $"care_workers,{careWorkers.Count}",
        $"visits,{visits.Count}",
        $"completed_visits,{visits.Count(visit => visit.Status == VisitStatus.Completed)}",
        $"incidents,{incidents.Count}",
        $"invoices,{invoices.Count}"
    };

    return Results.Text(string.Join(Environment.NewLine, rows), "text/csv");
});
phase1.MapPost("/reports/generate", (GenerateReportRequest request, CareDbContext context, ITenantContext tenant) =>
{
    var report = new
    {
        name = request.Name,
        format = request.Format,
        generatedAt = DateTimeOffset.Now,
        metrics = new
        {
            serviceUsers = context.ServiceUsers.Count(user => tenant.IsPlatformOwner || user.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || tenant.BranchId == null || user.BranchId == tenant.BranchId)),
            careWorkers = context.CareWorkers.Count(worker => tenant.IsPlatformOwner || worker.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || tenant.BranchId == null || worker.BranchId == tenant.BranchId)),
            visits = context.Visits.Count(visit => tenant.IsPlatformOwner || visit.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || tenant.BranchId == null || visit.BranchId == tenant.BranchId)),
            completedVisits = context.Visits.Count(visit => visit.Status == VisitStatus.Completed && (tenant.IsPlatformOwner || visit.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || tenant.BranchId == null || visit.BranchId == tenant.BranchId))),
            incidents = context.Incidents.Count(incident => tenant.IsPlatformOwner || incident.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || tenant.BranchId == null || incident.BranchId == tenant.BranchId)),
            openIncidents = context.Incidents.Count(incident => incident.Status != "Closed" && (tenant.IsPlatformOwner || incident.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || tenant.BranchId == null || incident.BranchId == tenant.BranchId))),
            invoices = context.Invoices.Count(invoice => tenant.IsPlatformOwner || invoice.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || tenant.BranchId == null || invoice.BranchId == tenant.BranchId)),
            auditEvents = context.AuditEvents.Count(audit => tenant.IsPlatformOwner || audit.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || tenant.BranchId == null || audit.BranchId == tenant.BranchId))
        }
    };
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "report.generated", "system", "Report", null, DateTimeOffset.Now, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    context.SaveChanges();
    return Results.Ok(report);
});
phase1.MapPost("/reports/builder", (BuildReportRequest request, CareDbContext context, ITenantContext tenant) =>
{
    if (Missing(request.Name, request.Category) || request.Metrics.Count == 0)
    {
        return Results.BadRequest(new { message = "Report name, category, and at least one metric are required." });
    }

    var report = new ReportDefinition(
        Guid.NewGuid(),
        request.Name.Trim(),
        request.Category.Trim(),
        string.Join(", ", request.Formats.Count == 0 ? ["PDF", "CSV"] : request.Formats),
        request.Schedule.Trim(),
        tenant.OrganizationId,
        tenant.BranchId ?? TenantDefaults.BranchId);
    context.Reports.Add(report);
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "report.definition_created", "system", nameof(ReportDefinition), report.Id, DateTimeOffset.Now, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    context.SaveChanges();
    return Results.Created($"/api/phase1/reports/{report.Id}", new
    {
        report,
        metrics = request.Metrics,
        filters = request.Filters,
        scheduled = !string.Equals(request.Schedule, "One-off", StringComparison.OrdinalIgnoreCase)
    });
});
phase1.MapGet("/compliance", (ICareRepository repository) => Results.Ok(repository.GetComplianceItems()));
phase1.MapGet("/uat-checklist", (ICareRepository repository) => Results.Ok(repository.GetUatChecklist()));

phase1.MapGet("/messages", (ICareRepository repository) => Results.Ok(repository.GetMessageThreads()));
phase1.MapPost("/messages", (CreateMessageThreadRequest request, ICareRepository repository) =>
{
    if (request.ServiceUserId == Guid.Empty || request.CareWorkerId == Guid.Empty || Missing(request.Subject, request.LastMessage))
    {
        return Results.BadRequest(new { message = "Service user, care worker, subject, and message are required." });
    }

    var thread = repository.AddMessageThread(request);
    return Results.Created($"/api/phase1/messages/{thread.Id}", thread);
});

phase1.MapGet("/notifications", (ICareRepository repository) => Results.Ok(repository.GetNotifications()));
phase1.MapPost("/notifications/send", (SendNotificationRequest request, CareDbContext context, ITenantContext tenant) =>
{
    if (Missing(request.Title, request.Detail, request.Channel))
    {
        return Results.BadRequest(new { message = "Title, detail, and channel are required." });
    }

    var notification = new NotificationItem(Guid.NewGuid(), request.Title, $"{request.Channel}: {request.Detail}", DateTimeOffset.Now, false, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId);
    context.Notifications.Add(notification);
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "notification.queued", "system", nameof(NotificationItem), notification.Id, DateTimeOffset.Now, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    context.SaveChanges();
    return Results.Accepted($"/api/phase1/notifications/{notification.Id}", notification);
});

phase1.MapGet("/admin/users", (ICareRepository repository) => Results.Ok(repository.GetAdminUsers()));
phase1.MapPost("/admin/users", (CreateAdminUserRequest request, ICareRepository repository) =>
{
    if (Missing(request.UserName, request.Email, request.Password) || !LooksLikeEmail(request.Email))
    {
        return Results.BadRequest(new { message = "Username, valid email, and password are required." });
    }

    if (request.Password.Length < 10)
    {
        return Results.BadRequest(new { message = "Password must be at least 10 characters." });
    }

    try
    {
        var user = repository.AddAdminUser(request);
        return Results.Created($"/api/phase1/admin/users/{user.Id}", user);
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { message = exception.Message });
    }
});
phase1.MapPatch("/admin/users/{id:guid}/role", (Guid id, UpdateUserRoleRequest request, ICareRepository repository) =>
{
    var user = repository.UpdateUserRole(id, request.Role);
    return user is null ? Results.NotFound() : Results.Ok(user);
});

phase1.MapGet("/audit-events", (ICareRepository repository) => Results.Ok(repository.GetAuditEvents()));

phase1.MapGet("/family/service-users/{id:guid}/timeline", (Guid id, CareDbContext context, ITenantContext tenant) =>
{
    var serviceUser = context.ServiceUsers.AsNoTracking().FirstOrDefault(user => user.Id == id);
    if (serviceUser is null || !tenant.CanAccess(serviceUser.OrganizationId, serviceUser.BranchId))
    {
        return Results.NotFound();
    }

    var visits = context.Visits.AsNoTracking()
        .Where(visit => visit.ServiceUserId == id && (tenant.IsPlatformOwner || visit.OrganizationId == tenant.OrganizationId) && (tenant.IsOrganizationWide || tenant.BranchId == null || visit.BranchId == tenant.BranchId))
        .Select(visit => new TimelineItem("Visit", visit.VisitType, visit.Status.ToString(), visit.StartsAt));
    var notes = context.CareNotes.AsNoTracking()
        .Where(note => note.ServiceUserId == id && (tenant.IsPlatformOwner || note.OrganizationId == tenant.OrganizationId) && (tenant.IsOrganizationWide || tenant.BranchId == null || note.BranchId == tenant.BranchId))
        .Select(note => new TimelineItem("Care note", note.Summary, note.Concerns, note.CreatedAt));
    var incidents = context.Incidents.AsNoTracking()
        .Where(incident => incident.ServiceUserId == id && (tenant.IsPlatformOwner || incident.OrganizationId == tenant.OrganizationId) && (tenant.IsOrganizationWide || tenant.BranchId == null || incident.BranchId == tenant.BranchId))
        .Select(incident => new TimelineItem("Incident", incident.Category, incident.Status, incident.ReportedAt));
    var documents = context.Documents.AsNoTracking()
        .Where(document => document.ServiceUserId == id && (tenant.IsPlatformOwner || document.OrganizationId == tenant.OrganizationId) && (tenant.IsOrganizationWide || tenant.BranchId == null || document.BranchId == tenant.BranchId))
        .Select(document => new TimelineItem("Document", document.FileName, document.Category, document.UploadedAt));

    return Results.Ok(visits.Concat(notes).Concat(incidents).Concat(documents).OrderByDescending(item => item.When).ToList());
});

phase1.MapGet("/family/service-users/{id:guid}/dashboard", (Guid id, CareDbContext context, ITenantContext tenant) =>
{
    var serviceUser = context.ServiceUsers.AsNoTracking().FirstOrDefault(user => user.Id == id);
    if (serviceUser is null || !tenant.CanAccess(serviceUser.OrganizationId, serviceUser.BranchId))
    {
        return Results.NotFound();
    }

    var visits = context.Visits.AsNoTracking().Where(visit => visit.ServiceUserId == id).AsEnumerable().Where(visit => TenantVisible(tenant, visit.OrganizationId, visit.BranchId)).ToList();
    var notes = context.CareNotes.AsNoTracking().Where(note => note.ServiceUserId == id).AsEnumerable().Where(note => TenantVisible(tenant, note.OrganizationId, note.BranchId)).ToList();
    var incidents = context.Incidents.AsNoTracking().Where(incident => incident.ServiceUserId == id).AsEnumerable().Where(incident => TenantVisible(tenant, incident.OrganizationId, incident.BranchId)).ToList();
    var medications = context.MedicationAdministrationRecords.AsNoTracking().AsEnumerable().Where(record => TenantVisible(tenant, record.OrganizationId, record.BranchId)).ToList();

    return Results.Ok(new
    {
        serviceUser = new { serviceUser.Id, serviceUser.FullName, serviceUser.Status, serviceUser.Risk, serviceUser.CareNeeds },
        upcomingVisits = visits.Where(visit => visit.StartsAt >= DateTimeOffset.Now).OrderBy(visit => visit.StartsAt).Take(5).ToList(),
        recentVisits = visits.OrderByDescending(visit => visit.StartsAt).Take(5).ToList(),
        recentNotes = notes.OrderByDescending(note => note.CreatedAt).Take(5).ToList(),
        medicationLog = medications.OrderByDescending(record => record.AdministeredAt).Take(8).ToList(),
        openIncidents = incidents.Where(incident => incident.Status != "Closed").OrderByDescending(incident => incident.ReportedAt).ToList(),
        monthlySummary = new
        {
            completedVisits = visits.Count(visit => visit.Status == VisitStatus.Completed && visit.StartsAt >= DateTimeOffset.Now.AddDays(-30)),
            notes = notes.Count(note => note.CreatedAt >= DateTimeOffset.Now.AddDays(-30)),
            incidents = incidents.Count(incident => incident.ReportedAt >= DateTimeOffset.Now.AddDays(-30)),
            lastUpdated = DateTimeOffset.Now
        }
    });
});

phase1.MapPost("/family/service-users/{id:guid}/preferences", (Guid id, FamilyPreferencesRequest request, CareDbContext context, ITenantContext tenant) =>
{
    var serviceUser = context.ServiceUsers.AsNoTracking().FirstOrDefault(user => user.Id == id);
    if (serviceUser is null || !tenant.CanAccess(serviceUser.OrganizationId, serviceUser.BranchId))
    {
        return Results.NotFound();
    }

    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "family.preferences_updated", "system", nameof(ServiceUser), id, DateTimeOffset.Now, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    context.SaveChanges();
    return Results.Ok(new
    {
        serviceUserId = id,
        request.EmailNotifications,
        request.SmsNotifications,
        request.MonthlyDigest,
        request.IncidentAlerts,
        updatedAt = DateTimeOffset.Now
    });
});

phase1.MapGet("/family/service-users/{id:guid}/monthly-report", (Guid id, CareDbContext context, ITenantContext tenant) =>
{
    var serviceUser = context.ServiceUsers.AsNoTracking().FirstOrDefault(user => user.Id == id);
    if (serviceUser is null || !tenant.CanAccess(serviceUser.OrganizationId, serviceUser.BranchId))
    {
        return Results.NotFound();
    }

    var since = DateTimeOffset.Now.AddDays(-30);
    var completedVisits = context.Visits.AsNoTracking().Where(visit => visit.ServiceUserId == id && visit.Status == VisitStatus.Completed && visit.StartsAt >= since).AsEnumerable().Count(visit => TenantVisible(tenant, visit.OrganizationId, visit.BranchId));
    var careNotes = context.CareNotes.AsNoTracking().Where(note => note.ServiceUserId == id && note.CreatedAt >= since).AsEnumerable().Count(note => TenantVisible(tenant, note.OrganizationId, note.BranchId));
    var incidentCount = context.Incidents.AsNoTracking().Where(incident => incident.ServiceUserId == id && incident.ReportedAt >= since).AsEnumerable().Count(incident => TenantVisible(tenant, incident.OrganizationId, incident.BranchId));
    var text = $"AiCare monthly family report\nService user: {serviceUser.FullName}\nPeriod start: {since:u}\nGenerated: {DateTimeOffset.Now:u}\nCompleted visits: {completedVisits}\nCare notes: {careNotes}\nIncidents: {incidentCount}\n";
    return Results.Text(text, "text/plain");
});

phase1.MapPost("/incidents/{id:guid}/investigate", (Guid id, InvestigateIncidentRequest request, CareDbContext context, ITenantContext tenant) =>
{
    if (Missing(request.Outcome, request.ActionPlan))
    {
        return Results.BadRequest(new { message = "Outcome and action plan are required." });
    }

    var incident = context.Incidents.Find(id);
    if (incident is null || !tenant.CanAccess(incident.OrganizationId, incident.BranchId))
    {
        return Results.NotFound();
    }

    var updated = incident with { Status = request.CloseIncident ? "Closed" : "Under investigation", Description = $"{incident.Description}\nInvestigation outcome: {request.Outcome}\nAction plan: {request.ActionPlan}" };
    context.Incidents.Update(updated);
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "incident.investigated", "system", nameof(Incident), id, DateTimeOffset.Now, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    context.SaveChanges();
    return Results.Ok(updated);
});

phase1.MapPost("/payroll-runs/{id:guid}/approve", (Guid id, CareDbContext context, ITenantContext tenant) =>
{
    var payroll = context.PayrollRuns.Find(id);
    if (payroll is null || !tenant.CanAccess(payroll.OrganizationId, payroll.BranchId))
    {
        return Results.NotFound();
    }

    var updated = payroll with { Status = "Approved" };
    context.PayrollRuns.Update(updated);
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "payroll.approved", "system", nameof(PayrollRun), id, DateTimeOffset.Now, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    context.SaveChanges();
    return Results.Ok(updated);
});

phase1.MapGet("/timesheets", (CareDbContext context, ITenantContext tenant) =>
{
    var items = context.Visits.AsNoTracking()
        .AsEnumerable()
        .Where(visit => TenantVisible(tenant, visit.OrganizationId, visit.BranchId))
        .GroupBy(visit => visit.CareWorkerId)
        .Select(group => new
        {
            careWorkerId = group.Key,
            visits = group.Count(),
            completedVisits = group.Count(visit => visit.Status == VisitStatus.Completed),
            scheduledMinutes = group.Sum(visit => visit.DurationMinutes),
            payableHours = Math.Round(group.Where(visit => visit.Status == VisitStatus.Completed).Sum(visit => visit.DurationMinutes) / 60m, 2),
            mileage = group.Count() * 3.5m,
            overtimeHours = Math.Max(0, group.Where(visit => visit.Status == VisitStatus.Completed).Sum(visit => visit.DurationMinutes) / 60m - 40m),
            status = "Ready for approval"
        })
        .ToList();
    return Results.Ok(items);
});

phase1.MapGet("/invoices/{id:guid}/lines", (Guid id, CareDbContext context, ITenantContext tenant) =>
{
    var invoice = context.Invoices.AsNoTracking().FirstOrDefault(item => item.Id == id);
    if (invoice is null || !tenant.CanAccess(invoice.OrganizationId, invoice.BranchId))
    {
        return Results.NotFound();
    }

    var visits = context.Visits.AsNoTracking()
        .Where(visit => visit.ServiceUserId == invoice.ServiceUserId && visit.Status == VisitStatus.Completed)
        .AsEnumerable()
        .Where(visit => TenantVisible(tenant, visit.OrganizationId, visit.BranchId))
        .OrderByDescending(visit => visit.StartsAt)
        .Take(12)
        .Select(visit => new
        {
            description = visit.VisitType,
            visit.StartsAt,
            quantity = Math.Round(visit.DurationMinutes / 60m, 2),
            unitRate = 30.00m,
            amount = Math.Round(visit.DurationMinutes / 60m, 2) * 30.00m
        })
        .ToList();
    return Results.Ok(visits);
});

phase1.MapPost("/invoices/{id:guid}/record-payment", (Guid id, RecordPaymentRequest request, CareDbContext context, ITenantContext tenant) =>
{
    var invoice = context.Invoices.Find(id);
    if (invoice is null || !tenant.CanAccess(invoice.OrganizationId, invoice.BranchId))
    {
        return Results.NotFound();
    }

    var updated = invoice with { Status = request.Amount >= invoice.Amount ? "Paid" : "Part paid" };
    context.Invoices.Update(updated);
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "invoice.payment_recorded", "system", nameof(Invoice), id, DateTimeOffset.Now, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    context.SaveChanges();
    return Results.Ok(new { invoice = updated, request.Amount, request.Reference, paidAt = DateTimeOffset.Now });
});

phase1.MapPost("/invoices/{id:guid}/approve", (Guid id, CareDbContext context, ITenantContext tenant) =>
{
    var invoice = context.Invoices.Find(id);
    if (invoice is null || !tenant.CanAccess(invoice.OrganizationId, invoice.BranchId))
    {
        return Results.NotFound();
    }

    var updated = invoice with { Status = "Approved" };
    context.Invoices.Update(updated);
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "invoice.approved", "system", nameof(Invoice), id, DateTimeOffset.Now, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    context.SaveChanges();
    return Results.Ok(updated);
});

phase1.MapPost("/ai/summarize-notes", (AiSummaryRequest request, CareDbContext context, ITenantContext tenant) =>
{
    var notes = context.CareNotes.AsNoTracking()
        .Where(note => (request.ServiceUserId == null || note.ServiceUserId == request.ServiceUserId) && (tenant.IsPlatformOwner || note.OrganizationId == tenant.OrganizationId) && (tenant.IsOrganizationWide || tenant.BranchId == null || note.BranchId == tenant.BranchId))
        .OrderByDescending(note => note.CreatedAt)
        .Take(10)
        .ToList();

    var summary = notes.Count == 0
        ? "No recent care notes are available for summarization."
        : $"AI draft summary based on {notes.Count} recent notes: {string.Join(" ", notes.Select(note => note.Summary)).Trim()}";
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "ai.summary_generated", "system", "AiInteraction", request.ServiceUserId, DateTimeOffset.Now, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    context.SaveChanges();
    return Results.Ok(new { summary, humanReviewRequired = true, generatedAt = DateTimeOffset.Now });
});

phase1.MapPost("/ai/detect-risks", (AiSummaryRequest request, CareDbContext context, ITenantContext tenant) =>
{
    var serviceUserId = request.ServiceUserId ?? context.ServiceUsers.AsNoTracking()
        .Where(user => tenant.IsPlatformOwner || user.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || tenant.BranchId == null || user.BranchId == tenant.BranchId))
        .Select(user => user.Id)
        .FirstOrDefault();
    if (serviceUserId == Guid.Empty)
    {
        return Results.BadRequest(new { message = "A service user is required before AI risk detection can run." });
    }

    var serviceUser = context.ServiceUsers.AsNoTracking().FirstOrDefault(user => user.Id == serviceUserId);
    if (serviceUser is null || !tenant.CanAccess(serviceUser.OrganizationId, serviceUser.BranchId))
    {
        return Results.NotFound();
    }

    var recentText = string.Join(" ", context.CareNotes.AsNoTracking().Where(note => note.ServiceUserId == serviceUserId && (tenant.IsPlatformOwner || note.OrganizationId == tenant.OrganizationId) && (tenant.IsOrganizationWide || tenant.BranchId == null || note.BranchId == tenant.BranchId)).OrderByDescending(note => note.CreatedAt).Take(10).Select(note => note.Summary + " " + note.Concerns));
    var risk = recentText.Contains("fall", StringComparison.OrdinalIgnoreCase) || recentText.Contains("unsteady", StringComparison.OrdinalIgnoreCase)
        ? RiskLevel.High
        : RiskLevel.Medium;
    var alert = new AiRiskAlert(Guid.NewGuid(), serviceUserId, "Care note pattern review", risk, recentText.Length == 0 ? "No notes found; baseline review recommended." : recentText, "Manager review required before action.", false, DateTimeOffset.Now, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId);
    context.AiRiskAlerts.Add(alert);
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "ai.risk_detected", "system", nameof(AiRiskAlert), alert.Id, DateTimeOffset.Now, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    context.SaveChanges();
    return Results.Created($"/api/phase1/ai/risk-alerts/{alert.Id}", alert);
});

phase1.MapGet("/organization/branches", (CareDbContext context, ITenantContext tenant) => Results.Ok(
    context.Branches.AsNoTracking()
        .Where(branch => tenant.IsPlatformOwner || branch.OrganizationId == tenant.OrganizationId)
        .Select(branch => new
        {
            branch.Id,
            branch.OrganizationId,
            branch.Name,
            branch.Region,
            branch.Status,
            users = context.AppUsers.Count(user => user.BranchId == branch.Id)
        })
        .ToList()));

phase1.MapPost("/organization/branches", (CreateBranchRequest request, CareDbContext context, ITenantContext tenant) =>
{
    if (Missing(request.Name, request.Region))
    {
        return Results.BadRequest(new { message = "Branch name and region are required." });
    }

    var organizationId = request.OrganizationId ?? tenant.OrganizationId;
    if (!tenant.IsPlatformOwner && organizationId != tenant.OrganizationId)
    {
        return Results.Forbid();
    }

    var organizationExists = context.Organizations.Any(organization => organization.Id == organizationId);
    if (!organizationExists)
    {
        return Results.BadRequest(new { message = "Organization does not exist." });
    }

    var branch = new Branch(Guid.NewGuid(), organizationId, request.Name.Trim(), request.Region.Trim(), "Active");
    context.Branches.Add(branch);
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "branch.created", "system", nameof(Branch), branch.Id, DateTimeOffset.Now, organizationId, branch.Id));
    context.SaveChanges();
    return Results.Created($"/api/phase1/organization/branches/{branch.Id}", branch);
});

phase1.MapGet("/organizations", (CareDbContext context, ITenantContext tenant) => Results.Ok(
    context.Organizations.AsNoTracking()
        .Where(organization => tenant.IsPlatformOwner || organization.Id == tenant.OrganizationId)
        .ToList()));

phase1.MapPost("/organizations", (CreateOrganizationRequest request, CareDbContext context, ITenantContext tenant) =>
{
    if (!tenant.IsPlatformOwner)
    {
        return Results.Forbid();
    }

    if (Missing(request.Name, request.Plan))
    {
        return Results.BadRequest(new { message = "Organization name and plan are required." });
    }

    var organization = new Organization(Guid.NewGuid(), request.Name.Trim(), request.Plan.Trim(), "Active");
    var branch = new Branch(Guid.NewGuid(), organization.Id, "Main Branch", "Primary", "Active");
    context.Organizations.Add(organization);
    context.Branches.Add(branch);
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "organization.created", "system", nameof(Organization), organization.Id, DateTimeOffset.Now, organization.Id, branch.Id));
    context.SaveChanges();
    return Results.Created($"/api/phase1/organizations/{organization.Id}", new { organization, branch });
});

phase1.MapGet("/storage/status", (IConfiguration configuration) => Results.Ok(new
{
    provider = configuration["Storage:Provider"] ?? "Local",
    root = configuration["Storage:RootPath"] ?? "App_Data/uploads",
    cloudReady = !string.Equals(configuration["Storage:Provider"], "Local", StringComparison.OrdinalIgnoreCase)
}));

app.Run();

static bool Missing(params string[] values) => values.Any(string.IsNullOrWhiteSpace);

static bool LooksLikeEmail(string value) => value.Contains('@', StringComparison.Ordinal) && value.Contains('.', StringComparison.Ordinal);

static bool TenantVisible(ITenantContext tenant, Guid? organizationId, Guid? branchId) => tenant.CanAccess(organizationId, branchId);

static async Task<string> UploadToLocalStorage(IFormFile file, string storedName, IWebHostEnvironment environment)
{
    var uploadRoot = Path.Combine(environment.ContentRootPath, "App_Data", "uploads");
    Directory.CreateDirectory(uploadRoot);
    var storagePath = Path.Combine(uploadRoot, storedName);
    await using var stream = File.Create(storagePath);
    await file.CopyToAsync(stream);
    return storagePath;
}

static async Task<string> UploadToSupabaseStorage(IFormFile file, string storedName, IConfiguration configuration, IHttpClientFactory httpClientFactory, ITenantContext tenant)
{
    var supabaseUrl = configuration["Supabase:Url"];
    var serviceRoleKey = configuration["Supabase:ServiceRoleKey"];
    var bucket = configuration["Supabase:Bucket"] ?? "care-documents";

    if (string.IsNullOrWhiteSpace(supabaseUrl) || string.IsNullOrWhiteSpace(serviceRoleKey))
    {
        throw new InvalidOperationException("Supabase storage is enabled, but Supabase:Url or Supabase:ServiceRoleKey is missing.");
    }

    var objectKey = $"{tenant.OrganizationId}/{tenant.BranchId?.ToString() ?? "org-wide"}/{DateTimeOffset.UtcNow:yyyy/MM}/{storedName}";
    var uploadUrl = $"{supabaseUrl.TrimEnd('/')}/storage/v1/object/{bucket}/{Uri.EscapeDataString(objectKey).Replace("%2F", "/", StringComparison.Ordinal)}";
    var client = httpClientFactory.CreateClient();
    using var content = new StreamContent(file.OpenReadStream());
    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType);
    using var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl)
    {
        Content = content
    };
    request.Headers.TryAddWithoutValidation("apikey", serviceRoleKey);
    request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {serviceRoleKey}");
    request.Headers.TryAddWithoutValidation("x-upsert", "false");

    using var response = await client.SendAsync(request);
    if (!response.IsSuccessStatusCode)
    {
        var detail = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException($"Supabase upload failed: {(int)response.StatusCode} {detail}");
    }

    return $"supabase://{bucket}/{objectKey}";
}

public sealed record TimelineItem(string Type, string Title, string Detail, DateTimeOffset When);
public sealed record GenerateReportRequest(string Name, string Format);
public sealed record BuildReportRequest(string Name, string Category, string Schedule, List<string> Metrics, List<string> Formats, Dictionary<string, string> Filters);
public sealed record SendNotificationRequest(string Channel, string Title, string Detail);
public sealed record InvestigateIncidentRequest(string Outcome, string ActionPlan, bool CloseIncident);
public sealed record AiSummaryRequest(Guid? ServiceUserId);
public sealed record CreateOrganizationRequest(string Name, string Plan);
public sealed record CreateBranchRequest(string Name, string Region, Guid? OrganizationId);
public sealed record FamilyPreferencesRequest(bool EmailNotifications, bool SmsNotifications, bool MonthlyDigest, bool IncidentAlerts);
public sealed record RecordPaymentRequest(decimal Amount, string Reference);
