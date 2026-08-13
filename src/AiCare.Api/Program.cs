using System.Text;
using System.Net.Http.Json;
using AiCare.Application;
using AiCare.Api;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

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
var connectionString = NormalizePostgresConnectionString(
    builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing DefaultConnection"));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();
builder.Services.AddScoped<ICurrentUserContext, HttpCurrentUserContext>();
builder.Services.AddHttpClient();
builder.Services.AddInfrastructure(connectionString);

var jwtOptions = builder.Configuration.GetSection("JwtOptions").Get<JwtOptions>() ?? throw new InvalidOperationException("Missing JwtOptions");
if (builder.Environment.IsEnvironment("Testing") && string.IsNullOrWhiteSpace(jwtOptions.SigningKey))
{
    jwtOptions.SigningKey = "test-signing-key-with-enough-length-for-hmac";
}
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

builder.Services.Configure<RouteHandlerOptions>(options =>
{
    options.ThrowOnBadRequest = true;
});

builder.Services.AddControllers();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<CareDbContext>();
    if (app.Environment.IsEnvironment("Testing"))
    {
        context.Database.EnsureCreated();
    }
    else
    {
        context.Database.Migrate();
        EnsureRuntimeSchema(context);
    }
}

app.UseCors("ReactClient");
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        if (exception is BadHttpRequestException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { message = "Request body or route values are invalid." });
            return;
        }

        if (exception is InvalidOperationException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { message = exception.Message });
            return;
        }

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new { message = "An unexpected error occurred." });
    });
});
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapGet("/", () => Results.Ok(new
{
    name = "AiCare API",
    phase = "Social care platform release pivot",
    status = "running"
}));

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "AiCare API",
    checkedAt = DateTimeOffset.UtcNow
}));

