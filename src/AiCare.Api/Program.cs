using System.Text;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Threading.RateLimiting;
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
        var configuredOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        var allowedOrigins = configuredOrigins
            .Where(origin => !string.IsNullOrWhiteSpace(origin))
            .Concat(["http://127.0.0.1:5173", "http://localhost:5173", "https://ai-care-frontend.vercel.app"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        policy
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

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
ValidateJwtOptions(jwtOptions, builder.Environment);
builder.Services.AddSingleton<Microsoft.Extensions.Options.IOptions<JwtOptions>>(
    Microsoft.Extensions.Options.Options.Create(jwtOptions));
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
var authPermitLimit = builder.Environment.IsEnvironment("Testing")
    ? 1000
    : builder.Configuration.GetValue<int?>("RateLimiting:AuthPermitLimit") ?? 10;
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = authPermitLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 300,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

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

        app.Logger.LogError(exception, "Unhandled API exception requestId={RequestId}", context.TraceIdentifier);
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new { message = "An unexpected error occurred.", requestId = context.TraceIdentifier });
    });
});
app.Use(async (context, next) =>
{
    var requestId = context.Request.Headers.TryGetValue("X-Request-ID", out var incoming)
        ? incoming.ToString()
        : Guid.NewGuid().ToString("N");
    context.TraceIdentifier = requestId;
    context.Response.Headers["X-Request-ID"] = requestId;
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";

    var stopwatch = Stopwatch.StartNew();
    try
    {
        await next();
    }
    finally
    {
        stopwatch.Stop();
        app.Logger.LogInformation(
            "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs}ms requestId={RequestId}",
            context.Request.Method,
            context.Request.Path,
            context.Response.StatusCode,
            stopwatch.ElapsedMilliseconds,
            requestId);
    }
});
app.UseCors("ReactClient");
app.UseAuthentication();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    var sessionClaim = context.User.FindFirst("sid")?.Value;
    if (Guid.TryParse(sessionClaim, out var authenticatedSessionId))
    {
        var scopedDb = context.RequestServices.GetRequiredService<CareDbContext>();
        var connection = scopedDb.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(context.RequestAborted);
        await using var command = connection.CreateCommand();
        command.CommandText = "select exists(select 1 from auth_sessions where id=@id and revoked_at is null and compromise_detected_at is null and expires_at>now())";
        var parameter = command.CreateParameter(); parameter.ParameterName = "id"; parameter.Value = authenticatedSessionId; command.Parameters.Add(parameter);
        if (!Convert.ToBoolean(await command.ExecuteScalarAsync(context.RequestAborted)))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { message = "Session is no longer active." });
            return;
        }
    }
    var enrollmentRequired = string.Equals(context.User.FindFirst("mfa_enrollment_required")?.Value, "true", StringComparison.OrdinalIgnoreCase);
    var enrollmentPath = context.Request.Path.StartsWithSegments("/api/security/mfa") || context.Request.Path.Equals("/api/auth/me", StringComparison.OrdinalIgnoreCase) || context.Request.Path.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase);
    if (enrollmentRequired && !enrollmentPath)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { message = "MFA enrollment is required before accessing care data.", mfaEnrollmentRequired = true });
        return;
    }
    var isFamilyMember = context.User.IsInRole(nameof(UserRole.FamilyMember));
    var isRestrictedPortalRole = isFamilyMember || context.User.IsInRole(nameof(UserRole.ServiceUser));
    var isPhaseOneApi = context.Request.Path.StartsWithSegments("/api/phase1");
    var isFamilyScopedApi = context.Request.Path.StartsWithSegments("/api/phase1/family");
    var isFamilyComplaintHistory = isFamilyMember && context.Request.Method == HttpMethods.Get && context.Request.Path.Equals("/api/phase1/complaints/mine", StringComparison.OrdinalIgnoreCase);
    var isAuthorizedFamilyCarePlanRoute = isFamilyMember && IsFamilyCarePlanRoute(context.Request);

    if (isRestrictedPortalRole && isPhaseOneApi && !isFamilyScopedApi && !isAuthorizedFamilyCarePlanRoute && !isFamilyComplaintHistory)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { message = "This account can only access its linked care portal." });
        return;
    }

    await next();
});
app.UseMiddleware<PrivilegedAccessMiddleware>();
if (!app.Environment.IsEnvironment("Testing")) app.UseMiddleware<SensitiveOperationStepUpMiddleware>();
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

app.MapGet("/status/config", (IConfiguration configuration, IWebHostEnvironment environment) => Results.Ok(new
{
    environment = environment.EnvironmentName,
    storageProvider = configuration["Storage:Provider"] ?? "Local",
    supabaseConfigured = HasConfig(configuration, "Supabase:Url") && HasConfig(configuration, "Supabase:ServiceRoleKey") && HasConfig(configuration, "Supabase:Bucket"),
    jwtConfigured = HasConfig(configuration, "JwtOptions:Issuer") && HasConfig(configuration, "JwtOptions:Audience") && HasConfig(configuration, "JwtOptions:SigningKey"),
    demoSeedEnabled = string.Equals(configuration["Demo:Enabled"], "true", StringComparison.OrdinalIgnoreCase),
    checkedAt = DateTimeOffset.UtcNow
}));

// Keep authorization behavior consistent in every environment. Local development
// must use a real seeded account instead of silently exposing all care endpoints.
var phase1 = app.MapGroup("/api/phase1").RequireAuthorization("Phase1User");

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
phase1.MapGet("/service-users/{id:guid}", async (Guid id, ICareRepository repository, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, IContextualAuthorization authorization) =>
{
    if (!await authorization.CanReadServiceUserAsync(id)) return currentUser.IsCareWorker || currentUser.IsFamilyMember ? Results.Forbid() : Results.NotFound();
    var serviceUser = repository.GetServiceUser(id);
    if (serviceUser is null) return Results.NotFound();
    AddAudit(context, tenant, currentUser, "service_user.viewed", nameof(ServiceUser), id);
    context.SaveChanges();
    return Results.Ok(serviceUser);
});
phase1.MapPost("/service-users", (CreateServiceUserRequest request, ICareRepository repository, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;
    if (Missing(request.FullName, request.PhoneNumber, request.CareNeeds, request.EmergencyContact, request.PreferredCareWorker))
    {
        return Error("Full name, phone number, care needs, emergency contact, and preferred care worker are required.");
    }

    if (request.DateOfBirth > DateOnly.FromDateTime(DateTime.UtcNow))
    {
        return Error("Date of birth cannot be in the future.");
    }

    var normalizedName = request.FullName.Trim().ToUpperInvariant();
    var duplicate = context.ServiceUsers.AsNoTracking().AsEnumerable().Any(item =>
        TenantVisible(tenant, item.OrganizationId, item.BranchId) &&
        item.DateOfBirth == request.DateOfBirth &&
        item.FullName.Trim().ToUpperInvariant() == normalizedName);
    if (duplicate) return Results.Conflict(new { message = "A person with the same name and date of birth already exists." });

    var serviceUser = repository.AddServiceUser(request);
    return Results.Created($"/api/phase1/service-users/{serviceUser.Id}", serviceUser);
});
phase1.MapPut("/service-users/{id:guid}", (Guid id, CreateServiceUserRequest request, ICareRepository repository, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;
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
phase1.MapDelete("/service-users/{id:guid}", async (Guid id, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, CancellationToken cancellationToken) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareManager);
    if (denied is not null) return denied;
    var serviceUser = await context.ServiceUsers.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
    if (serviceUser is null || !tenant.CanAccess(serviceUser.OrganizationId, serviceUser.BranchId)) return Results.NotFound();
    if (await HasActiveLegalHold(context, tenant, id, cancellationToken)) return Results.Conflict(new { message = "An active legal hold prevents deletion or disposal." });
    var now = DateTimeOffset.UtcNow;
    if ((await context.Visits.AsNoTracking().Where(item => item.OrganizationId == tenant.OrganizationId && item.ServiceUserId == id).ToListAsync(cancellationToken)).Any(item => item.StartsAt > now)) return Results.Conflict(new { message = "Future visits must be cancelled or completed before archiving." });
    if (await context.Incidents.AnyAsync(item => item.OrganizationId == tenant.OrganizationId && item.ServiceUserId == id && item.Status != "Closed", cancellationToken)) return Results.Conflict(new { message = "Open incidents must be resolved before archiving." });
    context.Entry(serviceUser).CurrentValues.SetValues(serviceUser with { Status = "Archived" });
    AddAudit(context, tenant, currentUser, "service_user.archived", nameof(ServiceUser), id);
    await context.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
});

phase1.MapGet("/service-users/{id:guid}/complete-record", async (Guid id, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, IContextualAuthorization authorization) =>
{
    if (!await authorization.CanReadServiceUserAsync(id)) return currentUser.IsCareWorker || currentUser.IsFamilyMember ? Results.Forbid() : Results.NotFound();
    var person = context.ServiceUsers.AsNoTracking().FirstOrDefault(item => item.Id == id && item.OrganizationId == tenant.OrganizationId);
    if (person is null || !tenant.CanAccess(person.OrganizationId, person.BranchId)) return Results.NotFound();

    var record = context.PersonRecords.AsNoTracking().FirstOrDefault(item => item.ServiceUserId == id && item.OrganizationId == tenant.OrganizationId);
    var assessments = context.CareAssessments.AsNoTracking().Where(item => item.ServiceUserId == id && item.OrganizationId == tenant.OrganizationId).AsEnumerable().OrderByDescending(item => item.CompletedAt).ToList();
    var plans = context.CarePlans.AsNoTracking().Where(item => item.ServiceUserId == id && item.OrganizationId == tenant.OrganizationId).OrderByDescending(item => item.Version).ToList();
    var planIds = plans.Select(item => item.Id).ToList();
    var outcomes = context.CarePlanOutcomes.AsNoTracking().Where(item => planIds.Contains(item.CarePlanId) && item.OrganizationId == tenant.OrganizationId).ToList();
    var risks = context.RiskAssessments.AsNoTracking().Where(item => item.ServiceUserId == id && item.OrganizationId == tenant.OrganizationId).ToList();
    var family = context.FamilyMembers.AsNoTracking().Where(item => item.ServiceUserId == id && item.OrganizationId == tenant.OrganizationId).ToList();
    var notes = context.CareNotes.AsNoTracking().Where(item => item.ServiceUserId == id && item.OrganizationId == tenant.OrganizationId).AsEnumerable().OrderByDescending(item => item.CreatedAt).Take(20).ToList();
    var incidents = context.Incidents.AsNoTracking().Where(item => item.ServiceUserId == id && item.OrganizationId == tenant.OrganizationId).AsEnumerable().OrderByDescending(item => item.ReportedAt).Take(20).ToList();
    AddAudit(context, tenant, currentUser, "person_record.viewed", nameof(ServiceUser), id);
    context.SaveChanges();
    return Results.Ok(new { person, record, assessments, plans, outcomes, risks, family, notes, incidents });
});