app.MapGet("/health/db", (CareDbContext context) =>
{
    try
    {
        return context.Database.CanConnect()
            ? Results.Ok(new { status = "healthy", provider = "PostgreSQL", checkedAt = DateTimeOffset.UtcNow })
            : Results.Problem("Database connection failed.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/health/storage", (IConfiguration configuration) =>
{
    var provider = configuration["Storage:Provider"] ?? "Local";
    var isSupabase = string.Equals(provider, "Supabase", StringComparison.OrdinalIgnoreCase);
    var ready = !isSupabase || (!string.IsNullOrWhiteSpace(configuration["Supabase:Url"]) &&
        !string.IsNullOrWhiteSpace(configuration["Supabase:ServiceRoleKey"]) &&
        !string.IsNullOrWhiteSpace(configuration["Supabase:Bucket"]));

    return ready
        ? Results.Ok(new { status = "healthy", provider, bucket = configuration["Supabase:Bucket"], checkedAt = DateTimeOffset.UtcNow })
        : Results.Problem("Supabase storage is selected but Supabase:Url, Supabase:ServiceRoleKey, or Supabase:Bucket is missing.", statusCode: StatusCodes.Status503ServiceUnavailable);
});

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

phase1.MapGet("/service-users", (ICareRepository repository, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    if (!currentUser.IsCareWorker)
    {
        return Results.Ok(repository.GetServiceUsers());
    }

    if (currentUser.CareWorkerId is null)
    {
        return Results.Ok(Array.Empty<ServiceUser>());
    }

    var serviceUserIds = context.Visits.AsNoTracking()
        .Where(visit => visit.CareWorkerId == currentUser.CareWorkerId)
        .AsEnumerable()
        .Where(visit => TenantVisible(tenant, visit.OrganizationId, visit.BranchId))
        .Select(visit => visit.ServiceUserId)
        .Distinct()
        .ToList();
    var serviceUsers = context.ServiceUsers.AsNoTracking()
        .Where(user => serviceUserIds.Contains(user.Id))
        .ToList();
    return Results.Ok(serviceUsers);
});
phase1.MapGet("/service-users/{id:guid}", (Guid id, ICareRepository repository) =>
{
    var serviceUser = repository.GetServiceUser(id);
    return serviceUser is null ? Results.NotFound() : Results.Ok(serviceUser);
});
phase1.MapPost("/service-users", (CreateServiceUserRequest request, ICareRepository repository) =>
{
    if (Missing(request.FullName, request.PhoneNumber, request.CareNeeds, request.EmergencyContact, request.PreferredCareWorker))
    {
        return Error("Full name, phone number, care needs, emergency contact, and preferred care worker are required.");
    }

    if (request.DateOfBirth > DateOnly.FromDateTime(DateTime.UtcNow))
    {
        return Error("Date of birth cannot be in the future.");
    }

    var serviceUser = repository.AddServiceUser(request);
    return Results.Created($"/api/phase1/service-users/{serviceUser.Id}", serviceUser);
});
phase1.MapPut("/service-users/{id:guid}", (Guid id, CreateServiceUserRequest request, ICareRepository repository) =>
{
    if (Missing(request.FullName, request.PhoneNumber, request.CareNeeds, request.EmergencyContact, request.PreferredCareWorker))
    {
        return Error("Full name, phone number, care needs, emergency contact, and preferred care worker are required.");
    }

    if (request.DateOfBirth > DateOnly.FromDateTime(DateTime.UtcNow))
    {
        return Error("Date of birth cannot be in the future.");
    }

    var serviceUser = repository.UpdateServiceUser(id, request);
    return serviceUser is null ? Results.NotFound() : Results.Ok(serviceUser);
});
phase1.MapDelete("/service-users/{id:guid}", (Guid id, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var serviceUser = context.ServiceUsers.Find(id);
    if (serviceUser is null || !tenant.CanAccess(serviceUser.OrganizationId, serviceUser.BranchId)) return Results.NotFound();
    context.ServiceUsers.Remove(serviceUser);
    AddAudit(context, tenant, currentUser, "service_user.deleted", nameof(ServiceUser), id);
    context.SaveChanges();
    return Results.NoContent();
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

phase1.MapPut("/service-users/{id:guid}/person-record", (Guid id, UpsertPersonRecordRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var person = context.ServiceUsers.FirstOrDefault(item => item.Id == id && item.OrganizationId == tenant.OrganizationId);
    if (person is null || !tenant.CanAccess(person.OrganizationId, person.BranchId)) return Results.NotFound();
    var existing = context.PersonRecords.FirstOrDefault(item => item.ServiceUserId == id && item.OrganizationId == tenant.OrganizationId);
    var updated = new PersonRecord(existing?.Id ?? Guid.NewGuid(), id, request.PreferredName, request.Pronouns, request.HealthIdentifier, request.GpDetails, request.PharmacyDetails, request.LegalRepresentative, request.ConsentStatus, request.MentalCapacityStatus, request.CommunicationPassport, request.PersonalHistory, request.WhatMattersToMe, request.DesiredOutcomes, request.AdvanceCareWishes, request.AdmittedAt, request.DischargedAt, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId);
    if (existing is null) context.PersonRecords.Add(updated); else context.Entry(existing).CurrentValues.SetValues(updated);
    AddAudit(context, tenant, currentUser, existing is null ? "person_record.created" : "person_record.updated", nameof(PersonRecord), updated.Id);
    context.SaveChanges();
    return Results.Ok(updated);
});

phase1.MapGet("/assessments", (Guid? serviceUserId, CareDbContext context, ITenantContext tenant) => Results.Ok(
    context.CareAssessments.AsNoTracking().Where(item => item.OrganizationId == tenant.OrganizationId && (serviceUserId == null || item.ServiceUserId == serviceUserId)).OrderByDescending(item => item.CompletedAt).ToList()));

phase1.MapPost("/assessments", (CreateCareAssessmentRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    try { System.Text.Json.JsonDocument.Parse(request.AnswersJson); } catch { return Error("AnswersJson must contain valid JSON."); }
    var assessment = new CareAssessment(Guid.NewGuid(), request.ServiceUserId, request.AssessmentType, request.TemplateVersion, "Completed", request.AnswersJson, request.Score, request.Risk, request.Summary, request.RecommendedActions, request.CompletedBy, DateTimeOffset.UtcNow, request.ReviewDueAt, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId);
    context.CareAssessments.Add(assessment);
    AddAudit(context, tenant, currentUser, "assessment.completed", nameof(CareAssessment), assessment.Id);
    context.SaveChanges();
    return Results.Created($"/api/phase1/assessments/{assessment.Id}", assessment);
});

phase1.MapPost("/care-plans/{id:guid}/outcomes", (Guid id, CreateCarePlanOutcomeRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var plan = context.CarePlans.AsNoTracking().FirstOrDefault(item => item.Id == id && item.OrganizationId == tenant.OrganizationId);
    if (plan is null || request.CarePlanId != id || request.ServiceUserId != plan.ServiceUserId) return Error("The outcome must belong to the selected care plan and person.");
    var outcome = new CarePlanOutcome(Guid.NewGuid(), id, plan.ServiceUserId, request.Goal, request.DesiredOutcome, request.Interventions, request.ResponsiblePerson, request.Measure, "Active", request.TargetDate, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId);
    context.CarePlanOutcomes.Add(outcome);
    AddAudit(context, tenant, currentUser, "care_plan.outcome_created", nameof(CarePlanOutcome), outcome.Id);
    context.SaveChanges();
    return Results.Created($"/api/phase1/care-plans/{id}/outcomes/{outcome.Id}", outcome);
});

phase1.MapPost("/care-plans/{id:guid}/approve", (Guid id, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var plan = context.CarePlans.FirstOrDefault(item => item.Id == id && item.OrganizationId == tenant.OrganizationId);
    if (plan is null) return Results.NotFound();
    var approved = plan with { Status = "Active" };
    context.Entry(plan).CurrentValues.SetValues(approved);
    AddAudit(context, tenant, currentUser, "care_plan.approved", nameof(CarePlan), id);
    context.SaveChanges();
    return Results.Ok(approved);
});

phase1.MapGet("/care-workers", (ICareRepository repository, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    if (!currentUser.IsCareWorker)
    {
        return Results.Ok(repository.GetCareWorkers());
    }

    if (currentUser.CareWorkerId is null)
    {
        return Results.Ok(Array.Empty<CareWorker>());
    }

    var worker = context.CareWorkers.AsNoTracking()
        .Where(item => item.Id == currentUser.CareWorkerId)
        .AsEnumerable()
        .Where(item => TenantVisible(tenant, item.OrganizationId, item.BranchId))
        .ToList();
    return Results.Ok(worker);
});
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
        return Error("Full name, specialization, and availability are required.");
    }

    var careWorker = repository.AddCareWorker(request);
    return Results.Created($"/api/phase1/care-workers/{careWorker.Id}", careWorker);
});
phase1.MapPut("/care-workers/{id:guid}", (Guid id, CreateCareWorkerRequest request, ICareRepository repository) =>
{
    if (Missing(request.FullName, request.Specialization, request.Availability))
    {
        return Error("Full name, specialization, and availability are required.");
    }

    var careWorker = repository.UpdateCareWorker(id, request);
    return careWorker is null ? Results.NotFound() : Results.Ok(careWorker);
});
phase1.MapDelete("/care-workers/{id:guid}", (Guid id, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var worker = context.CareWorkers.Find(id);
    if (worker is null || !tenant.CanAccess(worker.OrganizationId, worker.BranchId)) return Results.NotFound();
    context.CareWorkers.Remove(worker);
    AddAudit(context, tenant, currentUser, "care_worker.deleted", nameof(CareWorker), id);
    context.SaveChanges();
    return Results.NoContent();
});

phase1.MapGet("/visits", (ICareRepository repository, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    if (!currentUser.IsCareWorker)
    {
        return Results.Ok(repository.GetVisits());
    }

    if (currentUser.CareWorkerId is null)
    {
        return Results.Ok(Array.Empty<Visit>());
    }

    var visits = context.Visits.AsNoTracking()
        .Where(visit => visit.CareWorkerId == currentUser.CareWorkerId)
        .AsEnumerable()
        .Where(visit => TenantVisible(tenant, visit.OrganizationId, visit.BranchId))
        .ToList();
    return Results.Ok(visits);
});
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
phase1.MapPost("/visits", (CreateVisitRequest request, ICareRepository repository, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;

    if (request.ServiceUserId == Guid.Empty || request.CareWorkerId == Guid.Empty || string.IsNullOrWhiteSpace(request.VisitType) || request.DurationMinutes <= 0)
    {
        return Error("Service user, care worker, visit type, and a positive duration are required.");
    }

    var validation = ValidateVisitReferences(request.ServiceUserId, request.CareWorkerId, context, tenant);
    if (validation is not null) return validation;

    var visit = repository.AddVisit(request);
    return Results.Created($"/api/phase1/visits/{visit.Id}", visit);
});
phase1.MapPut("/visits/{id:guid}", (Guid id, CreateVisitRequest request, ICareRepository repository, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;

    if (request.ServiceUserId == Guid.Empty || request.CareWorkerId == Guid.Empty || string.IsNullOrWhiteSpace(request.VisitType) || request.DurationMinutes <= 0)
    {
        return Error("Service user, care worker, visit type, and a positive duration are required.");
    }

    var validation = ValidateVisitReferences(request.ServiceUserId, request.CareWorkerId, context, tenant);
    if (validation is not null) return validation;

    var visit = repository.UpdateVisit(id, request);
    return visit is null ? Results.NotFound() : Results.Ok(visit);
});
phase1.MapPatch("/visits/{id:guid}/status", (Guid id, UpdateVisitStatusRequest request, ICareRepository repository, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;

    var visit = repository.UpdateVisitStatus(id, request.Status);
    return visit is null ? Results.NotFound() : Results.Ok(visit);
});
phase1.MapDelete("/visits/{id:guid}", (Guid id, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;

    var visit = context.Visits.Find(id);
    if (visit is null || !tenant.CanAccess(visit.OrganizationId, visit.BranchId)) return Results.NotFound();
    context.Visits.Remove(visit);
    AddAudit(context, tenant, currentUser, "visit.deleted", nameof(Visit), id);
    context.SaveChanges();
    return Results.NoContent();
});
phase1.MapPost("/visits/{id:guid}/check-in", (Guid id, VisitCheckInRequest request, ICareRepository repository, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAssignedVisitForCareWorker(id, context, tenant, currentUser);
    if (denied is not null) return denied;

    var visit = repository.CheckInVisit(id, request);
    return visit is null ? Results.NotFound() : Results.Ok(visit);
});
phase1.MapPost("/visits/{id:guid}/check-out", (Guid id, VisitCheckOutRequest request, ICareRepository repository, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAssignedVisitForCareWorker(id, context, tenant, currentUser);
    if (denied is not null) return denied;

    var visit = repository.CheckOutVisit(id, request);
    return visit is null ? Results.NotFound() : Results.Ok(visit);
});

phase1.MapGet("/care-plans", (ICareRepository repository) => Results.Ok(repository.GetCarePlans()));
phase1.MapGet("/care-plans/{id:guid}", (Guid id, CareDbContext context, ITenantContext tenant) =>
{
    var carePlan = context.CarePlans.AsNoTracking().FirstOrDefault(item => item.Id == id);
    return carePlan is null || !tenant.CanAccess(carePlan.OrganizationId, carePlan.BranchId) ? Results.NotFound() : Results.Ok(carePlan);
});
phase1.MapPost("/care-plans", (CreateCarePlanRequest request, ICareRepository repository, CareDbContext context, ITenantContext tenant) =>
{
    if (request.ServiceUserId == Guid.Empty || Missing(request.PersonalCare, request.MedicationSupport, request.MobilityAndTransfers, request.Nutrition))
    {
        return Error("Service user and care plan details are required.");
    }

    var validation = ValidateServiceUserReference(request.ServiceUserId, context, tenant);
    if (validation is not null) return validation;

    var carePlan = repository.AddCarePlan(request);
    return Results.Created($"/api/phase1/care-plans/{carePlan.Id}", carePlan);
});
phase1.MapPut("/care-plans/{id:guid}", (Guid id, CreateCarePlanRequest request, ICareRepository repository, CareDbContext context, ITenantContext tenant) =>
{
    if (request.ServiceUserId == Guid.Empty || Missing(request.PersonalCare, request.MedicationSupport, request.MobilityAndTransfers, request.Nutrition))
    {
        return Error("Service user and care plan details are required.");
    }

    var validation = ValidateServiceUserReference(request.ServiceUserId, context, tenant);
    if (validation is not null) return validation;

    var carePlan = repository.UpdateCarePlan(id, request);
    return carePlan is null ? Results.NotFound() : Results.Ok(carePlan);
});
phase1.MapDelete("/care-plans/{id:guid}", (Guid id, ICareRepository repository) =>
    repository.DeleteCarePlan(id) ? Results.NoContent() : Results.NotFound());
phase1.MapGet("/risk-assessments", (ICareRepository repository) => Results.Ok(repository.GetRiskAssessments()));
phase1.MapGet("/risk-assessments/{id:guid}", (Guid id, CareDbContext context, ITenantContext tenant) =>
{
    var risk = context.RiskAssessments.AsNoTracking().FirstOrDefault(item => item.Id == id);
    return risk is null || !tenant.CanAccess(risk.OrganizationId, risk.BranchId) ? Results.NotFound() : Results.Ok(risk);
});
phase1.MapPost("/risk-assessments", (CreateRiskAssessmentRequest request, ICareRepository repository, CareDbContext context, ITenantContext tenant) =>
{
    if (request.ServiceUserId == Guid.Empty || Missing(request.Category, request.MitigationPlan))
    {
        return Error("Service user, category, and mitigation plan are required.");
    }

    var validation = ValidateServiceUserReference(request.ServiceUserId, context, tenant);
    if (validation is not null) return validation;

    var risk = repository.AddRiskAssessment(request);
    return Results.Created($"/api/phase1/risk-assessments/{risk.Id}", risk);
});
phase1.MapPut("/risk-assessments/{id:guid}", (Guid id, CreateRiskAssessmentRequest request, ICareRepository repository, CareDbContext context, ITenantContext tenant) =>
{
    if (request.ServiceUserId == Guid.Empty || Missing(request.Category, request.MitigationPlan))
    {
        return Error("Service user, category, and mitigation plan are required.");
    }

    var validation = ValidateServiceUserReference(request.ServiceUserId, context, tenant);
    if (validation is not null) return validation;

    var risk = repository.UpdateRiskAssessment(id, request);
    return risk is null ? Results.NotFound() : Results.Ok(risk);
});
phase1.MapDelete("/risk-assessments/{id:guid}", (Guid id, ICareRepository repository) =>
    repository.DeleteRiskAssessment(id) ? Results.NoContent() : Results.NotFound());
phase1.MapGet("/family-members", (ICareRepository repository) => Results.Ok(repository.GetFamilyMembers()));
phase1.MapGet("/family-members/{id:guid}", (Guid id, CareDbContext context, ITenantContext tenant) =>
{
    var family = context.FamilyMembers.AsNoTracking().FirstOrDefault(item => item.Id == id);
    return family is null || !tenant.CanAccess(family.OrganizationId, family.BranchId) ? Results.NotFound() : Results.Ok(family);
});
phase1.MapPost("/family-members", (CreateFamilyMemberRequest request, ICareRepository repository, CareDbContext context, ITenantContext tenant) =>
{
    if (request.ServiceUserId == Guid.Empty || Missing(request.FullName, request.Email, request.Relationship, request.AccessLevel) || !LooksLikeEmail(request.Email))
    {
        return Error("Valid family member contact details are required.");
    }

    var validation = ValidateServiceUserReference(request.ServiceUserId, context, tenant);
    if (validation is not null) return validation;

    var familyMember = repository.AddFamilyMember(request);
    return Results.Created($"/api/phase1/family-members/{familyMember.Id}", familyMember);
});
phase1.MapPut("/family-members/{id:guid}", (Guid id, CreateFamilyMemberRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    if (request.ServiceUserId == Guid.Empty || Missing(request.FullName, request.Email, request.Relationship, request.AccessLevel) || !LooksLikeEmail(request.Email))
    {
        return Error("Valid family member contact details are required.");
    }

    var family = context.FamilyMembers.Find(id);
    if (family is null || !tenant.CanAccess(family.OrganizationId, family.BranchId)) return Results.NotFound();
    var updated = family with { ServiceUserId = request.ServiceUserId, FullName = request.FullName, Email = request.Email, Relationship = request.Relationship, AccessLevel = request.AccessLevel };
    context.FamilyMembers.Update(updated);
    AddAudit(context, tenant, currentUser, "family_member.updated", nameof(FamilyMember), id);
    context.SaveChanges();
    return Results.Ok(updated);
});
phase1.MapDelete("/family-members/{id:guid}", (Guid id, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var family = context.FamilyMembers.Find(id);
    if (family is null || !tenant.CanAccess(family.OrganizationId, family.BranchId)) return Results.NotFound();
    context.FamilyMembers.Remove(family);
    AddAudit(context, tenant, currentUser, "family_member.deleted", nameof(FamilyMember), id);
    context.SaveChanges();
    return Results.NoContent();
});
phase1.MapGet("/documents", (ICareRepository repository) => Results.Ok(repository.GetDocuments()));
phase1.MapGet("/documents/{id:guid}", (Guid id, CareDbContext context, ITenantContext tenant) =>
{
    var document = context.Documents.AsNoTracking().FirstOrDefault(item => item.Id == id);
    return document is null || !tenant.CanAccess(document.OrganizationId, document.BranchId) ? Results.NotFound() : Results.Ok(document);
});
phase1.MapPost("/documents", (CreateDocumentRequest request, ICareRepository repository, CareDbContext context, ITenantContext tenant) =>
{
    if (request.ServiceUserId == Guid.Empty || Missing(request.FileName, request.Category, request.StoragePath, request.UploadedBy))
    {
        return Error("Document file name, category, storage path, and uploader are required.");
    }

    var validation = ValidateServiceUserReference(request.ServiceUserId, context, tenant);
    if (validation is not null) return validation;

    var document = repository.AddDocument(request);
    return Results.Created($"/api/phase1/documents/{document.Id}", document);
});
phase1.MapPost("/documents/upload", async (HttpRequest request, IWebHostEnvironment environment, IConfiguration configuration, IHttpClientFactory httpClientFactory, ICareRepository repository, ITenantContext tenant) =>
{
    if (!request.HasFormContentType)
    {
        return Error("Multipart form data is required.");
    }

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    var serviceUserIdValue = form["serviceUserId"].ToString();
    var category = form["category"].ToString();
    var uploadedBy = form["uploadedBy"].ToString();
    if (file is null || file.Length == 0 || !Guid.TryParse(serviceUserIdValue, out var serviceUserId) || Missing(category, uploadedBy))
    {
        return Error("File, service user, category, and uploader are required.");
    }

    if (file.Length > 10 * 1024 * 1024)
    {
        return Error("File uploads are limited to 10 MB for the demo backend.");
    }

    var safeFileName = SanitizeFileName(file.FileName);
    if (string.IsNullOrWhiteSpace(safeFileName))
    {
        return Error("A valid file name is required.");
    }

    var storedName = $"{Guid.NewGuid():N}-{safeFileName}";
    var storagePath = string.Equals(configuration["Storage:Provider"], "Supabase", StringComparison.OrdinalIgnoreCase)
        ? await UploadToSupabaseStorage(file, storedName, configuration, httpClientFactory, tenant)
        : await UploadToLocalStorage(file, storedName, environment);

    var document = repository.AddDocument(new CreateDocumentRequest(serviceUserId, safeFileName, category, storagePath, uploadedBy));
    return Results.Created($"/api/phase1/documents/{document.Id}", document);
});
phase1.MapGet("/documents/{id:guid}/download-url", async (Guid id, CareDbContext context, IConfiguration configuration, IHttpClientFactory httpClientFactory, ITenantContext tenant) =>
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
        var signedUrl = await CreateSupabaseSignedUrl(document.StoragePath, configuration, httpClientFactory);
        return Results.Ok(new { provider = "Supabase", url = signedUrl, expiresInSeconds = 900 });
    }

    var (_, objectKey) = ParseSupabaseStoragePath(document.StoragePath);
    return Results.Ok(new { provider = "Supabase", url = $"{publicBaseUrl.TrimEnd('/')}/{objectKey}" });
});
phase1.MapPut("/documents/{id:guid}", (Guid id, CreateDocumentRequest request, ICareRepository repository) =>
{
    var document = repository.UpdateDocument(id, request);
    return document is null ? Results.NotFound() : Results.Ok(document);
});
phase1.MapDelete("/documents/{id:guid}", (Guid id, ICareRepository repository) =>
    repository.DeleteDocument(id) ? Results.NoContent() : Results.NotFound());