phase1.MapPut("/service-users/{id:guid}/person-record", async (Guid id, UpsertPersonRecordRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, IContextualAuthorization authorization) =>
{
    if (!await authorization.CanWriteServiceUserAsync(id)) return Results.NotFound();
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
phase1.MapDelete("/care-workers/{id:guid}", async (Guid id, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, CancellationToken cancellationToken) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareManager);
    if (denied is not null) return denied;
    var worker = await context.CareWorkers.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
    if (worker is null || !tenant.CanAccess(worker.OrganizationId, worker.BranchId)) return Results.NotFound();
    var now = DateTimeOffset.UtcNow;
    var activeVisits = (await context.Visits.AsNoTracking().Where(item => item.OrganizationId == tenant.OrganizationId && item.CareWorkerId == id && item.Status != VisitStatus.Cancelled && item.Status != VisitStatus.Completed).ToListAsync(cancellationToken)).Any(item => item.StartsAt >= now);
    if (activeVisits) return Results.Conflict(new { message = "Future active visits must be reassigned, cancelled, or completed before archiving a care worker." });
    context.Entry(worker).CurrentValues.SetValues(worker with { Availability = "Archived", AssignedServiceUsers = 0, Utilization = 0 });
    foreach (var appUser in await context.AppUsers.Where(item => item.OrganizationId == tenant.OrganizationId && item.CareWorkerId == id).ToListAsync(cancellationToken))
    {
        context.Entry(appUser).CurrentValues.SetValues(appUser with { IsActive = false });
    }
    AddAudit(context, tenant, currentUser, "care_worker.archived", nameof(CareWorker), id);
    await context.SaveChangesAsync(cancellationToken);
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
phase1.MapGet("/visits/{id:guid}", async (Guid id, CareDbContext context, ITenantContext tenant, IContextualAuthorization authorization) =>
{
    if (!await authorization.CanReadVisitAsync(id)) return Results.NotFound();
    var visit = context.Visits.AsNoTracking().FirstOrDefault(item => item.Id == id && item.OrganizationId == tenant.OrganizationId);
    if (visit is null || !tenant.CanAccess(visit.OrganizationId, visit.BranchId)) return Results.NotFound();
    var person = context.ServiceUsers.AsNoTracking().FirstOrDefault(item => item.Id == visit.ServiceUserId);
    var worker = context.CareWorkers.AsNoTracking().FirstOrDefault(item => item.Id == visit.CareWorkerId);
    var notes = context.CareNotes.AsNoTracking().Where(item => item.VisitId == id && item.OrganizationId == tenant.OrganizationId).ToList().OrderByDescending(item => item.CreatedAt).ToList();
    var observations = context.HealthObservations.AsNoTracking().Where(item => item.VisitId == id && item.OrganizationId == tenant.OrganizationId).ToList().OrderByDescending(item => item.RecordedAt).ToList();
    return Results.Ok(new { visit, person, worker, notes, observations });
});
phase1.MapGet("/rota", (DateTimeOffset? from, DateTimeOffset? to, Guid? careWorkerId, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager, UserRole.BackOffice);
    if (denied is not null) return denied;

    var start = from ?? DateTimeOffset.UtcNow.Date;
    var end = to ?? start.AddDays(7);
    if (end <= start || end.Subtract(start).TotalDays > 62)
    {
        return Error("Rota date range must be between 1 and 62 days.");
    }

    var visits = context.Visits.AsNoTracking()
        .AsEnumerable()
        .Where(visit => TenantVisible(tenant, visit.OrganizationId, visit.BranchId))
        .Where(visit => visit.StartsAt >= start && visit.StartsAt < end && (careWorkerId == null || visit.CareWorkerId == careWorkerId))
        .OrderBy(visit => visit.StartsAt)
        .ToList();
    var workerIds = visits.Select(visit => visit.CareWorkerId).Distinct().ToList();
    var serviceUserIds = visits.Select(visit => visit.ServiceUserId).Distinct().ToList();
    var workers = context.CareWorkers.AsNoTracking().Where(worker => workerIds.Contains(worker.Id)).ToDictionary(worker => worker.Id);
    var serviceUsers = context.ServiceUsers.AsNoTracking().Where(user => serviceUserIds.Contains(user.Id)).ToDictionary(user => user.Id);
    return Results.Ok(visits.Select(visit => new
    {
        visit,
        careWorkerName = workers.TryGetValue(visit.CareWorkerId, out var worker) ? worker.FullName : "",
        serviceUserName = serviceUsers.TryGetValue(visit.ServiceUserId, out var serviceUser) ? serviceUser.FullName : "",
        conflicts = FindVisitConflicts(context, tenant, visit.CareWorkerId, visit.StartsAt, visit.DurationMinutes, visit.Id).Count
    }));
});
phase1.MapPost("/visits/conflicts", (CreateVisitRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;

    if (request.CareWorkerId == Guid.Empty || request.DurationMinutes <= 0)
    {
        return Error("Care worker and a positive duration are required.");
    }

    var workerIds = VisitWorkerIds(request.CareWorkerId, request.AdditionalCareWorkerIds);
    var validation = workerIds.Select(id => ValidateCareWorkerReference(id, context, tenant)).FirstOrDefault(result => result is not null);
    if (validation is not null) return validation;

    var conflicts = workerIds.SelectMany(id => FindVisitConflicts(context, tenant, id, request.StartsAt, request.DurationMinutes)).ToList();
    var availabilityConflicts = workerIds.SelectMany(id => FindWorkerAvailabilityConflicts(context, tenant, id, request.StartsAt, request.DurationMinutes)).ToList();
    var safetyConflicts = workerIds.SelectMany(id => FindWorkerSafetyConflicts(context, tenant, id, request.StartsAt, request.RequiredSkills)).ToList();
    var operationalConflicts = workerIds.SelectMany(id => FindWorkerOperationalConflicts(context, tenant, id, request.ServiceUserId, request.StartsAt, request.DurationMinutes)).ToList();
    return Results.Ok(new { hasConflicts = conflicts.Count > 0 || availabilityConflicts.Count > 0 || safetyConflicts.Count > 0 || operationalConflicts.Count > 0, conflicts, availabilityConflicts, safetyConflicts, operationalConflicts });
});
phase1.MapPost("/visits", (CreateVisitRequest request, HttpContext httpContext, ICareRepository repository, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;

    var idempotencyKey = httpContext.Request.Headers["Idempotency-Key"].ToString();
    if (TryGetIdempotentResource(context, tenant, currentUser, "POST /api/phase1/visits", idempotencyKey, out var existingVisitId))
    {
        var existingVisit = context.Visits.AsNoTracking().FirstOrDefault(item => item.Id == existingVisitId && item.OrganizationId == tenant.OrganizationId);
        return existingVisit is null ? Results.NotFound() : Results.Ok(existingVisit);
    }

    if (request.ServiceUserId == Guid.Empty || request.CareWorkerId == Guid.Empty || string.IsNullOrWhiteSpace(request.VisitType) || request.DurationMinutes <= 0)
    {
        return Error("Service user, care worker, visit type, and a positive duration are required.");
    }

    var workerIds = VisitWorkerIds(request.CareWorkerId, request.AdditionalCareWorkerIds);
    var validation = ValidateVisitReferences(request.ServiceUserId, request.CareWorkerId, context, tenant)
        ?? workerIds.Skip(1).Select(id => ValidateCareWorkerReference(id, context, tenant)).FirstOrDefault(result => result is not null);
    if (validation is not null) return validation;

    var conflicts = FindVisitConflicts(context, tenant, request.CareWorkerId, request.StartsAt, request.DurationMinutes);
    if (conflicts.Count > 0)
    {
        return Error("Care worker already has a conflicting visit.");
    }
    var availabilityConflicts = FindWorkerAvailabilityConflicts(context, tenant, request.CareWorkerId, request.StartsAt, request.DurationMinutes);
    if (availabilityConflicts.Count > 0)
    {
        return Error("Care worker is unavailable for the requested visit time. Check structured availability rules.");
    }
    var safetyConflicts = FindWorkerSafetyConflicts(context, tenant, request.CareWorkerId, request.StartsAt, request.RequiredSkills);
    if (safetyConflicts.Count > 0)
    {
        return Error("Care worker does not meet the visit's current compliance or skill requirements.");
    }
    var additionalConflicts = workerIds.Skip(1).SelectMany(id => FindVisitConflicts(context, tenant, id, request.StartsAt, request.DurationMinutes)
        .Concat(FindWorkerAvailabilityConflicts(context, tenant, id, request.StartsAt, request.DurationMinutes))
        .Concat(FindWorkerSafetyConflicts(context, tenant, id, request.StartsAt, request.RequiredSkills))).ToList();
    var operationalConflicts = workerIds.SelectMany(id => FindWorkerOperationalConflicts(context, tenant, id, request.ServiceUserId, request.StartsAt, request.DurationMinutes)).ToList();
    if (additionalConflicts.Count > 0 || operationalConflicts.Count > 0) return Error("One or more assigned workers fail availability, compliance, working-time, absence, or travel requirements.");

    var visit = repository.AddVisit(request);
    StoreIdempotentResource(context, tenant, currentUser, "POST /api/phase1/visits", idempotencyKey, nameof(Visit), visit.Id);
    SyncVisitWorkerAssignments(context, tenant, visit.Id, request.CareWorkerId, request.AdditionalCareWorkerIds);
    RecordScheduleChange(context, tenant, currentUser, visit.Id, "Created", null, visit, request.ChangeReason);
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

    var workerIds = VisitWorkerIds(request.CareWorkerId, request.AdditionalCareWorkerIds);
    var validation = ValidateVisitReferences(request.ServiceUserId, request.CareWorkerId, context, tenant)
        ?? workerIds.Skip(1).Select(workerId => ValidateCareWorkerReference(workerId, context, tenant)).FirstOrDefault(result => result is not null);
    if (validation is not null) return validation;

    var conflicts = FindVisitConflicts(context, tenant, request.CareWorkerId, request.StartsAt, request.DurationMinutes, id);
    if (conflicts.Count > 0)
    {
        return Error("Care worker already has a conflicting visit.");
    }
    var availabilityConflicts = FindWorkerAvailabilityConflicts(context, tenant, request.CareWorkerId, request.StartsAt, request.DurationMinutes);
    if (availabilityConflicts.Count > 0)
    {
        return Error("Care worker is unavailable for the requested visit time. Check structured availability rules.");
    }
    var safetyConflicts = FindWorkerSafetyConflicts(context, tenant, request.CareWorkerId, request.StartsAt, request.RequiredSkills);
    if (safetyConflicts.Count > 0)
    {
        return Error("Care worker does not meet the visit's current compliance or skill requirements.");
    }
    var additionalConflicts = workerIds.Skip(1).SelectMany(workerId => FindVisitConflicts(context, tenant, workerId, request.StartsAt, request.DurationMinutes, id)
        .Concat(FindWorkerAvailabilityConflicts(context, tenant, workerId, request.StartsAt, request.DurationMinutes))
        .Concat(FindWorkerSafetyConflicts(context, tenant, workerId, request.StartsAt, request.RequiredSkills))).ToList();
    var operationalConflicts = workerIds.SelectMany(workerId => FindWorkerOperationalConflicts(context, tenant, workerId, request.ServiceUserId, request.StartsAt, request.DurationMinutes, id)).ToList();
    if (additionalConflicts.Count > 0 || operationalConflicts.Count > 0) return Error("One or more assigned workers fail availability, compliance, working-time, absence, or travel requirements.");

    var previousVisit = context.Visits.AsNoTracking().FirstOrDefault(item => item.Id == id);
    var visit = repository.UpdateVisit(id, request);
    if (visit is not null) SyncVisitWorkerAssignments(context, tenant, visit.Id, request.CareWorkerId, request.AdditionalCareWorkerIds);
    if (visit is not null) RecordScheduleChange(context, tenant, currentUser, visit.Id, previousVisit?.CareWorkerId != visit.CareWorkerId ? "Reassigned" : previousVisit?.StartsAt != visit.StartsAt ? "Rescheduled" : "Updated", previousVisit, visit, request.ChangeReason);
    return visit is null ? Results.NotFound() : Results.Ok(visit);
});
phase1.MapPost("/visits/recurring", (CreateRecurringVisitRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;

    if (request.ServiceUserId == Guid.Empty || request.CareWorkerId == Guid.Empty || Missing(request.VisitType, request.Frequency) || request.DurationMinutes <= 0 || request.Occurrences <= 0)
    {
        return Error("Service user, care worker, visit type, frequency, duration, and occurrences are required.");
    }

    if (request.Occurrences > 60)
    {
        return Error("Recurring visit generation is limited to 60 occurrences.");
    }

    var validation = ValidateVisitReferences(request.ServiceUserId, request.CareWorkerId, context, tenant);
    if (validation is not null) return validation;

    var starts = ExpandRecurringStarts(request.StartsAt, request.Frequency, request.Occurrences).ToList();
    var conflicts = starts.SelectMany(startsAt => FindVisitConflicts(context, tenant, request.CareWorkerId, startsAt, request.DurationMinutes)).ToList();
    if (conflicts.Count > 0)
    {
        return Error("Recurring series has conflicts. Use /api/phase1/visits/conflicts to inspect the rota.");
    }
    var availabilityConflicts = starts.SelectMany(startsAt => FindWorkerAvailabilityConflicts(context, tenant, request.CareWorkerId, startsAt, request.DurationMinutes)).ToList();
    if (availabilityConflicts.Count > 0)
    {
        return Error("Recurring series includes times when the care worker is unavailable. Check structured availability rules.");
    }
    var safetyConflicts = starts.SelectMany(startsAt => FindWorkerSafetyConflicts(context, tenant, request.CareWorkerId, startsAt, request.RequiredSkills)).ToList();
    if (safetyConflicts.Count > 0)
    {
        return Error("Recurring series includes assignments that fail current compliance or skill requirements.");
    }
    var workerIds = VisitWorkerIds(request.CareWorkerId, request.AdditionalCareWorkerIds);
    var secondaryValidation = workerIds.Skip(1).Select(id => ValidateCareWorkerReference(id, context, tenant)).FirstOrDefault(result => result is not null);
    if (secondaryValidation is not null) return secondaryValidation;
    var additionalConflicts = starts.SelectMany(startsAt => workerIds.Skip(1).SelectMany(id => FindVisitConflicts(context, tenant, id, startsAt, request.DurationMinutes)
        .Concat(FindWorkerAvailabilityConflicts(context, tenant, id, startsAt, request.DurationMinutes))
        .Concat(FindWorkerSafetyConflicts(context, tenant, id, startsAt, request.RequiredSkills)))).ToList();
    var operationalConflicts = starts.SelectMany(startsAt => workerIds.SelectMany(id => FindWorkerOperationalConflicts(context, tenant, id, request.ServiceUserId, startsAt, request.DurationMinutes))).ToList();
    if (additionalConflicts.Count > 0 || operationalConflicts.Count > 0) return Error("Recurring series fails workforce absence, working-time, travel, or double-up worker requirements.");

    var visits = starts.Select(startsAt => new Visit(
        Guid.NewGuid(),
        request.ServiceUserId,
        request.CareWorkerId,
        startsAt,
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
        tenant.OrganizationId,
        tenant.BranchId ?? TenantDefaults.BranchId)).ToList();
    context.Visits.AddRange(visits);
    AddAudit(context, tenant, currentUser, $"visit.recurring_scheduled:{visits.Count}", nameof(Visit), visits.First().Id);
    context.SaveChanges();
    foreach (var visit in visits) { SyncVisitWorkerAssignments(context, tenant, visit.Id, request.CareWorkerId, request.AdditionalCareWorkerIds); RecordScheduleChange(context, tenant, currentUser, visit.Id, "RecurringCreated", null, visit, "Recurring series created"); }
    return Results.Created("/api/phase1/visits/recurring", new { count = visits.Count, visits });
});
phase1.MapPatch("/visits/{id:guid}/status", (Guid id, UpdateVisitStatusRequest request, ICareRepository repository, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;

    var previousVisit = context.Visits.AsNoTracking().FirstOrDefault(item => item.Id == id);
    var visit = repository.UpdateVisitStatus(id, request.Status);
    if (visit is not null) RecordScheduleChange(context, tenant, currentUser, id, request.Status == VisitStatus.Cancelled ? "Cancelled" : "StatusChanged", previousVisit, visit, request.Reason);
    return visit is null ? Results.NotFound() : Results.Ok(visit);
});
phase1.MapDelete("/visits/{id:guid}", (Guid id, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;

    var visit = context.Visits.Find(id);
    if (visit is null || !tenant.CanAccess(visit.OrganizationId, visit.BranchId)) return Results.NotFound();
    if (visit.Status == VisitStatus.Completed || visit.Status == VisitStatus.InProgress) return Results.Conflict(new { message = "Started or completed visits require a correction record, not deletion." });
    context.Entry(visit).CurrentValues.SetValues(visit with { Status = VisitStatus.Cancelled });
    RecordScheduleChange(context, tenant, currentUser, id, "Cancelled", visit, visit with { Status = VisitStatus.Cancelled }, "Visit archived by delete request");
    AddAudit(context, tenant, currentUser, "visit.cancelled", nameof(Visit), id);
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
    context.Entry(family).CurrentValues.SetValues(family with { Status = "Revoked" });
    AddAudit(context, tenant, currentUser, "family_member.revoked", nameof(FamilyMember), id);
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
    if (context.MedicationAdministrationRecords.Any(item => item.MedicationId == id && item.OrganizationId == tenant.OrganizationId)) return Results.Conflict(new { message = "Medication with administration history must be discontinued, not deleted." });
    context.Entry(medication).CurrentValues.SetValues(medication with { Schedule = "Discontinued", AllergyWarning = $"[Discontinued] {medication.AllergyWarning}" });
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "medication.discontinued", currentUser.UserName, nameof(Medication), id, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
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
phase1.MapPost("/mar/{id:guid}/administer", (Guid id, CompleteMedicationAdministrationRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, IConfiguration configuration) =>
    CompleteMedicationAdministration(id, "Administered", request, context, tenant, currentUser, configuration));
phase1.MapPost("/mar/{id:guid}/skip", (Guid id, CompleteMedicationAdministrationRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, IConfiguration configuration) =>
    CompleteMedicationAdministration(id, "Missed", request, context, tenant, currentUser, configuration));
phase1.MapPost("/mar/{id:guid}/refuse", (Guid id, CompleteMedicationAdministrationRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, IConfiguration configuration) =>
    CompleteMedicationAdministration(id, "Refused", request, context, tenant, currentUser, configuration));
phase1.MapPost("/mar/{id:guid}/missed", (Guid id, CompleteMedicationAdministrationRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, IConfiguration configuration) =>
    CompleteMedicationAdministration(id, "Missed", request, context, tenant, currentUser, configuration));
phase1.MapPost("/mar/{id:guid}/held", (Guid id, CompleteMedicationAdministrationRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, IConfiguration configuration) =>
    CompleteMedicationAdministration(id, "Held", request, context, tenant, currentUser, configuration));
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

    context.Entry(note).CurrentValues.SetValues(note with { Summary = $"[Corrected/withdrawn] {note.Summary}", Concerns = string.IsNullOrWhiteSpace(note.Concerns) ? "Withdrawn by correction" : $"{note.Concerns} | Withdrawn by correction", RequiresReview = true });
    AddAudit(context, tenant, currentUser, "care_note.withdrawn", nameof(CareNote), id);
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

    context.Entry(notification).CurrentValues.SetValues(notification with { IsRead = true });
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "notification.dismissed", currentUser.UserName, nameof(NotificationItem), id, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
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

    if (request.Role == UserRole.FamilyMember)
    {
        if (request.FamilyMemberId is null)
        {
            return Error("Family member accounts must be linked to a family member profile.");
        }

        var familyValidation = ValidateFamilyMemberReference(request.FamilyMemberId.Value, context, tenant);
        if (familyValidation is not null) return familyValidation;
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

phase1.MapGet("/family/me", (CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    if (!currentUser.IsFamilyMember || currentUser.FamilyMemberId is null)
    {
        return Results.Forbid();
    }

    var familyMember = context.FamilyMembers.AsNoTracking()
        .FirstOrDefault(item => item.Id == currentUser.FamilyMemberId.Value);
    if (familyMember is null || !tenant.CanAccess(familyMember.OrganizationId, familyMember.BranchId))
    {
        return Results.Forbid();
    }

    var serviceUser = context.ServiceUsers.AsNoTracking()
        .FirstOrDefault(item => item.Id == familyMember.ServiceUserId);
    if (serviceUser is null || !tenant.CanAccess(serviceUser.OrganizationId, serviceUser.BranchId))
    {
        return Results.Forbid();
    }

    AddAudit(context, tenant, currentUser, "family.portal_accessed", nameof(ServiceUser), serviceUser.Id);
    context.SaveChanges();
    return Results.Ok(new { familyMember, serviceUser });
});

phase1.MapGet("/family/service-users/{id:guid}/timeline", (Guid id, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireFamilyServiceUserAccess(id, context, tenant, currentUser);
    if (denied is not null) return denied;

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

phase1.MapGet("/family/service-users/{id:guid}/dashboard", (Guid id, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireFamilyServiceUserAccess(id, context, tenant, currentUser);
    if (denied is not null) return denied;

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
    var denied = RequireFamilyServiceUserAccess(id, context, tenant, currentUser);
    if (denied is not null) return denied;

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

phase1.MapGet("/family/service-users/{id:guid}/monthly-report", (Guid id, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireFamilyServiceUserAccess(id, context, tenant, currentUser);
    if (denied is not null) return denied;

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
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareManager, UserRole.CareCoordinator);
    if (denied is not null) return denied;
    if (request.CloseIncident)
    {
        return Error("Direct incident closure is disabled. Complete the governed investigation and CAPA workflow before closure.", StatusCodes.Status409Conflict);
    }
    if (Missing(request.Outcome, request.ActionPlan))
    {
        return Error("Outcome and action plan are required.");
    }

    var incident = context.Incidents.Find(id);
    if (incident is null || !tenant.CanAccess(incident.OrganizationId, incident.BranchId))
    {
        return Results.NotFound();
    }

    var updated = incident with { Status = "Under investigation", Description = $"{incident.Description}\nLegacy triage note: {request.Outcome}\nProposed action: {request.ActionPlan}" };
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

phase1.MapGet("/tenant/onboarding", (CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var organization = context.Organizations.AsNoTracking().FirstOrDefault(item => item.Id == tenant.OrganizationId);
    if (organization is null) return Results.NotFound();
    var branches = context.Branches.AsNoTracking().Where(item => item.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || tenant.BranchId == null || item.Id == tenant.BranchId)).ToList();
    var branchIds = branches.Select(item => item.Id).ToHashSet();
    var users = context.AppUsers.AsNoTracking().Count(item => item.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || tenant.BranchId == null || item.BranchId == null || branchIds.Contains(item.BranchId.Value)));
    var staff = context.AppUsers.AsNoTracking().Count(item => item.OrganizationId == tenant.OrganizationId && item.Role != UserRole.Administrator && item.Role != UserRole.FamilyMember && item.Role != UserRole.ServiceUser && (tenant.IsOrganizationWide || tenant.BranchId == null || item.BranchId == null || branchIds.Contains(item.BranchId.Value)));
    var serviceUsers = context.ServiceUsers.AsNoTracking().Count(item => item.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || tenant.BranchId == null || item.BranchId == tenant.BranchId));
    var careWorkers = context.CareWorkers.AsNoTracking().Count(item => item.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || tenant.BranchId == null || item.BranchId == tenant.BranchId));
    var carePlans = context.CarePlans.AsNoTracking().Count(item => item.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || tenant.BranchId == null || item.BranchId == tenant.BranchId));
    var familyMembers = context.FamilyMembers.AsNoTracking().Count(item => item.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || tenant.BranchId == null || item.BranchId == tenant.BranchId));
    var steps = new[]
    {
        new { key = "organization", title = "Confirm organization", complete = organization.Status == "Active" || !string.IsNullOrWhiteSpace(organization.Name), actionPath = "/admin" },
        new { key = "branches", title = "Create branch/team", complete = branches.Count > 0, actionPath = "/admin" },
        new { key = "staff", title = "Invite staff", complete = staff > 0 || careWorkers > 0, actionPath = "/admin" },
        new { key = "people", title = "Add service users", complete = serviceUsers > 0, actionPath = "/people" },
        new { key = "care", title = "Create care records", complete = carePlans > 0, actionPath = "/operations/care" },
        new { key = "family", title = "Configure family access", complete = familyMembers > 0, actionPath = "/family" }
    };
    AddAudit(context, tenant, currentUser, "tenant.onboarding_viewed", nameof(Organization), tenant.OrganizationId, tenant.OrganizationId, tenant.BranchId ?? branches.FirstOrDefault()?.Id);
    context.SaveChanges();
    return Results.Ok(new { organization, branches, counts = new { users, staff, serviceUsers, careWorkers, carePlans, familyMembers }, steps, complete = steps.All(step => step.complete), canActivate = serviceUsers > 0 && (staff > 0 || careWorkers > 0) && carePlans > 0 });
});
phase1.MapPost("/tenant/activate", (CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareManager);
    if (denied is not null) return denied;
    var organization = context.Organizations.FirstOrDefault(item => item.Id == tenant.OrganizationId);
    if (organization is null) return Results.NotFound();
    if (string.Equals(organization.Status, "Active", StringComparison.OrdinalIgnoreCase)) return Results.Ok(new { organization.Id, organization.Name, organization.Plan, organization.Status });
    var branches = context.Branches.Where(item => item.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || tenant.BranchId == null || item.Id == tenant.BranchId)).ToList();
    var branchIds = branches.Select(item => item.Id).ToHashSet();
    var staff = context.AppUsers.AsNoTracking().Count(item => item.OrganizationId == tenant.OrganizationId && item.Role != UserRole.Administrator && item.Role != UserRole.FamilyMember && item.Role != UserRole.ServiceUser && (tenant.IsOrganizationWide || tenant.BranchId == null || item.BranchId == null || branchIds.Contains(item.BranchId.Value)));
    var careWorkers = context.CareWorkers.AsNoTracking().Count(item => item.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || tenant.BranchId == null || item.BranchId == tenant.BranchId));
    var serviceUsers = context.ServiceUsers.AsNoTracking().Count(item => item.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || tenant.BranchId == null || item.BranchId == tenant.BranchId));
    var carePlans = context.CarePlans.AsNoTracking().Count(item => item.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || tenant.BranchId == null || item.BranchId == tenant.BranchId));
    var missing = new List<string>();
    if (branches.Count == 0) missing.Add("branch");
    if (staff == 0 && careWorkers == 0) missing.Add("staff");
    if (serviceUsers == 0) missing.Add("service user");
    if (carePlans == 0) missing.Add("care plan");
    if (missing.Count > 0) return Results.Conflict(new { message = "Tenant setup is incomplete.", missing });
    var updated = organization with { Status = "Active" };
    context.Entry(organization).CurrentValues.SetValues(updated);
    foreach (var branch in branches.Where(item => !string.Equals(item.Status, "Active", StringComparison.OrdinalIgnoreCase)))
    {
        context.Entry(branch).CurrentValues.SetValues(branch with { Status = "Active" });
    }
    AddAudit(context, tenant, currentUser, "tenant.activated", nameof(Organization), organization.Id, organization.Id, tenant.BranchId ?? branches.First().Id);
    context.SaveChanges();
    return Results.Ok(new { updated.Id, updated.Name, updated.Plan, updated.Status });
});
phase1.MapGet("/storage/status", (IConfiguration configuration) => Results.Ok(new
{
    provider = configuration["Storage:Provider"] ?? "Local",
    root = configuration["Storage:RootPath"] ?? "App_Data/uploads",
    cloudReady = !string.Equals(configuration["Storage:Provider"], "Local", StringComparison.OrdinalIgnoreCase)
}));