phase1.MapGet("/medications", (ICareRepository repository) => Results.Ok(repository.GetMedications()));
phase1.MapGet("/medications/{id:guid}", (Guid id, CareDbContext context, ITenantContext tenant) =>
{
    var medication = context.Medications.AsNoTracking().FirstOrDefault(item => item.Id == id);
    return medication is null || !tenant.CanAccess(medication.OrganizationId, medication.BranchId) ? Results.NotFound() : Results.Ok(medication);
});
phase1.MapPost("/medications", (CreateMedicationRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;

    if (request.ServiceUserId == Guid.Empty || Missing(request.Name, request.Dosage, request.Route, request.Schedule))
    {
        return Error("Service user, medication name, dosage, route, and schedule are required.");
    }

    var validation = ValidateServiceUserReference(request.ServiceUserId, context, tenant);
    if (validation is not null) return validation;

    var medication = new Medication(Guid.NewGuid(), request.ServiceUserId, request.Name, request.Dosage, request.Route, request.Schedule, request.IsPrn, request.Pharmacy, request.AllergyWarning, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId);
    context.Medications.Add(medication);
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "medication.created", currentUser.UserName, nameof(Medication), medication.Id, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    context.SaveChanges();
    return Results.Created($"/api/phase1/medications/{medication.Id}", medication);
});
phase1.MapPut("/medications/{id:guid}", (Guid id, CreateMedicationRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;

    if (request.ServiceUserId == Guid.Empty || Missing(request.Name, request.Dosage, request.Route, request.Schedule))
    {
        return Error("Service user, medication name, dosage, route, and schedule are required.");
    }

    var validation = ValidateServiceUserReference(request.ServiceUserId, context, tenant);
    if (validation is not null) return validation;

    var medication = context.Medications.Find(id);
    if (medication is null || !tenant.CanAccess(medication.OrganizationId, medication.BranchId)) return Results.NotFound();

    var updated = medication with
    {
        ServiceUserId = request.ServiceUserId,
        Name = request.Name,
        Dosage = request.Dosage,
        Route = request.Route,
        Schedule = request.Schedule,
        IsPrn = request.IsPrn,
        Pharmacy = request.Pharmacy,
        AllergyWarning = request.AllergyWarning
    };
    context.Medications.Update(updated);
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "medication.updated", currentUser.UserName, nameof(Medication), id, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    context.SaveChanges();
    return Results.Ok(updated);
});
phase1.MapDelete("/medications/{id:guid}", (Guid id, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;

    var medication = context.Medications.Find(id);
    if (medication is null || !tenant.CanAccess(medication.OrganizationId, medication.BranchId)) return Results.NotFound();
    context.Medications.Remove(medication);
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "medication.deleted", currentUser.UserName, nameof(Medication), id, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    context.SaveChanges();
    return Results.NoContent();
});
phase1.MapGet("/mar", (ICareRepository repository) => Results.Ok(repository.GetMedicationAdministrationRecords()));
phase1.MapGet("/mar/{id:guid}", (Guid id, CareDbContext context, ITenantContext tenant) =>
{
    var record = context.MedicationAdministrationRecords.AsNoTracking().FirstOrDefault(item => item.Id == id);
    return record is null || !tenant.CanAccess(record.OrganizationId, record.BranchId) ? Results.NotFound() : Results.Ok(record);
});
phase1.MapPost("/mar", (CreateMedicationAdministrationRecordRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;

    var validation = ValidateMedicationAdministrationReferences(request.MedicationId, request.VisitId, request.CareWorkerId, context, tenant);
    if (validation is not null) return validation;

    var record = new MedicationAdministrationRecord(Guid.NewGuid(), request.MedicationId, request.VisitId, request.CareWorkerId, request.ScheduledAt, null, "Scheduled", request.Notes, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId);
    context.MedicationAdministrationRecords.Add(record);
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "emar.scheduled", currentUser.UserName, nameof(MedicationAdministrationRecord), record.Id, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    context.SaveChanges();
    return Results.Created($"/api/phase1/mar/{record.Id}", record);
});
phase1.MapPost("/mar/{id:guid}/administer", (Guid id, CompleteMedicationAdministrationRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
    CompleteMedicationAdministration(id, "Administered", request, context, tenant, currentUser));
phase1.MapPost("/mar/{id:guid}/skip", (Guid id, CompleteMedicationAdministrationRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
    CompleteMedicationAdministration(id, "Skipped", request, context, tenant, currentUser));
phase1.MapPost("/mar/{id:guid}/refuse", (Guid id, CompleteMedicationAdministrationRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
    CompleteMedicationAdministration(id, "Refused", request, context, tenant, currentUser));
phase1.MapGet("/care-notes", (ICareRepository repository, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    if (!currentUser.IsCareWorker)
    {
        return Results.Ok(repository.GetCareNotes());
    }

    if (currentUser.CareWorkerId is null)
    {
        return Results.Ok(Array.Empty<CareNote>());
    }

    var notes = context.CareNotes.AsNoTracking()
        .Where(note => note.CareWorkerId == currentUser.CareWorkerId)
        .AsEnumerable()
        .Where(note => TenantVisible(tenant, note.OrganizationId, note.BranchId))
        .ToList();
    return Results.Ok(notes);
});
phase1.MapGet("/care-notes/{id:guid}", (Guid id, CareDbContext context, ITenantContext tenant) =>
{
    var note = context.CareNotes.AsNoTracking().FirstOrDefault(item => item.Id == id);
    return note is null || !tenant.CanAccess(note.OrganizationId, note.BranchId) ? Results.NotFound() : Results.Ok(note);
});
phase1.MapPost("/care-notes", (CreateCareNoteRequest request, ICareRepository repository, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    if (request.VisitId == Guid.Empty || request.ServiceUserId == Guid.Empty || request.CareWorkerId == Guid.Empty || Missing(request.Summary))
    {
        return Error("Visit, service user, care worker, and summary are required.");
    }

    var validation = ValidateCareNoteReferences(request.VisitId, request.ServiceUserId, request.CareWorkerId, context, tenant);
    if (validation is not null) return validation;

    var denied = RequireAssignedVisitForCareWorker(request.VisitId, context, tenant, currentUser);
    if (denied is not null) return denied;

    var note = repository.AddCareNote(request);
    return Results.Created($"/api/phase1/care-notes/{note.Id}", note);
});
phase1.MapPut("/care-notes/{id:guid}", (Guid id, CreateCareNoteRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    if (request.VisitId == Guid.Empty || request.ServiceUserId == Guid.Empty || request.CareWorkerId == Guid.Empty || Missing(request.Summary))
    {
        return Error("Visit, service user, care worker, and summary are required.");
    }

    var validation = ValidateCareNoteReferences(request.VisitId, request.ServiceUserId, request.CareWorkerId, context, tenant);
    if (validation is not null) return validation;

    var denied = RequireAssignedVisitForCareWorker(request.VisitId, context, tenant, currentUser);
    if (denied is not null) return denied;

    var note = context.CareNotes.Find(id);
    if (note is null || !tenant.CanAccess(note.OrganizationId, note.BranchId)) return Results.NotFound();
    var updated = note with
    {
        VisitId = request.VisitId,
        ServiceUserId = request.ServiceUserId,
        CareWorkerId = request.CareWorkerId,
        Summary = request.Summary,
        PersonalCare = request.PersonalCare,
        MealsAndHydration = request.MealsAndHydration,
        Medication = request.Medication,
        Concerns = request.Concerns,
        RequiresReview = request.RequiresReview
    };
    context.CareNotes.Update(updated);
    AddAudit(context, tenant, currentUser, "care_note.updated", nameof(CareNote), id);
    context.SaveChanges();
    return Results.Ok(updated);
});
phase1.MapDelete("/care-notes/{id:guid}", (Guid id, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var note = context.CareNotes.Find(id);
    if (note is null || !tenant.CanAccess(note.OrganizationId, note.BranchId)) return Results.NotFound();
    var denied = RequireAssignedVisitForCareWorker(note.VisitId, context, tenant, currentUser);
    if (denied is not null) return denied;

    context.CareNotes.Remove(note);
    AddAudit(context, tenant, currentUser, "care_note.deleted", nameof(CareNote), id);
    context.SaveChanges();
    return Results.NoContent();
});
phase1.MapGet("/observations", (ICareRepository repository) => Results.Ok(repository.GetHealthObservations()));
phase1.MapGet("/incidents", (ICareRepository repository, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    if (!currentUser.IsCareWorker)
    {
        return Results.Ok(repository.GetIncidents());
    }

    if (currentUser.CareWorkerId is null)
    {
        return Results.Ok(Array.Empty<Incident>());
    }

    var assignedVisitIds = context.Visits.AsNoTracking()
        .Where(visit => visit.CareWorkerId == currentUser.CareWorkerId)
        .AsEnumerable()
        .Where(visit => TenantVisible(tenant, visit.OrganizationId, visit.BranchId))
        .Select(visit => visit.Id)
        .ToList();
    var incidents = context.Incidents.AsNoTracking()
        .Where(incident => incident.VisitId != null && assignedVisitIds.Contains(incident.VisitId.Value))
        .ToList();
    return Results.Ok(incidents);
});
phase1.MapGet("/incidents/{id:guid}", (Guid id, CareDbContext context, ITenantContext tenant) =>
{
    var incident = context.Incidents.AsNoTracking().FirstOrDefault(item => item.Id == id);
    return incident is null || !tenant.CanAccess(incident.OrganizationId, incident.BranchId) ? Results.NotFound() : Results.Ok(incident);
});
phase1.MapPost("/incidents", (CreateIncidentRequest request, ICareRepository repository, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    if (request.ServiceUserId == Guid.Empty || Missing(request.Category, request.Severity, request.Description))
    {
        return Error("Service user, category, severity, and description are required.");
    }

    var validation = ValidateServiceUserReference(request.ServiceUserId, context, tenant);
    if (validation is not null) return validation;

    var denied = RequireIncidentAccessForCareWorker(request, context, tenant, currentUser);
    if (denied is not null) return denied;

    var incident = repository.AddIncident(request);
    return Results.Created($"/api/phase1/incidents/{incident.Id}", incident);
});
phase1.MapPut("/incidents/{id:guid}", (Guid id, CreateIncidentRequest request, ICareRepository repository, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var incidentToUpdate = context.Incidents.AsNoTracking().FirstOrDefault(item => item.Id == id);
    if (incidentToUpdate is null || !tenant.CanAccess(incidentToUpdate.OrganizationId, incidentToUpdate.BranchId)) return Results.NotFound();

    var denied = RequireIncidentAccessForCareWorker(request, context, tenant, currentUser);
    if (denied is not null) return denied;

    var incident = repository.UpdateIncident(id, request);
    return incident is null ? Results.NotFound() : Results.Ok(incident);
});
phase1.MapDelete("/incidents/{id:guid}", (Guid id, ICareRepository repository, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var incident = context.Incidents.AsNoTracking().FirstOrDefault(item => item.Id == id);
    if (incident is null || !tenant.CanAccess(incident.OrganizationId, incident.BranchId)) return Results.NotFound();

    if (incident.VisitId is null && currentUser.IsCareWorker)
    {
        return Error("Care workers can only manage incidents linked to assigned visits.", StatusCodes.Status403Forbidden);
    }

    var denied = incident.VisitId is null ? null : RequireAssignedVisitForCareWorker(incident.VisitId.Value, context, tenant, currentUser);
    if (denied is not null) return denied;

    return repository.DeleteIncident(id) ? Results.NoContent() : Results.NotFound();
});
phase1.MapGet("/ai/risk-alerts", (ICareRepository repository) => Results.Ok(repository.GetAiRiskAlerts()));
phase1.MapGet("/payroll-runs", (CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, string? status = null) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.BackOffice);
    if (denied is not null) return denied;

    var payrollRuns = context.PayrollRuns.AsNoTracking()
        .AsEnumerable()
        .Where(payroll => TenantVisible(tenant, payroll.OrganizationId, payroll.BranchId))
        .Where(payroll => string.IsNullOrWhiteSpace(status) || string.Equals(payroll.Status, status, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(payroll => payroll.CreatedAt)
        .ToList();
    return Results.Ok(payrollRuns);
});
phase1.MapGet("/payroll-runs/{id:guid}", (Guid id, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.BackOffice);
    if (denied is not null) return denied;

    var payroll = context.PayrollRuns.AsNoTracking().FirstOrDefault(item => item.Id == id);
    return payroll is null || !tenant.CanAccess(payroll.OrganizationId, payroll.BranchId) ? Results.NotFound() : Results.Ok(payroll);
});
phase1.MapPost("/payroll-runs/generate", (ICareRepository repository, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.BackOffice);
    if (denied is not null) return denied;

    var payroll = repository.GeneratePayrollRun();
    return Results.Created($"/api/phase1/payroll-runs/{payroll.Id}", payroll);
});
phase1.MapGet("/payroll-runs/{id:guid}/export", (Guid id, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.BackOffice);
    if (denied is not null) return denied;

    var payroll = context.PayrollRuns.AsNoTracking().FirstOrDefault(item => item.Id == id);
    if (payroll is null || !tenant.CanAccess(payroll.OrganizationId, payroll.BranchId)) return Results.NotFound();

    var rows = new[]
    {
        "period,worker_count,gross_pay,status,created_at",
        $"{payroll.Period},{payroll.WorkerCount},{payroll.GrossPay},{payroll.Status},{payroll.CreatedAt:O}"
    };
    return Results.Text(string.Join(Environment.NewLine, rows), "text/csv");
});
phase1.MapGet("/invoices", (CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, string? status = null) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.BackOffice);
    if (denied is not null) return denied;

    var invoices = context.Invoices.AsNoTracking()
        .AsEnumerable()
        .Where(invoice => TenantVisible(tenant, invoice.OrganizationId, invoice.BranchId))
        .Where(invoice => string.IsNullOrWhiteSpace(status) || string.Equals(invoice.Status, status, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(invoice => invoice.IssuedAt)
        .ToList();
    return Results.Ok(invoices);
});
phase1.MapGet("/invoices/{id:guid}", (Guid id, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.BackOffice);
    if (denied is not null) return denied;

    var invoice = context.Invoices.AsNoTracking().FirstOrDefault(item => item.Id == id);
    return invoice is null || !tenant.CanAccess(invoice.OrganizationId, invoice.BranchId) ? Results.NotFound() : Results.Ok(invoice);
});
phase1.MapPost("/invoices/generate", (ICareRepository repository, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.BackOffice);
    return denied ?? Results.Ok(repository.GenerateInvoices());
});
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
phase1.MapPost("/reports/generate", (GenerateReportRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
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
    AddAudit(context, tenant, currentUser, "report.generated", "Report", null);
    context.SaveChanges();
    return Results.Ok(report);
});
phase1.MapPost("/reports/builder", (BuildReportRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    if (Missing(request.Name, request.Category) || request.Metrics.Count == 0)
    {
        return Error("Report name, category, and at least one metric are required.");
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
    AddAudit(context, tenant, currentUser, "report.definition_created", nameof(ReportDefinition), report.Id);
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
        return Error("Service user, care worker, subject, and message are required.");
    }

    var thread = repository.AddMessageThread(request);
    return Results.Created($"/api/phase1/messages/{thread.Id}", thread);
});