var demo = app.MapGroup("/api/demo").RequireAuthorization("Phase1User");
demo.MapPost("/seed", (HttpContext httpContext, IConfiguration configuration, IWebHostEnvironment environment, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    if (!DemoAccessAllowed(httpContext, configuration, environment))
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

demo.MapDelete("/reset", (HttpContext httpContext, IConfiguration configuration, IWebHostEnvironment environment, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    if (!DemoAccessAllowed(httpContext, configuration, environment))
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

var pilot = app.MapGroup("/api/pilot");
pilot.MapPost("/seed", async (HttpContext httpContext, IConfiguration configuration, IWebHostEnvironment environment, CareDbContext context, CancellationToken cancellationToken) =>
{
    if (!PilotSeedAccessAllowed(httpContext, configuration))
    {
        return Results.NotFound();
    }

    var providerName = configuration["PilotSeed:ProviderName"] ?? "AiCare Controlled Pilot Provider";
    var adminEmail = (configuration["PilotSeed:AdminEmail"] ?? "pilot.admin@aicare.local").Trim().ToLowerInvariant();
    var adminPassword = configuration["PilotSeed:AdminPassword"] ?? "PilotAdmin123!";
    var now = DateTimeOffset.UtcNow;

    if (adminPassword.Length < 12 || !adminPassword.Any(char.IsUpper) || !adminPassword.Any(char.IsLower) || !adminPassword.Any(char.IsDigit) || !adminPassword.Any(ch => !char.IsLetterOrDigit(ch)))
    {
        return Error("PilotSeed:AdminPassword must be at least 12 characters and include upper, lower, number, and symbol characters.");
    }

    var organization = await context.Organizations.FirstOrDefaultAsync(item => item.Name == providerName, cancellationToken);
    if (organization is null)
    {
        organization = new Organization(Guid.NewGuid(), providerName, "Trial", "Setup");
        context.Organizations.Add(organization);
    }

    var branchNames = new[] { "North Branch", "South Branch" };
    var branches = await context.Branches.Where(item => item.OrganizationId == organization.Id).ToListAsync(cancellationToken);
    foreach (var branchName in branchNames)
    {
        if (branches.All(item => item.Name != branchName))
        {
            var branch = new Branch(Guid.NewGuid(), organization.Id, branchName, branchName.StartsWith("North", StringComparison.OrdinalIgnoreCase) ? "North" : "South", "Setup");
            context.Branches.Add(branch);
            branches.Add(branch);
        }
    }
    var primaryBranch = branches.First(item => item.Name == "North Branch");

    if (!await context.AppUsers.AnyAsync(item => item.OrganizationId == organization.Id && item.Email == adminEmail, cancellationToken))
    {
        context.AppUsers.Add(new AppUser(Guid.NewGuid(), adminEmail, adminEmail, PasswordHasher.HashPassword(adminPassword), UserRole.Administrator, true, organization.Id, primaryBranch.Id));
    }

    var existingPilotPeople = await context.ServiceUsers.CountAsync(item => item.OrganizationId == organization.Id && item.FullName.StartsWith("Pilot Service User "), cancellationToken);

    var workers = await context.CareWorkers.Where(item => item.OrganizationId == organization.Id && item.FullName.StartsWith("Pilot Care Worker ")).ToListAsync(cancellationToken);
    var specializations = new[] { "Personal care", "Medication support", "Dementia care", "Mobility support", "Nutrition support", "Reablement", "End of life care", "Learning disability support" };
    for (var i = workers.Count + 1; i <= 8; i++)
    {
        var branch = branches[(i - 1) % branches.Count];
        var worker = new CareWorker(Guid.NewGuid(), $"Pilot Care Worker {i:00}", specializations[(i - 1) % specializations.Length], "Weekdays 07:00-15:00; alternate weekends", 0, 0, "Clear", "Compliant", "10 miles", organization.Id, branch.Id);
        context.CareWorkers.Add(worker);
        context.AppUsers.Add(new AppUser(Guid.NewGuid(), $"pilot.worker.{i:00}@aicare.local", $"pilot.worker.{i:00}@aicare.local", PasswordHasher.HashPassword("PilotWorker123!"), UserRole.CareWorker, true, organization.Id, branch.Id, worker.Id));
        workers.Add(worker);
    }

    var firstNames = new[] { "Aisha", "Bilal", "Carol", "David", "Elaine", "Farah", "George", "Hannah", "Imran", "Julia", "Khalid", "Linda", "Martin", "Nadia", "Owen", "Priya", "Qasim", "Ruth", "Samina", "Thomas" };
    var lastNames = new[] { "Ahmed", "Brown", "Clark", "Davies", "Evans", "Farooq", "Green", "Hussain", "Iqbal", "Jones", "Khan", "Lewis", "Morgan", "Nadeem", "ONeill", "Patel", "Qureshi", "Roberts", "Shah", "Taylor" };
    var needs = new[] { "Morning personal care and breakfast support", "Medication prompts and welfare checks", "Mobility support and meal preparation", "Dementia reassurance visits", "Nutrition monitoring and hydration prompts" };
    var people = new List<ServiceUser>();

    for (var i = existingPilotPeople + 1; i <= 20; i++)
    {
        var worker = workers[(i - 1) % workers.Count];
        var branch = branches[(i - 1) % branches.Count];
        var person = new ServiceUser(Guid.NewGuid(), $"Pilot Service User {i:00} - {firstNames[i - 1]} {lastNames[i - 1]}", new DateOnly(1938 + i % 45, 1 + i % 12, 1 + i % 24), $"+44770090{i:000}", needs[(i - 1) % needs.Length], $"Emergency Contact {i:00} +44771100{i:000}", worker.FullName, i % 5 == 0 ? RiskLevel.High : i % 3 == 0 ? RiskLevel.Medium : RiskLevel.Low, "Onboarded", $"{10 + i} Pilot Street, Caretown", i % 5 == 0 ? "Penicillin" : "None known", i % 4 == 0 ? "Diabetes; reduced mobility" : "Long-term care support needs", i % 3 == 0 ? "Local authority" : i % 3 == 1 ? "Private" : "NHS continuing healthcare", i % 2 == 0 ? "Male" : "Female", "", "Independent with support", i % 4 == 0 ? "Mild cognitive impairment" : "No known impairment", "Plain English communication", "Respect daily routine and personal preferences", "Encourage fluids and balanced meals", organization.Id, branch.Id);
        context.ServiceUsers.Add(person);
        people.Add(person);

        var plan = new CarePlan(Guid.NewGuid(), person.Id, "v1", "Draft", "Support with washing, dressing, grooming, and daily comfort checks.", "Prompt prescribed medication and record exceptions through eMAR.", "Use agreed moving and handling plan; encourage safe independence.", "Prepare light meals, encourage fluids, and record appetite concerns.", now.AddDays(60 + i), organization.Id, branch.Id);
        context.CarePlans.Add(plan);
        context.RiskAssessments.Add(new RiskAssessment(Guid.NewGuid(), person.Id, i % 5 == 0 ? "Falls" : "General wellbeing", person.Risk, "Follow care plan, escalate changes, and review after incidents.", now.AddDays(30 + i), organization.Id, branch.Id));

        if (i <= 10)
        {
            var family = new FamilyMember(Guid.NewGuid(), person.Id, $"Pilot Family Contact {i:00}", $"pilot.family.{i:00}@aicare.local", i % 2 == 0 ? "Son" : "Daughter", "Portal updates", "Invited", organization.Id, branch.Id);
            context.FamilyMembers.Add(family);
            context.AppUsers.Add(new AppUser(Guid.NewGuid(), family.Email, family.Email, PasswordHasher.HashPassword("PilotFamily123!"), UserRole.FamilyMember, true, organization.Id, branch.Id, null, family.Id));
        }

        if (i <= 14)
        {
            context.Medications.Add(new Medication(Guid.NewGuid(), person.Id, i % 2 == 0 ? "Paracetamol" : "Ramipril", i % 2 == 0 ? "500mg" : "2.5mg", "Oral", i % 2 == 0 ? "08:00, 20:00" : "08:00", false, "Pilot Community Pharmacy", "Check MAR and allergy record before administration", organization.Id, branch.Id));
        }
    }

    await context.SaveChangesAsync(cancellationToken);

    people = await context.ServiceUsers.Where(item => item.OrganizationId == organization.Id && item.FullName.StartsWith("Pilot Service User ")).OrderBy(item => item.FullName).ToListAsync(cancellationToken);
    var medications = await context.Medications.Where(item => item.OrganizationId == organization.Id && people.Select(person => person.Id).Contains(item.ServiceUserId)).ToListAsync(cancellationToken);
    var start = new DateTimeOffset(now.UtcDateTime.Date.AddDays(1).AddHours(7), TimeSpan.Zero);
    var visitCount = 0;
    var marCount = 0;
    for (var day = 0; day < 7; day++)
    {
        for (var i = 0; i < people.Count; i++)
        {
            var person = people[i];
            var worker = workers[i % workers.Count];
            var visitExists = await context.Visits.AnyAsync(item => item.OrganizationId == organization.Id && item.ServiceUserId == person.Id && item.StartsAt.Date == start.AddDays(day).Date, cancellationToken);
            if (visitExists) continue;
            var branch = person.BranchId ?? primaryBranch.Id;
            var startsAt = start.AddDays(day).AddMinutes(i * 35);
            var visit = new Visit(Guid.NewGuid(), person.Id, worker.Id, startsAt, startsAt.Hour < 12 ? "Morning care" : startsAt.Hour < 16 ? "Lunchtime support" : "Tea visit", 30, worker.Specialization, VisitStatus.Scheduled, null, null, null, null, null, null, organization.Id, branch);
            context.Visits.Add(visit);
            visitCount++;
            var medication = medications.FirstOrDefault(item => item.ServiceUserId == person.Id);
            if (medication is not null && day < 5)
            {
                context.MedicationAdministrationRecords.Add(new MedicationAdministrationRecord(Guid.NewGuid(), medication.Id, visit.Id, worker.Id, startsAt.AddMinutes(10), null, "Scheduled", "Pilot scheduled medication prompt", organization.Id, branch));
                marCount++;
            }
        }
    }

    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "pilot.seeded", "pilot-seed", nameof(Organization), organization.Id, now, organization.Id, primaryBranch.Id));
    await context.SaveChangesAsync(cancellationToken);

    return Results.Created("/api/pilot/seed", new
    {
        organizationId = organization.Id,
        provider = organization.Name,
        adminEmail,
        branches = await context.Branches.CountAsync(item => item.OrganizationId == organization.Id, cancellationToken),
        staff = await context.CareWorkers.CountAsync(item => item.OrganizationId == organization.Id && item.FullName.StartsWith("Pilot Care Worker "), cancellationToken),
        serviceUsers = await context.ServiceUsers.CountAsync(item => item.OrganizationId == organization.Id && item.FullName.StartsWith("Pilot Service User "), cancellationToken),
        carePlans = await context.CarePlans.CountAsync(item => item.OrganizationId == organization.Id, cancellationToken),
        visitsCreated = visitCount,
        medications = await context.Medications.CountAsync(item => item.OrganizationId == organization.Id, cancellationToken),
        marRecordsCreated = marCount,
        familyMembers = await context.FamilyMembers.CountAsync(item => item.OrganizationId == organization.Id, cancellationToken),
        status = organization.Status
    });
});
app.Run();

static bool Missing(params string[] values) => values.Any(string.IsNullOrWhiteSpace);

static IResult Error(string message, int statusCode = StatusCodes.Status400BadRequest) =>
    Results.Json(new { message }, statusCode: statusCode);

static bool HasConfig(IConfiguration configuration, string key) => !string.IsNullOrWhiteSpace(configuration[key]);

static void ValidateJwtOptions(JwtOptions jwtOptions, IWebHostEnvironment environment)
{
    if (string.IsNullOrWhiteSpace(jwtOptions.Issuer) ||
        string.IsNullOrWhiteSpace(jwtOptions.Audience) ||
        string.IsNullOrWhiteSpace(jwtOptions.SigningKey))
    {
        throw new InvalidOperationException("JwtOptions issuer, audience, and signing key must be configured.");
    }

    if (!environment.IsEnvironment("Testing") && jwtOptions.SigningKey.Length < 32)
    {
        throw new InvalidOperationException("JwtOptions signing key must be at least 32 characters.");
    }

    if (jwtOptions.TokenLifetimeMinutes <= 0 || jwtOptions.TokenLifetimeMinutes > 1440)
    {
        throw new InvalidOperationException("JwtOptions token lifetime must be between 1 and 1440 minutes.");
    }
}

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

static async Task<bool> HasActiveLegalHold(CareDbContext context, ITenantContext tenant, Guid serviceUserId, CancellationToken cancellationToken)
{
    if (context.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
    {
        return false;
    }

    var connection = context.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open)
    {
        await connection.OpenAsync(cancellationToken);
    }

    await using var command = connection.CreateCommand();
    command.CommandText = "select exists(select 1 from legal_holds where organization_id=@organization and status in ('Active','ReleaseRequested') and (service_user_id is null or service_user_id=@person))";
    AddParameter(command, "organization", tenant.OrganizationId);
    AddParameter(command, "person", serviceUserId);
    return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
}

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

static IResult? RequireFamilyServiceUserAccess(Guid serviceUserId, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser)
{
    if (!currentUser.IsFamilyMember)
    {
        return null;
    }

    if (currentUser.FamilyMemberId is null)
    {
        return Error("Family member account is not linked to a family profile.", StatusCodes.Status403Forbidden);
    }

    var familyMember = context.FamilyMembers.AsNoTracking().FirstOrDefault(item => item.Id == currentUser.FamilyMemberId);
    if (familyMember is null || !tenant.CanAccess(familyMember.OrganizationId, familyMember.BranchId))
    {
        return Error("Family member profile is not accessible.", StatusCodes.Status403Forbidden);
    }

    return familyMember.ServiceUserId == serviceUserId
        ? null
        : Error("Family members can only access their linked service user.", StatusCodes.Status403Forbidden);
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

static IResult? ValidateFamilyMemberReference(Guid familyMemberId, CareDbContext context, ITenantContext tenant)
{
    var familyMember = context.FamilyMembers.AsNoTracking().FirstOrDefault(item => item.Id == familyMemberId);
    if (familyMember is null || !tenant.CanAccess(familyMember.OrganizationId, familyMember.BranchId))
    {
        return Error("Family member does not exist or is not accessible.");
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

static List<object> FindVisitConflicts(CareDbContext context, ITenantContext tenant, Guid careWorkerId, DateTimeOffset startsAt, int durationMinutes, Guid? excludeVisitId = null)
{
    var endsAt = startsAt.AddMinutes(durationMinutes);
    var additionalVisitIds = ReadAdditionalVisitIds(context, tenant, careWorkerId);
    return context.Visits.AsNoTracking()
        .Where(visit => (visit.CareWorkerId == careWorkerId || additionalVisitIds.Contains(visit.Id)) && (excludeVisitId == null || visit.Id != excludeVisitId))
        .AsEnumerable()
        .Where(visit => TenantVisible(tenant, visit.OrganizationId, visit.BranchId))
        .Where(visit =>
        {
            var visitEndsAt = visit.StartsAt.AddMinutes(visit.DurationMinutes);
            return startsAt < visitEndsAt && endsAt > visit.StartsAt;
        })
        .OrderBy(visit => visit.StartsAt)
        .Select(visit => new
        {
            visit.Id,
            visit.ServiceUserId,
            visit.CareWorkerId,
            visit.StartsAt,
            visit.DurationMinutes,
            visit.VisitType,
            visit.Status
        })
        .Cast<object>()
        .ToList();
}

static Guid[] VisitWorkerIds(Guid primaryWorkerId, IReadOnlyCollection<Guid>? additionalWorkerIds) =>
    new[] { primaryWorkerId }.Concat(additionalWorkerIds ?? []).Where(id => id != Guid.Empty).Distinct().ToArray();

static HashSet<Guid> ReadAdditionalVisitIds(CareDbContext context, ITenantContext tenant, Guid careWorkerId)
{
    var ids = new HashSet<Guid>(); var connection = context.Database.GetDbConnection(); var opened = connection.State != System.Data.ConnectionState.Open;
    try { if (opened) connection.Open(); using var command=connection.CreateCommand(); command.CommandText="select visit_id from visit_care_worker_assignments where care_worker_id=@worker and organization_id=@organization"; AddParameter(command,"worker",careWorkerId); AddParameter(command,"organization",tenant.OrganizationId); using var reader=command.ExecuteReader(); while(reader.Read())ids.Add(reader.GetGuid(0)); }
    catch(System.Data.Common.DbException){ }
    finally { if(opened&&connection.State==System.Data.ConnectionState.Open)connection.Close(); }
    return ids;
}

static List<object> FindWorkerOperationalConflicts(CareDbContext context, ITenantContext tenant, Guid workerId, Guid serviceUserId, DateTimeOffset startsAt, int durationMinutes, Guid? excludeVisitId = null)
{
    var result=new List<object>(); var end=startsAt.AddMinutes(durationMinutes); var policy=(MinimumRest:660,Daily:720,Weekly:2880,Travel:15,MaximumContinuous:360,RequiredBreak:20); var absence=false;
    var connection=context.Database.GetDbConnection();var opened=connection.State!=System.Data.ConnectionState.Open;
    try { if(opened)connection.Open(); using(var command=connection.CreateCommand()){command.CommandText="select exists(select 1 from worker_absences where care_worker_id=@worker and organization_id=@organization and status in ('Approved','Confirmed') and starts_at < @end and ends_at > @start)";AddParameter(command,"worker",workerId);AddParameter(command,"organization",tenant.OrganizationId);AddParameter(command,"start",startsAt);AddParameter(command,"end",end);absence=(bool)(command.ExecuteScalar()??false);} using(var command=connection.CreateCommand()){command.CommandText="select minimum_rest_minutes,maximum_daily_minutes,maximum_weekly_minutes,travel_buffer_minutes,maximum_continuous_minutes,required_break_minutes from scheduling_policies where organization_id=@organization and branch_id=@branch";AddParameter(command,"organization",tenant.OrganizationId);AddParameter(command,"branch",tenant.BranchId??TenantDefaults.BranchId);using var reader=command.ExecuteReader();if(reader.Read())policy=(reader.GetInt32(0),reader.GetInt32(1),reader.GetInt32(2),reader.GetInt32(3),reader.GetInt32(4),reader.GetInt32(5));}}
    catch(System.Data.Common.DbException){return result;} finally{if(opened&&connection.State==System.Data.ConnectionState.Open)connection.Close();}
    if(absence)result.Add(new{careWorkerId=workerId,category="Absence",code="worker-absent",reason="Worker has approved leave or sickness overlapping this visit."});
    var additional=ReadAdditionalVisitIds(context,tenant,workerId);
    var visits=context.Visits.AsNoTracking().AsEnumerable().Where(v=>TenantVisible(tenant,v.OrganizationId,v.BranchId)&&(v.CareWorkerId==workerId||additional.Contains(v.Id))&&(excludeVisitId is null||v.Id!=excludeVisitId)).ToList();
    var dayMinutes=visits.Where(v=>v.StartsAt.Date==startsAt.Date).Sum(v=>v.DurationMinutes)+durationMinutes;
    if(dayMinutes>policy.Daily)result.Add(new{careWorkerId=workerId,category="WorkingTime",code="daily-hours",reason=$"Assignment would exceed the {policy.Daily}-minute daily limit."});
    var weekStart=startsAt.Date.AddDays(-(((int)startsAt.DayOfWeek+6)%7));var weekEnd=weekStart.AddDays(7);
    var weekMinutes=visits.Where(v=>v.StartsAt>=weekStart&&v.StartsAt<weekEnd).Sum(v=>v.DurationMinutes)+durationMinutes;
    if(weekMinutes>policy.Weekly)result.Add(new{careWorkerId=workerId,category="WorkingTime",code="weekly-hours",reason=$"Assignment would exceed the {policy.Weekly}-minute weekly limit."});
    var duties=visits.Where(v=>v.StartsAt.Date==startsAt.Date).Select(v=>(Start:v.StartsAt,End:v.StartsAt.AddMinutes(v.DurationMinutes),Minutes:v.DurationMinutes,Proposed:false)).ToList();
    duties.Add((Start:startsAt,End:end,Minutes:durationMinutes,Proposed:true));duties.Sort((left,right)=>left.Start.CompareTo(right.Start));
    var clusterMinutes=0;var clusterContainsProposed=false;DateTimeOffset? clusterEnd=null;
    foreach(var duty in duties){if(clusterEnd is not null&&(duty.Start-clusterEnd.Value).TotalMinutes>=policy.RequiredBreak){if(clusterContainsProposed&&clusterMinutes>policy.MaximumContinuous)break;clusterMinutes=0;clusterContainsProposed=false;}clusterMinutes+=duty.Minutes;clusterContainsProposed|=duty.Proposed;clusterEnd=clusterEnd is null||duty.End>clusterEnd?duty.End:clusterEnd;}
    if(clusterContainsProposed&&clusterMinutes>policy.MaximumContinuous)result.Add(new{careWorkerId=workerId,category="WorkingTime",code="insufficient-break",reason=$"Assignment creates {clusterMinutes} continuous working minutes; a {policy.RequiredBreak}-minute break is required before exceeding {policy.MaximumContinuous} minutes."});
    foreach(var visit in visits){var visitEnd=visit.StartsAt.AddMinutes(visit.DurationMinutes);if(visitEnd<=startsAt){var gap=(startsAt-visitEnd).TotalMinutes;if(visit.StartsAt.Date!=startsAt.Date&&gap<policy.MinimumRest)result.Add(new{careWorkerId=workerId,category="WorkingTime",code="minimum-rest",reason=$"Only {Math.Floor(gap)} minutes rest precede this visit; {policy.MinimumRest} are required."});else if(visit.ServiceUserId!=serviceUserId&&gap<policy.Travel)result.Add(new{careWorkerId=workerId,category="Travel",code="travel-time",reason=$"Only {Math.Floor(gap)} travel minutes are available; {policy.Travel} are required."});}else if(end<=visit.StartsAt){var gap=(visit.StartsAt-end).TotalMinutes;if(visit.StartsAt.Date!=startsAt.Date&&gap<policy.MinimumRest)result.Add(new{careWorkerId=workerId,category="WorkingTime",code="minimum-rest",reason=$"Only {Math.Floor(gap)} minutes rest follow this visit; {policy.MinimumRest} are required."});else if(visit.ServiceUserId!=serviceUserId&&gap<policy.Travel)result.Add(new{careWorkerId=workerId,category="Travel",code="travel-time",reason=$"Only {Math.Floor(gap)} travel minutes are available; {policy.Travel} are required."});}}
    return result.GroupBy(x=>System.Text.Json.JsonSerializer.Serialize(x)).Select(g=>g.First()).ToList();
}

static void SyncVisitWorkerAssignments(CareDbContext context, ITenantContext tenant, Guid visitId, Guid primaryWorkerId, IReadOnlyCollection<Guid>? additionalWorkerIds)
{
    var connection=context.Database.GetDbConnection();var opened=connection.State!=System.Data.ConnectionState.Open;
    try{if(opened)connection.Open();using(var delete=connection.CreateCommand()){delete.CommandText="delete from visit_care_worker_assignments where visit_id=@visit and organization_id=@organization";AddParameter(delete,"visit",visitId);AddParameter(delete,"organization",tenant.OrganizationId);delete.ExecuteNonQuery();}foreach(var workerId in VisitWorkerIds(primaryWorkerId,additionalWorkerIds).Where(id=>id!=primaryWorkerId)){using var insert=connection.CreateCommand();insert.CommandText="insert into visit_care_worker_assignments(visit_id,care_worker_id,organization_id,branch_id,assignment_role) values(@visit,@worker,@organization,@branch,'Additional')";AddParameter(insert,"visit",visitId);AddParameter(insert,"worker",workerId);AddParameter(insert,"organization",tenant.OrganizationId);AddParameter(insert,"branch",tenant.BranchId??TenantDefaults.BranchId);insert.ExecuteNonQuery();}}
    catch(System.Data.Common.DbException){if((additionalWorkerIds?.Count??0)>0)throw;}
    finally{if(opened&&connection.State==System.Data.ConnectionState.Open)connection.Close();}
}

static void RecordScheduleChange(CareDbContext context, ITenantContext tenant, ICurrentUserContext user, Guid visitId, string changeType, object? oldValue, object? newValue, string? reason)
{
    var connection=context.Database.GetDbConnection();var opened=connection.State!=System.Data.ConnectionState.Open;
    try{if(opened)connection.Open();using var command=connection.CreateCommand();command.CommandText="insert into schedule_change_history(id,visit_id,organization_id,branch_id,change_type,old_values_json,new_values_json,reason,changed_by) values(@id,@visit,@organization,@branch,@type,@old,@new,@reason,@actor)";AddParameter(command,"id",Guid.NewGuid());AddParameter(command,"visit",visitId);AddParameter(command,"organization",tenant.OrganizationId);AddParameter(command,"branch",tenant.BranchId??TenantDefaults.BranchId);AddParameter(command,"type",changeType);AddParameter(command,"old",System.Text.Json.JsonSerializer.Serialize(oldValue));AddParameter(command,"new",System.Text.Json.JsonSerializer.Serialize(newValue));AddParameter(command,"reason",string.IsNullOrWhiteSpace(reason)?changeType:reason.Trim());AddParameter(command,"actor",user.UserName);command.ExecuteNonQuery();}
    catch(System.Data.Common.DbException){ }
    finally{if(opened&&connection.State==System.Data.ConnectionState.Open)connection.Close();}
}

static List<object> FindWorkerAvailabilityConflicts(CareDbContext context, ITenantContext tenant, Guid careWorkerId, DateTimeOffset startsAt, int durationMinutes)
{
    var result = new List<object>();
    var rules = new List<(int DayOfWeek, TimeOnly StartTime, TimeOnly EndTime, bool IsAvailable, DateOnly EffectiveFrom, DateOnly? EffectiveTo)>();
    var connection = context.Database.GetDbConnection();
    var opened = connection.State != System.Data.ConnectionState.Open;
    try
    {
        if (opened) connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "select day_of_week,start_time,end_time,is_available,effective_from,effective_to from worker_availability_rules where care_worker_id=@worker and organization_id=@organization";
        AddParameter(command, "worker", careWorkerId);
        AddParameter(command, "organization", tenant.OrganizationId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rules.Add((reader.GetInt32(0), reader.GetFieldValue<TimeOnly>(1), reader.GetFieldValue<TimeOnly>(2), reader.GetBoolean(3), reader.GetFieldValue<DateOnly>(4), reader.IsDBNull(5) ? null : reader.GetFieldValue<DateOnly>(5)));
        }
    }
    catch (System.Data.Common.DbException)
    {
        // Compatibility fallback while older/test databases are awaiting the workforce migration.
        return result;
    }
    finally
    {
        if (opened && connection.State == System.Data.ConnectionState.Open) connection.Close();
    }

    if (rules.Count == 0) return result;
    var visitDate = DateOnly.FromDateTime(startsAt.DateTime);
    var visitStart = TimeOnly.FromDateTime(startsAt.DateTime);
    var visitEndAt = startsAt.AddMinutes(durationMinutes);
    var visitEnd = TimeOnly.FromDateTime(visitEndAt.DateTime);
    var applicable = rules.Where(rule => rule.EffectiveFrom <= visitDate && (rule.EffectiveTo is null || rule.EffectiveTo >= visitDate)).ToList();
    var dayRules = applicable.Where(rule => rule.DayOfWeek == (int)startsAt.DayOfWeek).ToList();
    var crossesDay = DateOnly.FromDateTime(visitEndAt.DateTime) != visitDate;
    var insideAvailableWindow = !crossesDay && dayRules.Any(rule => rule.IsAvailable && visitStart >= rule.StartTime && visitEnd <= rule.EndTime);
    var overlapsUnavailableWindow = dayRules.Any(rule => !rule.IsAvailable && visitStart < rule.EndTime && visitEnd > rule.StartTime);
    if (!insideAvailableWindow || overlapsUnavailableWindow)
    {
        result.Add(new
        {
            careWorkerId,
            startsAt,
            endsAt = visitEndAt,
            reason = overlapsUnavailableWindow ? "The visit overlaps an explicit unavailable period." : "The visit is outside the worker's available hours."
        });
    }
    return result;
}

static List<object> FindWorkerSafetyConflicts(CareDbContext context, ITenantContext tenant, Guid careWorkerId, DateTimeOffset startsAt, string? requiredSkills)
{
    var result = new List<object>();
    var compliance = new List<(string Type, string Status, DateTimeOffset? ExpiresAt)>();
    var training = new List<(string Name, string Category, string Status, DateTimeOffset? ExpiresAt)>();
    var competencies = new List<(string Name, string Status, DateTimeOffset? ExpiresAt)>();
    var restrictions = new List<(string Type, string Skill, string Reason, DateTimeOffset StartsAt, DateTimeOffset? EndsAt)>();
    var connection = context.Database.GetDbConnection();
    var opened = connection.State != System.Data.ConnectionState.Open;

    try
    {
        if (opened) connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "select compliance_type,status,expires_at from worker_compliance_records where care_worker_id=@worker and organization_id=@organization";
            AddParameter(command, "worker", careWorkerId);
            AddParameter(command, "organization", tenant.OrganizationId);
            using var reader = command.ExecuteReader();
            while (reader.Read()) compliance.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2)));
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "select course_name,category,status,expires_at from worker_training_records where care_worker_id=@worker and organization_id=@organization";
            AddParameter(command, "worker", careWorkerId);
            AddParameter(command, "organization", tenant.OrganizationId);
            using var reader = command.ExecuteReader();
            while (reader.Read()) training.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3)));
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "select competency,status,expires_at from worker_competency_records where care_worker_id=@worker and organization_id=@organization";
            AddParameter(command, "worker", careWorkerId);
            AddParameter(command, "organization", tenant.OrganizationId);
            using var reader = command.ExecuteReader();
            while (reader.Read()) competencies.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2)));
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "select restriction_type,skill,reason,starts_at,ends_at from worker_assignment_restrictions where care_worker_id=@worker and organization_id=@organization and status='Active'";
            AddParameter(command, "worker", careWorkerId);
            AddParameter(command, "organization", tenant.OrganizationId);
            using var reader = command.ExecuteReader();
            while (reader.Read()) restrictions.Add((reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetFieldValue<DateTimeOffset>(3),reader.IsDBNull(4)?null:reader.GetFieldValue<DateTimeOffset>(4)));
        }
    }
    catch (System.Data.Common.DbException)
    {
        // Compatibility fallback while older/test databases are awaiting the workforce migration.
        return result;
    }
    finally
    {
        if (opened && connection.State == System.Data.ConnectionState.Open) connection.Close();
    }

    var activeAt = startsAt.ToUniversalTime();
    static bool Valid(string status, DateTimeOffset? expiresAt, DateTimeOffset activeAt) =>
        status.Equals("Valid", StringComparison.OrdinalIgnoreCase) && (expiresAt is null || expiresAt > activeAt);

    foreach (var restriction in restrictions.Where(x => x.StartsAt <= activeAt && (x.EndsAt is null || x.EndsAt > activeAt)))
    {
        var matches = restriction.Type != "Skill" || string.IsNullOrWhiteSpace(restriction.Skill) || (requiredSkills ?? "").Contains(restriction.Skill,StringComparison.OrdinalIgnoreCase);
        if (matches) result.Add(new { careWorkerId, category = "Restriction", code = restriction.Type == "Medication" ? "medication-restricted" : "assignment-restricted", skill = restriction.Skill, reason = restriction.Reason });
    }

    foreach (var complianceType in new[] { "DBS", "Right to Work" })
    {
        var records = compliance.Where(item => item.Type.Equals(complianceType, StringComparison.OrdinalIgnoreCase)).ToList();
        if (records.Count > 0 && !records.Any(item => Valid(item.Status, item.ExpiresAt, activeAt)))
        {
            result.Add(new { careWorkerId, category = "Compliance", code = complianceType == "DBS" ? "dbs-invalid" : "right-to-work-invalid", reason = $"{complianceType} evidence is expired or invalid at the visit time." });
        }
    }

    var mandatoryTraining = training.Where(item => item.Category.Equals("Mandatory", StringComparison.OrdinalIgnoreCase)).ToList();
    if (mandatoryTraining.Count > 0 && !mandatoryTraining.Any(item => Valid(item.Status, item.ExpiresAt, activeAt)))
    {
        result.Add(new { careWorkerId, category = "Compliance", code = "mandatory-training-invalid", reason = "All recorded mandatory training is expired or invalid at the visit time." });
    }

    var required = (requiredSkills ?? "")
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    if (required.Count == 0) return result;

    var validEvidence = competencies
        .Where(item => Valid(item.Status, item.ExpiresAt, activeAt))
        .Select(item => item.Name)
        .Concat(training.Where(item => Valid(item.Status, item.ExpiresAt, activeAt)).Select(item => item.Name))
        .ToList();
    var worker = context.CareWorkers.AsNoTracking().FirstOrDefault(item => item.Id == careWorkerId);
    if (worker is not null) validEvidence.Add(worker.Specialization);

    foreach (var skill in required.Where(skill => !validEvidence.Any(evidence => evidence.Contains(skill, StringComparison.OrdinalIgnoreCase))))
    {
        result.Add(new { careWorkerId, category = "Skill", code = "skill-gap", skill, reason = $"No current competency or training evidence matches required skill '{skill}'." });
    }

    return result;
}