phase1.MapGet("/notifications", (CareDbContext context, ITenantContext tenant, bool unreadOnly = false) =>
{
    var notifications = context.Notifications.AsNoTracking()
        .AsEnumerable()
        .Where(notification => TenantVisible(tenant, notification.OrganizationId, notification.BranchId))
        .Where(notification => !unreadOnly || !notification.IsRead)
        .OrderByDescending(notification => notification.CreatedAt)
        .ToList();
    return Results.Ok(notifications);
});
phase1.MapGet("/notifications/unread-count", (CareDbContext context, ITenantContext tenant) =>
{
    var count = context.Notifications.AsNoTracking()
        .AsEnumerable()
        .Count(notification => TenantVisible(tenant, notification.OrganizationId, notification.BranchId) && !notification.IsRead);
    return Results.Ok(new { unread = count });
});
phase1.MapGet("/notifications/{id:guid}", (Guid id, CareDbContext context, ITenantContext tenant) =>
{
    var notification = context.Notifications.AsNoTracking().FirstOrDefault(item => item.Id == id);
    return notification is null || !tenant.CanAccess(notification.OrganizationId, notification.BranchId)
        ? Results.NotFound()
        : Results.Ok(notification);
});
phase1.MapPost("/notifications/send", (SendNotificationRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    if (Missing(request.Title, request.Detail, request.Channel))
    {
        return Error("Title, detail, and channel are required.");
    }

    var notification = new NotificationItem(Guid.NewGuid(), request.Title, $"{request.Channel}: {request.Detail}", DateTimeOffset.UtcNow, false, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId);
    context.Notifications.Add(notification);
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "notification.queued", currentUser.UserName, nameof(NotificationItem), notification.Id, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    context.SaveChanges();
    return Results.Accepted($"/api/phase1/notifications/{notification.Id}", notification);
});
phase1.MapPost("/notifications/{id:guid}/read", (Guid id, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var notification = context.Notifications.Find(id);
    if (notification is null || !tenant.CanAccess(notification.OrganizationId, notification.BranchId)) return Results.NotFound();

    var updated = notification with { IsRead = true };
    context.Entry(notification).CurrentValues.SetValues(updated);
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "notification.read", currentUser.UserName, nameof(NotificationItem), id, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    context.SaveChanges();
    return Results.Ok(updated);
});
phase1.MapPost("/notifications/{id:guid}/unread", (Guid id, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var notification = context.Notifications.Find(id);
    if (notification is null || !tenant.CanAccess(notification.OrganizationId, notification.BranchId)) return Results.NotFound();

    var updated = notification with { IsRead = false };
    context.Entry(notification).CurrentValues.SetValues(updated);
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "notification.unread", currentUser.UserName, nameof(NotificationItem), id, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    context.SaveChanges();
    return Results.Ok(updated);
});
phase1.MapPost("/notifications/read-all", (CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var notifications = context.Notifications
        .AsEnumerable()
        .Where(notification => TenantVisible(tenant, notification.OrganizationId, notification.BranchId) && !notification.IsRead)
        .ToList();

    foreach (var notification in notifications)
    {
        context.Entry(notification).CurrentValues.SetValues(notification with { IsRead = true });
    }

    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "notification.read_all", currentUser.UserName, nameof(NotificationItem), null, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    context.SaveChanges();
    return Results.Ok(new { updated = notifications.Count });
});
phase1.MapDelete("/notifications/{id:guid}", (Guid id, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var notification = context.Notifications.Find(id);
    if (notification is null || !tenant.CanAccess(notification.OrganizationId, notification.BranchId)) return Results.NotFound();

    context.Notifications.Remove(notification);
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "notification.deleted", currentUser.UserName, nameof(NotificationItem), id, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    context.SaveChanges();
    return Results.NoContent();
});

phase1.MapGet("/admin/users", (ICareRepository repository, ICurrentUserContext currentUser) =>
{
    var denied = RequireAdministrator(currentUser);
    return denied ?? Results.Ok(repository.GetAdminUsers());
});
phase1.MapPost("/admin/users", (CreateAdminUserRequest request, ICareRepository repository, ICurrentUserContext currentUser, CareDbContext context, ITenantContext tenant) =>
{
    var denied = RequireAdministrator(currentUser);
    if (denied is not null) return denied;

    if (Missing(request.UserName, request.Email, request.Password) || !LooksLikeEmail(request.Email))
    {
        return Error("Username, valid email, and password are required.");
    }

    if (request.Password.Length < 10)
    {
        return Error("Password must be at least 10 characters.");
    }

    if (request.Role == UserRole.CareWorker)
    {
        if (request.CareWorkerId is null)
        {
            return Error("Care worker accounts must be linked to a care worker profile.");
        }

        var workerValidation = ValidateCareWorkerReference(request.CareWorkerId.Value, context, tenant);
        if (workerValidation is not null) return workerValidation;
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
phase1.MapPatch("/admin/users/{id:guid}/role", (Guid id, UpdateUserRoleRequest request, ICareRepository repository, ICurrentUserContext currentUser) =>
{
    var denied = RequireAdministrator(currentUser);
    if (denied is not null) return denied;

    var user = repository.UpdateUserRole(id, request.Role);
    return user is null ? Results.NotFound() : Results.Ok(user);
});

phase1.MapGet("/audit-events", (ICareRepository repository, ICurrentUserContext currentUser) =>
{
    var denied = RequireAdministrator(currentUser);
    return denied ?? Results.Ok(repository.GetAuditEvents());
});

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

phase1.MapPost("/family/service-users/{id:guid}/preferences", (Guid id, FamilyPreferencesRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var serviceUser = context.ServiceUsers.AsNoTracking().FirstOrDefault(user => user.Id == id);
    if (serviceUser is null || !tenant.CanAccess(serviceUser.OrganizationId, serviceUser.BranchId))
    {
        return Results.NotFound();
    }

    AddAudit(context, tenant, currentUser, "family.preferences_updated", nameof(ServiceUser), id);
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

phase1.MapPost("/incidents/{id:guid}/investigate", (Guid id, InvestigateIncidentRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    if (Missing(request.Outcome, request.ActionPlan))
    {
        return Error("Outcome and action plan are required.");
    }

    var incident = context.Incidents.Find(id);
    if (incident is null || !tenant.CanAccess(incident.OrganizationId, incident.BranchId))
    {
        return Results.NotFound();
    }

    var updated = incident with { Status = request.CloseIncident ? "Closed" : "Under investigation", Description = $"{incident.Description}\nInvestigation outcome: {request.Outcome}\nAction plan: {request.ActionPlan}" };
    context.Entry(incident).CurrentValues.SetValues(updated);
    AddAudit(context, tenant, currentUser, "incident.investigated", nameof(Incident), id);
    context.SaveChanges();
    return Results.Ok(updated);
});

phase1.MapPost("/payroll-runs/{id:guid}/approve", (Guid id, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.BackOffice);
    if (denied is not null) return denied;

    var payroll = context.PayrollRuns.Find(id);
    if (payroll is null || !tenant.CanAccess(payroll.OrganizationId, payroll.BranchId))
    {
        return Results.NotFound();
    }

    if (string.Equals(payroll.Status, "Approved", StringComparison.OrdinalIgnoreCase))
    {
        return Error("Payroll run is already approved.");
    }

    var updated = payroll with { Status = "Approved" };
    context.Entry(payroll).CurrentValues.SetValues(updated);
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "payroll.approved", currentUser.UserName, nameof(PayrollRun), id, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    context.SaveChanges();
    return Results.Ok(updated);
});
phase1.MapPost("/payroll-runs/{id:guid}/reject", (Guid id, RejectFinancialRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.BackOffice);
    if (denied is not null) return denied;

    if (Missing(request.Reason))
    {
        return Error("A rejection reason is required.");
    }

    var payroll = context.PayrollRuns.Find(id);
    if (payroll is null || !tenant.CanAccess(payroll.OrganizationId, payroll.BranchId)) return Results.NotFound();

    var updated = payroll with { Status = "Rejected" };
    context.Entry(payroll).CurrentValues.SetValues(updated);
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), $"payroll.rejected: {request.Reason}", currentUser.UserName, nameof(PayrollRun), id, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    context.SaveChanges();
    return Results.Ok(updated);
});

phase1.MapGet("/timesheets", (CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.BackOffice);
    if (denied is not null) return denied;

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

phase1.MapGet("/invoices/{id:guid}/lines", (Guid id, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.BackOffice);
    if (denied is not null) return denied;

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

phase1.MapPost("/invoices/{id:guid}/record-payment", (Guid id, RecordPaymentRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.BackOffice);
    if (denied is not null) return denied;

    var invoice = context.Invoices.Find(id);
    if (invoice is null || !tenant.CanAccess(invoice.OrganizationId, invoice.BranchId))
    {
        return Results.NotFound();
    }

    if (request.Amount <= 0 || Missing(request.Reference))
    {
        return Error("Payment amount and reference are required.");
    }

    if (string.Equals(invoice.Status, "Void", StringComparison.OrdinalIgnoreCase))
    {
        return Error("Void invoices cannot receive payments.");
    }

    var updated = invoice with { Status = request.Amount >= invoice.Amount ? "Paid" : "Part paid" };
    context.Entry(invoice).CurrentValues.SetValues(updated);
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), $"invoice.payment_recorded: {request.Reference}", currentUser.UserName, nameof(Invoice), id, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    context.SaveChanges();
    return Results.Ok(new { invoice = updated, request.Amount, request.Reference, paidAt = DateTimeOffset.UtcNow });
});

phase1.MapPost("/invoices/{id:guid}/approve", (Guid id, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.BackOffice);
    if (denied is not null) return denied;

    var invoice = context.Invoices.Find(id);
    if (invoice is null || !tenant.CanAccess(invoice.OrganizationId, invoice.BranchId))
    {
        return Results.NotFound();
    }

    if (string.Equals(invoice.Status, "Paid", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(invoice.Status, "Void", StringComparison.OrdinalIgnoreCase))
    {
        return Error("Paid or void invoices cannot be approved.");
    }

    var updated = invoice with { Status = "Approved" };
    context.Entry(invoice).CurrentValues.SetValues(updated);
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "invoice.approved", currentUser.UserName, nameof(Invoice), id, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    context.SaveChanges();
    return Results.Ok(updated);
});
phase1.MapPost("/invoices/{id:guid}/void", (Guid id, RejectFinancialRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.BackOffice);
    if (denied is not null) return denied;

    if (Missing(request.Reason))
    {
        return Error("A void reason is required.");
    }

    var invoice = context.Invoices.Find(id);
    if (invoice is null || !tenant.CanAccess(invoice.OrganizationId, invoice.BranchId)) return Results.NotFound();

    if (string.Equals(invoice.Status, "Paid", StringComparison.OrdinalIgnoreCase))
    {
        return Error("Paid invoices cannot be voided.");
    }

    var updated = invoice with { Status = "Void" };
    context.Entry(invoice).CurrentValues.SetValues(updated);
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), $"invoice.voided: {request.Reason}", currentUser.UserName, nameof(Invoice), id, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    context.SaveChanges();
    return Results.Ok(updated);
});