static bool TryGetIdempotentResource(CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, string endpoint, string key, out Guid resourceId)
{
    resourceId = Guid.Empty;
    if (string.IsNullOrWhiteSpace(key) || context.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
    {
        return false;
    }

    var connection = context.Database.GetDbConnection();
    var opened = connection.State != System.Data.ConnectionState.Open;
    try
    {
        if (opened) connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "select resource_id from api_idempotency_keys where organization_id=@organization and actor_user_id=@actor and endpoint=@endpoint and idempotency_key=@key";
        AddParameter(command, "organization", tenant.OrganizationId);
        AddParameter(command, "actor", currentUser.UserId ?? Guid.Empty);
        AddParameter(command, "endpoint", endpoint);
        AddParameter(command, "key", key.Trim());
        var value = command.ExecuteScalar();
        if (value is not Guid id) return false;
        resourceId = id;
        return true;
    }
    catch
    {
        return false;
    }
    finally
    {
        if (opened) connection.Close();
    }
}

static void StoreIdempotentResource(CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, string endpoint, string key, string resourceType, Guid resourceId)
{
    if (string.IsNullOrWhiteSpace(key) || context.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
    {
        return;
    }

    var connection = context.Database.GetDbConnection();
    var opened = connection.State != System.Data.ConnectionState.Open;
    try
    {
        if (opened) connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "insert into api_idempotency_keys(id,organization_id,branch_id,actor_user_id,endpoint,idempotency_key,resource_type,resource_id) values(@id,@organization,@branch,@actor,@endpoint,@key,@type,@resource) on conflict(organization_id,actor_user_id,endpoint,idempotency_key) do nothing";
        AddParameter(command, "id", Guid.NewGuid());
        AddParameter(command, "organization", tenant.OrganizationId);
        AddParameter(command, "branch", tenant.BranchId ?? TenantDefaults.BranchId);
        AddParameter(command, "actor", currentUser.UserId ?? Guid.Empty);
        AddParameter(command, "endpoint", endpoint);
        AddParameter(command, "key", key.Trim());
        AddParameter(command, "type", resourceType);
        AddParameter(command, "resource", resourceId);
        command.ExecuteNonQuery();
    }
    finally
    {
        if (opened) connection.Close();
    }
}

static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
{
    var parameter = command.CreateParameter();
    parameter.ParameterName = name;
    parameter.Value = value;
    command.Parameters.Add(parameter);
}

static IEnumerable<DateTimeOffset> ExpandRecurringStarts(DateTimeOffset startsAt, string frequency, int occurrences)
{
    for (var index = 0; index < occurrences; index++)
    {
        yield return frequency.Trim().ToLowerInvariant() switch
        {
            "daily" => startsAt.AddDays(index),
            "weekly" => startsAt.AddDays(index * 7),
            "fortnightly" => startsAt.AddDays(index * 14),
            _ => throw new InvalidOperationException("Frequency must be Daily, Weekly, or Fortnightly.")
        };
    }
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

    var profile = context.Database.SqlQueryRaw<int>("select 1 as \"Value\" from medication_safety_profiles where medication_id={0} and organization_id={1} and reconciliation_status='Verified' and last_reconciled_at is not null and reconciled_by<>'' limit 1", medicationId, tenant.OrganizationId).Any();
    if (!profile)
    {
        return Error("Medication must have a verified reconciliation profile before MAR scheduling.");
    }

    return ValidateCareWorkerReference(careWorkerId, context, tenant);
}

static IResult CompleteMedicationAdministration(Guid id, string outcome, CompleteMedicationAdministrationRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, IConfiguration configuration)
{
    if (!configuration.GetValue<bool>("MedicationSafety:EmarProductionEnabled")) return Results.Json(new { message = "eMAR administration is disabled until the medication clinical-safety gate is approved." }, statusCode: StatusCodes.Status423Locked);
    var record = context.MedicationAdministrationRecords.Find(id);
    if (record is null || !tenant.CanAccess(record.OrganizationId, record.BranchId)) return Results.NotFound();

    var denied = RequireAssignedVisitForCareWorker(record.VisitId, context, tenant, currentUser);
    if (denied is not null) return denied;

    if (outcome is "Refused" or "Missed" or "Held" && string.IsNullOrWhiteSpace(request.Notes)) return Results.BadRequest(new { message = "A reason/note is required for refused, missed, or held medication outcomes." });
    if (record.Outcome != "Scheduled") return Results.Conflict(new { message = "Medication administration record already has a final outcome." });

    var completed = record with
    {
        AdministeredAt = request.AdministeredAt ?? DateTimeOffset.UtcNow,
        Outcome = outcome,
        Notes = request.Notes?.Trim() ?? ""
    };
    context.Entry(record).CurrentValues.SetValues(completed);
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), $"emar.{outcome.ToLowerInvariant()}", currentUser.UserName, nameof(MedicationAdministrationRecord), id, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    context.SaveChanges();
    return Results.Ok(completed);
}

static bool PilotSeedAccessAllowed(HttpContext httpContext, IConfiguration configuration)
{
    if (!string.Equals(configuration["PilotSeed:Enabled"], "true", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var expectedKey = configuration["PilotSeed:Key"];
    return !string.IsNullOrWhiteSpace(expectedKey) &&
        httpContext.Request.Headers.TryGetValue("X-Pilot-Seed-Key", out var providedKey) &&
        string.Equals(providedKey.ToString(), expectedKey, StringComparison.Ordinal);
}
static bool DemoAccessAllowed(HttpContext httpContext, IConfiguration configuration, IWebHostEnvironment environment)
{
    if (environment.IsProduction())
    {
        return false;
    }

    if (!string.Equals(configuration["Demo:Enabled"], "true", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var expectedKey = configuration["Demo:SeedKey"];
    return !string.IsNullOrWhiteSpace(expectedKey) &&
        httpContext.Request.Headers.TryGetValue("X-Demo-Key", out var providedKey) &&
        string.Equals(providedKey.ToString(), expectedKey, StringComparison.Ordinal);
}

static bool IsFamilyCarePlanRoute(HttpRequest request)
{
    var path = request.Path.Value ?? string.Empty;
    if (!path.StartsWith("/api/phase1/care-plans/", StringComparison.OrdinalIgnoreCase)) return false;
    if (HttpMethods.IsGet(request.Method) &&
        (path.EndsWith("/lifecycle", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/versions", StringComparison.OrdinalIgnoreCase))) return true;
    return HttpMethods.IsPost(request.Method) && path.EndsWith("/signatures", StringComparison.OrdinalIgnoreCase);
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

    context.Database.ExecuteSqlRaw("""
        ALTER TABLE "AppUsers"
        ADD COLUMN IF NOT EXISTS "FamilyMemberId" uuid;
        """);

    context.Database.ExecuteSqlRaw("""
        CREATE INDEX IF NOT EXISTS "IX_AppUsers_FamilyMemberId"
        ON "AppUsers" ("FamilyMemberId");
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
public sealed record CreateRecurringVisitRequest(Guid ServiceUserId, Guid CareWorkerId, DateTimeOffset StartsAt, string VisitType, int DurationMinutes, string RequiredSkills, string Frequency, int Occurrences, IReadOnlyCollection<Guid>? AdditionalCareWorkerIds = null);
public sealed record CreateMedicationRequest(Guid ServiceUserId, string Name, string Dosage, string Route, string Schedule, bool IsPrn, string Pharmacy, string AllergyWarning);
public sealed record CreateMedicationAdministrationRecordRequest(Guid MedicationId, Guid VisitId, Guid CareWorkerId, DateTimeOffset ScheduledAt, string Notes);
public sealed record CompleteMedicationAdministrationRequest(DateTimeOffset? AdministeredAt, string Notes);
public sealed record CreateOrganizationRequest(string Name, string Plan);
public sealed record CreateBranchRequest(string Name, string Region, Guid? OrganizationId);
public sealed record FamilyPreferencesRequest(bool EmailNotifications, bool SmsNotifications, bool MonthlyDigest, bool IncidentAlerts);
public sealed record RecordPaymentRequest(decimal Amount, string Reference);
public sealed record RejectFinancialRequest(string Reason);

public partial class Program;