phase1.MapPost("/ai/summarize-notes", (AiSummaryRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var notes = context.CareNotes.AsNoTracking()
        .Where(note => (request.ServiceUserId == null || note.ServiceUserId == request.ServiceUserId) && (tenant.IsPlatformOwner || note.OrganizationId == tenant.OrganizationId) && (tenant.IsOrganizationWide || tenant.BranchId == null || note.BranchId == tenant.BranchId))
        .OrderByDescending(note => note.CreatedAt)
        .Take(10)
        .ToList();

    var summary = notes.Count == 0
        ? "No recent care notes are available for summarization."
        : $"AI draft summary based on {notes.Count} recent notes: {string.Join(" ", notes.Select(note => note.Summary)).Trim()}";
    AddAudit(context, tenant, currentUser, "ai.summary_generated", "AiInteraction", request.ServiceUserId);
    context.SaveChanges();
    return Results.Ok(new { summary, humanReviewRequired = true, generatedAt = DateTimeOffset.Now });
});

phase1.MapPost("/ai/detect-risks", (AiSummaryRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var serviceUserId = request.ServiceUserId ?? context.ServiceUsers.AsNoTracking()
        .Where(user => tenant.IsPlatformOwner || user.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || tenant.BranchId == null || user.BranchId == tenant.BranchId))
        .Select(user => user.Id)
        .FirstOrDefault();
    if (serviceUserId == Guid.Empty)
    {
        return Error("A service user is required before AI risk detection can run.");
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
    AddAudit(context, tenant, currentUser, "ai.risk_detected", nameof(AiRiskAlert), alert.Id);
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

phase1.MapPost("/organization/branches", (CreateBranchRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    if (Missing(request.Name, request.Region))
    {
        return Error("Branch name and region are required.");
    }

    var organizationId = request.OrganizationId ?? tenant.OrganizationId;
    if (!tenant.IsPlatformOwner && organizationId != tenant.OrganizationId)
    {
        return Results.Forbid();
    }

    var organizationExists = context.Organizations.Any(organization => organization.Id == organizationId);
    if (!organizationExists)
    {
        return Error("Organization does not exist.");
    }

    var branch = new Branch(Guid.NewGuid(), organizationId, request.Name.Trim(), request.Region.Trim(), "Active");
    context.Branches.Add(branch);
    AddAudit(context, tenant, currentUser, "branch.created", nameof(Branch), branch.Id, organizationId, branch.Id);
    context.SaveChanges();
    return Results.Created($"/api/phase1/organization/branches/{branch.Id}", branch);
});

phase1.MapGet("/organizations", (CareDbContext context, ITenantContext tenant) => Results.Ok(
    context.Organizations.AsNoTracking()
        .Where(organization => tenant.IsPlatformOwner || organization.Id == tenant.OrganizationId)
        .ToList()));

phase1.MapPost("/organizations", (CreateOrganizationRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    if (!tenant.IsPlatformOwner)
    {
        return Results.Forbid();
    }

    if (Missing(request.Name, request.Plan))
    {
        return Error("Organization name and plan are required.");
    }

    var organization = new Organization(Guid.NewGuid(), request.Name.Trim(), request.Plan.Trim(), "Active");
    var branch = new Branch(Guid.NewGuid(), organization.Id, "Main Branch", "Primary", "Active");
    context.Organizations.Add(organization);
    context.Branches.Add(branch);
    AddAudit(context, tenant, currentUser, "organization.created", nameof(Organization), organization.Id, organization.Id, branch.Id);
    context.SaveChanges();
    return Results.Created($"/api/phase1/organizations/{organization.Id}", new { organization, branch });
});

phase1.MapGet("/storage/status", (IConfiguration configuration) => Results.Ok(new
{
    provider = configuration["Storage:Provider"] ?? "Local",
    root = configuration["Storage:RootPath"] ?? "App_Data/uploads",
    cloudReady = !string.Equals(configuration["Storage:Provider"], "Local", StringComparison.OrdinalIgnoreCase)
}));

var demo = app.MapGroup("/api/demo").RequireAuthorization("Phase1User");
demo.MapPost("/seed", (HttpContext httpContext, IConfiguration configuration, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    if (!DemoAccessAllowed(httpContext, configuration))
    {
        return Results.NotFound();
    }

    var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
    var serviceUser = new ServiceUser(Guid.NewGuid(), $"Demo Service User {stamp}", new DateOnly(1970, 1, 1), "+10000000000", "Demo care needs only", "Demo Contact +10000000001", "Demo Care Worker", RiskLevel.Medium, "Onboarded", "Demo address", "None", "Demo condition", "Demo funding", "Demo", "", "Demo mobility", "Demo cognition", "Demo communication", "Demo preferences", "Demo diet", tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId);
    var worker = new CareWorker(Guid.NewGuid(), $"Demo Care Worker {stamp}", "Demo support", "Weekdays demo availability", 0, 0, "Demo clear", "Demo complete", "Demo radius", tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId);
    var visit = new Visit(Guid.NewGuid(), serviceUser.Id, worker.Id, DateTimeOffset.UtcNow.AddDays(1), "Demo visit", 30, "Demo skills", VisitStatus.Scheduled, null, null, null, null, null, null, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId);
    var carePlan = new CarePlan(Guid.NewGuid(), serviceUser.Id, "v1", "Draft", "Demo personal care.", "Demo medication support.", "Demo mobility support.", "Demo nutrition.", DateTimeOffset.UtcNow.AddDays(30), tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId);
    var risk = new RiskAssessment(Guid.NewGuid(), serviceUser.Id, "Demo risk", RiskLevel.Medium, "Demo mitigation plan.", DateTimeOffset.UtcNow.AddDays(14), tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId);
    var family = new FamilyMember(Guid.NewGuid(), serviceUser.Id, $"Demo Family Member {stamp}", $"demo.family.{stamp}@example.com", "Demo relationship", "Demo access", "Invited", tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId);
    var document = new DocumentItem(Guid.NewGuid(), serviceUser.Id, $"demo-document-{stamp}.txt", "Demo document", $"supabase://care-documents/demo/demo-document-{stamp}.txt", "demo-admin", DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId);
    var note = new CareNote(Guid.NewGuid(), visit.Id, serviceUser.Id, worker.Id, "Demo care note summary.", "Demo personal care completed.", "Demo meal note.", "Demo medication note.", "Demo concern only.", false, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId);
    var incident = new Incident(Guid.NewGuid(), serviceUser.Id, visit.Id, "Demo incident", "Low", "Demo incident description only.", "Reported", DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId);
    var message = new MessageThread(Guid.NewGuid(), serviceUser.Id, worker.Id, "Demo message", MessagePriority.Routine, "Demo message body.", DateTimeOffset.UtcNow.AddMinutes(30), tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId);

    context.AddRange(serviceUser, worker, visit, carePlan, risk, family, document, note, incident, message);
    AddAudit(context, tenant, currentUser, "demo.seeded", "DemoData", serviceUser.Id);
    context.SaveChanges();

    return Results.Created("/api/demo/seed", new
    {
        serviceUserId = serviceUser.Id,
        careWorkerId = worker.Id,
        visitId = visit.Id,
        carePlanId = carePlan.Id,
        riskAssessmentId = risk.Id,
        familyMemberId = family.Id,
        documentId = document.Id,
        careNoteId = note.Id,
        incidentId = incident.Id,
        messageThreadId = message.Id
    });
});

demo.MapDelete("/reset", (HttpContext httpContext, IConfiguration configuration, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    if (!DemoAccessAllowed(httpContext, configuration))
    {
        return Results.NotFound();
    }

    var demoServiceUserIds = context.ServiceUsers
        .Where(item => item.FullName.StartsWith("Demo Service User") && item.OrganizationId == tenant.OrganizationId)
        .Select(item => item.Id)
        .ToList();
    var demoWorkerIds = context.CareWorkers
        .Where(item => item.FullName.StartsWith("Demo Care Worker") && item.OrganizationId == tenant.OrganizationId)
        .Select(item => item.Id)
        .ToList();

    context.MessageThreads.RemoveRange(context.MessageThreads.Where(item => demoServiceUserIds.Contains(item.ServiceUserId) || demoWorkerIds.Contains(item.CareWorkerId)));
    context.Incidents.RemoveRange(context.Incidents.Where(item => demoServiceUserIds.Contains(item.ServiceUserId)));
    context.CareNotes.RemoveRange(context.CareNotes.Where(item => demoServiceUserIds.Contains(item.ServiceUserId) || demoWorkerIds.Contains(item.CareWorkerId)));
    context.Documents.RemoveRange(context.Documents.Where(item => demoServiceUserIds.Contains(item.ServiceUserId) && item.FileName.StartsWith("demo-document-")));
    context.FamilyMembers.RemoveRange(context.FamilyMembers.Where(item => demoServiceUserIds.Contains(item.ServiceUserId) && item.FullName.StartsWith("Demo Family Member")));
    context.RiskAssessments.RemoveRange(context.RiskAssessments.Where(item => demoServiceUserIds.Contains(item.ServiceUserId) && item.Category.StartsWith("Demo")));
    context.CarePlans.RemoveRange(context.CarePlans.Where(item => demoServiceUserIds.Contains(item.ServiceUserId)));
    context.Visits.RemoveRange(context.Visits.Where(item => demoServiceUserIds.Contains(item.ServiceUserId) || demoWorkerIds.Contains(item.CareWorkerId)));
    context.CareWorkers.RemoveRange(context.CareWorkers.Where(item => demoWorkerIds.Contains(item.Id)));
    context.ServiceUsers.RemoveRange(context.ServiceUsers.Where(item => demoServiceUserIds.Contains(item.Id)));
    AddAudit(context, tenant, currentUser, "demo.reset", "DemoData", null);
    var removed = context.SaveChanges();
    return Results.Ok(new { removedChanges = removed, serviceUsers = demoServiceUserIds.Count, careWorkers = demoWorkerIds.Count });
});

app.Run();

static bool Missing(params string[] values) => values.Any(string.IsNullOrWhiteSpace);

static IResult Error(string message, int statusCode = StatusCodes.Status400BadRequest) =>
    Results.Json(new { message }, statusCode: statusCode);

static IResult? RequireAdministrator(ICurrentUserContext currentUser) =>
    currentUser.IsAdministrator ? null : Error("Administrator access is required.", StatusCodes.Status403Forbidden);

static IResult? RequireAnyRole(ICurrentUserContext currentUser, params UserRole[] roles) =>
    currentUser.HasAnyRole(roles) ? null : Error("You do not have permission to access this resource.", StatusCodes.Status403Forbidden);

static void AddAudit(CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, string action, string entityType, Guid? entityId, Guid? organizationId = null, Guid? branchId = null)
{
    var actor = string.IsNullOrWhiteSpace(currentUser.UserName) ? "system" : currentUser.UserName;
    context.AuditEvents.Add(new AuditEvent(
        Guid.NewGuid(),
        action,
        actor,
        entityType,
        entityId,
        DateTimeOffset.UtcNow,
        organizationId ?? tenant.OrganizationId,
        branchId ?? tenant.BranchId ?? TenantDefaults.BranchId));
}

static bool LooksLikeEmail(string value) => value.Contains('@', StringComparison.Ordinal) && value.Contains('.', StringComparison.Ordinal);

static bool TenantVisible(ITenantContext tenant, Guid? organizationId, Guid? branchId) => tenant.CanAccess(organizationId, branchId);

static IResult? RequireAssignedVisitForCareWorker(Guid visitId, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser)
{
    if (!currentUser.IsCareWorker)
    {
        return null;
    }

    if (currentUser.CareWorkerId is null)
    {
        return Error("Care worker account is not linked to a care worker profile.", StatusCodes.Status403Forbidden);
    }

    var visit = context.Visits.AsNoTracking().FirstOrDefault(item => item.Id == visitId);
    if (visit is null || !tenant.CanAccess(visit.OrganizationId, visit.BranchId))
    {
        return Results.NotFound();
    }

    return visit.CareWorkerId == currentUser.CareWorkerId
        ? null
        : Error("Care workers can only access their assigned visits.", StatusCodes.Status403Forbidden);
}

static IResult? RequireIncidentAccessForCareWorker(CreateIncidentRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser)
{
    if (!currentUser.IsCareWorker)
    {
        return null;
    }

    if (request.VisitId is null)
    {
        return Error("Care workers can only manage incidents linked to assigned visits.", StatusCodes.Status403Forbidden);
    }

    var denied = RequireAssignedVisitForCareWorker(request.VisitId.Value, context, tenant, currentUser);
    if (denied is not null) return denied;

    var visit = context.Visits.AsNoTracking().FirstOrDefault(item => item.Id == request.VisitId.Value);
    if (visit is null)
    {
        return Results.NotFound();
    }

    return visit.ServiceUserId == request.ServiceUserId
        ? null
        : Error("Incident service user must match the assigned visit.", StatusCodes.Status403Forbidden);
}

static IResult? ValidateServiceUserReference(Guid serviceUserId, CareDbContext context, ITenantContext tenant)
{
    var serviceUser = context.ServiceUsers.AsNoTracking().FirstOrDefault(item => item.Id == serviceUserId);
    if (serviceUser is null || !tenant.CanAccess(serviceUser.OrganizationId, serviceUser.BranchId))
    {
        return Error("Service user does not exist or is not accessible.");
    }

    return null;
}

static IResult? ValidateCareWorkerReference(Guid careWorkerId, CareDbContext context, ITenantContext tenant)
{
    var careWorker = context.CareWorkers.AsNoTracking().FirstOrDefault(item => item.Id == careWorkerId);
    if (careWorker is null || !tenant.CanAccess(careWorker.OrganizationId, careWorker.BranchId))
    {
        return Error("Care worker does not exist or is not accessible.");
    }

    return null;
}

static IResult? ValidateVisitReferences(Guid serviceUserId, Guid careWorkerId, CareDbContext context, ITenantContext tenant)
{
    return ValidateServiceUserReference(serviceUserId, context, tenant)
        ?? ValidateCareWorkerReference(careWorkerId, context, tenant);
}

static IResult? ValidateCareNoteReferences(Guid visitId, Guid serviceUserId, Guid careWorkerId, CareDbContext context, ITenantContext tenant)
{
    var visit = context.Visits.AsNoTracking().FirstOrDefault(item => item.Id == visitId);
    if (visit is null || !tenant.CanAccess(visit.OrganizationId, visit.BranchId))
    {
        return Error("Visit does not exist or is not accessible.");
    }

    if (visit.ServiceUserId != serviceUserId || visit.CareWorkerId != careWorkerId)
    {
        return Error("Care note visit, service user, and care worker must match.");
    }

    return ValidateVisitReferences(serviceUserId, careWorkerId, context, tenant);
}

static IResult? ValidateMedicationAdministrationReferences(Guid medicationId, Guid visitId, Guid careWorkerId, CareDbContext context, ITenantContext tenant)
{
    var medication = context.Medications.AsNoTracking().FirstOrDefault(item => item.Id == medicationId);
    if (medication is null || !tenant.CanAccess(medication.OrganizationId, medication.BranchId))
    {
        return Error("Medication does not exist or is not accessible.");
    }

    var visit = context.Visits.AsNoTracking().FirstOrDefault(item => item.Id == visitId);
    if (visit is null || !tenant.CanAccess(visit.OrganizationId, visit.BranchId))
    {
        return Error("Visit does not exist or is not accessible.");
    }

    if (visit.CareWorkerId != careWorkerId)
    {
        return Error("Medication record care worker must match the visit.");
    }

    if (visit.ServiceUserId != medication.ServiceUserId)
    {
        return Error("Medication and visit must belong to the same service user.");
    }

    return ValidateCareWorkerReference(careWorkerId, context, tenant);
}

static IResult CompleteMedicationAdministration(Guid id, string outcome, CompleteMedicationAdministrationRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser)
{
    var record = context.MedicationAdministrationRecords.Find(id);
    if (record is null || !tenant.CanAccess(record.OrganizationId, record.BranchId)) return Results.NotFound();

    var denied = RequireAssignedVisitForCareWorker(record.VisitId, context, tenant, currentUser);
    if (denied is not null) return denied;

    var completed = record with
    {
        AdministeredAt = request.AdministeredAt ?? DateTimeOffset.UtcNow,
        Outcome = outcome,
        Notes = request.Notes
    };
    context.Entry(record).CurrentValues.SetValues(completed);
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), $"emar.{outcome.ToLowerInvariant()}", currentUser.UserName, nameof(MedicationAdministrationRecord), id, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    context.SaveChanges();
    return Results.Ok(completed);
}

static bool DemoAccessAllowed(HttpContext httpContext, IConfiguration configuration)
{
    if (!string.Equals(configuration["Demo:Enabled"], "true", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var expectedKey = configuration["Demo:SeedKey"];
    return !string.IsNullOrWhiteSpace(expectedKey) &&
        httpContext.Request.Headers.TryGetValue("X-Demo-Key", out var providedKey) &&
        string.Equals(providedKey.ToString(), expectedKey, StringComparison.Ordinal);
}

static string SanitizeFileName(string fileName)
{
    var safeName = Path.GetFileName(fileName);
    foreach (var invalidChar in Path.GetInvalidFileNameChars())
    {
        safeName = safeName.Replace(invalidChar, '-');
    }

    return safeName.Trim();
}

static string NormalizePostgresConnectionString(string connectionString)
{
    if (!Uri.TryCreate(connectionString, UriKind.Absolute, out var uri) ||
        (uri.Scheme != "postgres" && uri.Scheme != "postgresql"))
    {
        return connectionString;
    }

    var credentials = uri.UserInfo.Split(':', 2);
    var builder = new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.Port > 0 ? uri.Port : 5432,
        Database = uri.AbsolutePath.TrimStart('/'),
        Username = Uri.UnescapeDataString(credentials.ElementAtOrDefault(0) ?? string.Empty),
        Password = Uri.UnescapeDataString(credentials.ElementAtOrDefault(1) ?? string.Empty),
        SslMode = SslMode.Require
    };

    return builder.ConnectionString;
}

static void EnsureRuntimeSchema(CareDbContext context)
{
    if (!context.Database.IsNpgsql())
    {
        return;
    }

    context.Database.ExecuteSqlRaw("""
        ALTER TABLE "AppUsers"
        ADD COLUMN IF NOT EXISTS "CareWorkerId" uuid;
        """);

    context.Database.ExecuteSqlRaw("""
        CREATE INDEX IF NOT EXISTS "IX_AppUsers_CareWorkerId"
        ON "AppUsers" ("CareWorkerId");
        """);
}

static (string Bucket, string ObjectKey) ParseSupabaseStoragePath(string storagePath)
{
    var path = storagePath.Replace("supabase://", "", StringComparison.OrdinalIgnoreCase);
    var splitAt = path.IndexOf('/', StringComparison.Ordinal);
    if (splitAt < 1 || splitAt == path.Length - 1)
    {
        throw new InvalidOperationException("Supabase storage path must be formatted as supabase://bucket/object-key.");
    }

    return (path[..splitAt], path[(splitAt + 1)..]);
}

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

static async Task<string> CreateSupabaseSignedUrl(string storagePath, IConfiguration configuration, IHttpClientFactory httpClientFactory)
{
    var supabaseUrl = configuration["Supabase:Url"];
    var serviceRoleKey = configuration["Supabase:ServiceRoleKey"];
    var (bucket, objectKey) = ParseSupabaseStoragePath(storagePath);

    if (string.IsNullOrWhiteSpace(supabaseUrl) || string.IsNullOrWhiteSpace(serviceRoleKey))
    {
        throw new InvalidOperationException("Supabase signed URLs require Supabase:Url and Supabase:ServiceRoleKey.");
    }

    var requestUrl = $"{supabaseUrl.TrimEnd('/')}/storage/v1/object/sign/{bucket}/{Uri.EscapeDataString(objectKey).Replace("%2F", "/", StringComparison.Ordinal)}";
    var client = httpClientFactory.CreateClient();
    using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl)
    {
        Content = JsonContent.Create(new { expiresIn = 900 })
    };
    request.Headers.TryAddWithoutValidation("apikey", serviceRoleKey);
    request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {serviceRoleKey}");

    using var response = await client.SendAsync(request);
    var detail = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        throw new InvalidOperationException($"Supabase signed URL failed: {(int)response.StatusCode} {detail}");
    }

    using var payload = System.Text.Json.JsonDocument.Parse(detail);
    var signedPath = payload.RootElement.GetProperty("signedURL").GetString()
        ?? throw new InvalidOperationException("Supabase signed URL response did not include signedURL.");
    return $"{supabaseUrl.TrimEnd('/')}{signedPath}";
}

public sealed record TimelineItem(string Type, string Title, string Detail, DateTimeOffset When);
public sealed record GenerateReportRequest(string Name, string Format);
public sealed record BuildReportRequest(string Name, string Category, string Schedule, List<string> Metrics, List<string> Formats, Dictionary<string, string> Filters);
public sealed record SendNotificationRequest(string Channel, string Title, string Detail);
public sealed record InvestigateIncidentRequest(string Outcome, string ActionPlan, bool CloseIncident);
public sealed record AiSummaryRequest(Guid? ServiceUserId);
public sealed record CreateMedicationRequest(Guid ServiceUserId, string Name, string Dosage, string Route, string Schedule, bool IsPrn, string Pharmacy, string AllergyWarning);
public sealed record CreateMedicationAdministrationRecordRequest(Guid MedicationId, Guid VisitId, Guid CareWorkerId, DateTimeOffset ScheduledAt, string Notes);
public sealed record CompleteMedicationAdministrationRequest(DateTimeOffset? AdministeredAt, string Notes);
public sealed record CreateOrganizationRequest(string Name, string Plan);
public sealed record CreateBranchRequest(string Name, string Region, Guid? OrganizationId);
public sealed record FamilyPreferencesRequest(bool EmailNotifications, bool SmsNotifications, bool MonthlyDigest, bool IncidentAlerts);
public sealed record RecordPaymentRequest(decimal Amount, string Reference);
public sealed record RejectFinancialRequest(string Reason);

public partial class Program;
