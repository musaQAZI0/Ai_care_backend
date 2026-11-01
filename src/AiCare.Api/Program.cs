using System.Text;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
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

if (int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var renderPort) && renderPort is > 0 and <= 65535)
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{renderPort}");
}

builder.Services.AddCors(options =>
{
    options.AddPolicy("ReactClient", policy =>
    {
        var configuredOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        var localOrigins = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing")
            ? new[] { "http://127.0.0.1:5173", "http://localhost:5173" }
            : [];
        var allowedOrigins = configuredOrigins
            .Where(origin => !string.IsNullOrWhiteSpace(origin))
            .Concat(localOrigins)
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
builder.Services.AddScoped<AdminPermissionService>();
builder.Services.AddHttpClient();
builder.Services.AddScoped<PasswordResetEmailSender>();
var decisionSupportMode = builder.Configuration["MedicationSafety:DecisionSupportMode"] ?? "Disabled";
if (!Enum.TryParse<MedicationDecisionSupportMode>(decisionSupportMode, ignoreCase: true, out var parsedDecisionSupportMode)
    || parsedDecisionSupportMode != MedicationDecisionSupportMode.Disabled)
{
    throw new InvalidOperationException(
        "Medication decision support is not configured. V1 supports Disabled only; LocalRules and ExternalProvider require a reviewed implementation.");
}
builder.Services.AddSingleton<IMedicationDecisionSupport, DisabledMedicationDecisionSupport>();
builder.Services.AddHttpClient<IMedicationTerminologyService, NhsDmdTerminologyService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddInfrastructure(connectionString);
builder.Services.AddScoped<MedicationChangeActivationService>();
builder.Services.AddHostedService<MedicationChangeActivationWorker>();
builder.Services.AddScoped<EmarMonitoringService>();
if (!builder.Environment.IsEnvironment("Testing")) builder.Services.AddHostedService<EmarMonitoringWorker>();
builder.Services.AddScoped<VisitAlertMonitoringService>();
if (!builder.Environment.IsEnvironment("Testing")) builder.Services.AddHostedService<VisitAlertMonitoringWorker>();

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
    options.AddPolicy("Phase1User", policy => policy
        .RequireRole("ServiceUser", "FamilyMember", "CareWorker", "CareCoordinator", "CareManager", "Administrator", "BackOffice")
        .RequireAssertion(context =>
            Guid.TryParse(context.User.FindFirst("organization_id")?.Value, out var organizationId) &&
            organizationId != Guid.Empty &&
            (context.User.IsInRole("Administrator") || context.User.IsInRole("BackOffice") ||
             Guid.TryParse(context.User.FindFirst("branch_id")?.Value, out var branchId) && branchId != Guid.Empty)));
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
    else if (builder.Configuration.GetValue("Database:RunMigrationsOnStartup", !app.Environment.IsProduction()))
    {
        context.Database.Migrate();
        EnsureRuntimeSchema(context);
    }
    else
    {
        app.Logger.LogInformation("Automatic database migrations are disabled; expecting a completed deployment migration step.");
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
app.UseMiddleware<AdminPermissionGateMiddleware>();
if (!app.Environment.IsEnvironment("Testing")) app.UseMiddleware<SensitiveOperationStepUpMiddleware>();
app.UseAuthorization();
app.UseMiddleware<SubscriptionEntitlementMiddleware>();

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
    catch (Exception)
    {
        return Results.Problem("Database health check failed.", statusCode: StatusCodes.Status503ServiceUnavailable);
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
        ? Results.Ok(new { status = "healthy", provider, checkedAt = DateTimeOffset.UtcNow })
        : Results.Problem("Storage health check failed.", statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/health/ready", (CareDbContext context, IConfiguration configuration) =>
{
    try
    {
        var provider = configuration["Storage:Provider"] ?? "Local";
        var storageReady = !string.Equals(provider, "Supabase", StringComparison.OrdinalIgnoreCase) ||
            (HasConfig(configuration, "Supabase:Url") && HasConfig(configuration, "Supabase:ServiceRoleKey") && HasConfig(configuration, "Supabase:Bucket"));
        return context.Database.CanConnect() && storageReady
            ? Results.Ok(new { status = "ready", checkedAt = DateTimeOffset.UtcNow })
            : Results.Problem("Service readiness check failed.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception)
    {
        return Results.Problem("Service readiness check failed.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

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

    var publishedVisitIds = PublishedRotaAccess.CurrentVisitIds(context, tenant, currentUser.CareWorkerId.Value);
    var additionalVisitIds = PublishedRotaAccess.AdditionalVisitIds(context, tenant, currentUser.CareWorkerId.Value);
    var serviceUserIds = context.Visits.AsNoTracking()
        .Where(visit => visit.CareWorkerId == currentUser.CareWorkerId || additionalVisitIds.Contains(visit.Id))
        .AsEnumerable()
        .Where(visit => TenantVisible(tenant, visit.OrganizationId, visit.BranchId))
        .Where(visit => visit.Status != VisitStatus.Scheduled || visit.StartsAt <= DateTimeOffset.UtcNow || publishedVisitIds.Contains(visit.Id))
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

    if (InvalidContact(request.Contact)) return Error("Check contact details: valid email, supported contact method, and field length limits are required.");
    if (ContactEmailInUse(context, tenant, request.Contact, "ServiceUser")) return Results.Conflict(new { message = "This contact email is already used by another profile." });

    var normalizedName = request.FullName.Trim().ToUpperInvariant();
    var duplicate = context.ServiceUsers.AsNoTracking().AsEnumerable().Any(item =>
        TenantVisible(tenant, item.OrganizationId, item.BranchId) &&
        item.DateOfBirth == request.DateOfBirth &&
        item.FullName.Trim().ToUpperInvariant() == normalizedName);
    if (duplicate) return Results.Conflict(new { message = "A person with the same name and date of birth already exists." });

    var serviceUser = repository.AddServiceUser(request);
    return Results.Created($"/api/phase1/service-users/{serviceUser.Id}", serviceUser);
});
phase1.MapPut("/service-users/{id:guid}", (Guid id, CreateServiceUserRequest request, ICareRepository repository, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
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

    if (InvalidContact(request.Contact)) return Error("Check contact details: valid email, supported contact method, and field length limits are required.");
    if (ContactEmailInUse(context, tenant, request.Contact, "ServiceUser", id)) return Results.Conflict(new { message = "This contact email is already used by another profile." });

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
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareManager, UserRole.CareCoordinator, UserRole.BackOffice, UserRole.CareWorker);
    if (denied is not null) return denied;
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
phase1.MapGet("/care-workers/{id:guid}", (Guid id, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareManager, UserRole.CareCoordinator, UserRole.BackOffice, UserRole.CareWorker);
    if (denied is not null) return denied;
    if (currentUser.IsCareWorker && currentUser.CareWorkerId != id) return Results.NotFound();
    var worker = context.CareWorkers.AsNoTracking().FirstOrDefault(item => item.Id == id && item.OrganizationId == tenant.OrganizationId);
    if (worker is null || !tenant.CanAccess(worker.OrganizationId, worker.BranchId)) return Results.NotFound();
    var visits = context.Visits.AsNoTracking().Where(item => item.CareWorkerId == id && item.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || item.BranchId == tenant.BranchId)).OrderByDescending(item => item.StartsAt).Take(25).ToList();
    return Results.Ok(new { worker, visits });
});
phase1.MapPost("/care-workers", (CreateCareWorkerRequest request, ICareRepository repository, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareManager);
    if (denied is not null) return denied;
    if (Missing(request.FullName, request.Specialization, request.Availability) ||
        request.FullName.Length > 200 || request.Specialization.Length > 200 || request.Availability.Length > 200 ||
        request.DbsStatus is null || request.TrainingCompliance is null || request.TravelRadius is null ||
        request.DbsStatus.Length > 80 || request.TrainingCompliance.Length > 80 || request.TravelRadius.Length > 80)
    {
        return Error("Provide a full name, specialisation, and availability within the field length limits.");
    }

    if (InvalidContact(request.Contact)) return Error("Check contact details: valid email, supported contact method, and field length limits are required.");
    if (ContactEmailInUse(context, tenant, request.Contact, "CareWorker")) return Results.Conflict(new { message = "This contact email is already used by another profile." });

    var careWorker = repository.AddCareWorker(request);
    return Results.Created($"/api/phase1/care-workers/{careWorker.Id}", careWorker);
});
phase1.MapPut("/care-workers/{id:guid}", (Guid id, CreateCareWorkerRequest request, ICareRepository repository, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareManager);
    if (denied is not null) return denied;
    if (Missing(request.FullName, request.Specialization, request.Availability) ||
        request.FullName.Length > 200 || request.Specialization.Length > 200 || request.Availability.Length > 200 ||
        request.DbsStatus is null || request.TrainingCompliance is null || request.TravelRadius is null ||
        request.DbsStatus.Length > 80 || request.TrainingCompliance.Length > 80 || request.TravelRadius.Length > 80)
    {
        return Error("Provide a full name, specialisation, and availability within the field length limits.");
    }

    if (InvalidContact(request.Contact)) return Error("Check contact details: valid email, supported contact method, and field length limits are required.");
    if (ContactEmailInUse(context, tenant, request.Contact, "CareWorker", id)) return Results.Conflict(new { message = "This contact email is already used by another profile." });

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

    var publishedVisitIds = PublishedRotaAccess.CurrentVisitIds(context, tenant, currentUser.CareWorkerId.Value);
    var additionalVisitIds = PublishedRotaAccess.AdditionalVisitIds(context, tenant, currentUser.CareWorkerId.Value);
    var visits = context.Visits.AsNoTracking()
        .Where(visit => visit.CareWorkerId == currentUser.CareWorkerId || additionalVisitIds.Contains(visit.Id))
        .AsEnumerable()
        .Where(visit => TenantVisible(tenant, visit.OrganizationId, visit.BranchId))
        .Where(visit => visit.Status != VisitStatus.Scheduled || visit.StartsAt <= DateTimeOffset.UtcNow || publishedVisitIds.Contains(visit.Id))
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
phase1.MapGet("/rota-publications", async (CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, CancellationToken cancellationToken) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager, UserRole.BackOffice);
    if (denied is not null) return denied;
    var publications = await context.Database.SqlQuery<RotaPublicationSummary>($"""
        select p.id as "Id",p.branch_id as "BranchId",p.week_start as "WeekStart",p.revision as "Revision",
               p.published_at as "PublishedAt",p.published_by as "PublishedBy",
               (select count(*)::int from rota_publication_visits pv where pv.publication_id=p.id) as "VisitCount",
               (select count(distinct workers.care_worker_id)::int from (
                 select pv.care_worker_id from rota_publication_visits pv where pv.publication_id=p.id
                 union select pa.care_worker_id from rota_publication_additional_workers pa where pa.publication_id=p.id
               ) workers) as "WorkerCount",
               (select count(*)::int from rota_publication_acknowledgements a where a.publication_id=p.id) as "AcknowledgedCount",
               ((select count(*) from "Visits" v where v."OrganizationId"={tenant.OrganizationId}
                   and coalesce(v."BranchId",{TenantDefaults.BranchId})=p.branch_id and v."Status"<>'Cancelled'
                   and (v."StartsAt" at time zone 'Europe/London')::date>=p.week_start
                   and (v."StartsAt" at time zone 'Europe/London')::date<p.week_start+7)
                 <> (select count(*) from rota_publication_visits pv where pv.publication_id=p.id)
                 or exists(select 1 from unassigned_visits u where u.organization_id=p.organization_id and u.branch_id=p.branch_id
                   and u.status='Open' and (u.starts_at at time zone 'Europe/London')::date>=p.week_start
                   and (u.starts_at at time zone 'Europe/London')::date<p.week_start+7)
                 or exists(select 1 from rota_publication_visits pv join "Visits" v on v."Id"=pv.visit_id
                   where pv.publication_id=p.id and (v."CareWorkerId"<>pv.care_worker_id or v."StartsAt"<>pv.starts_at
                     or v."DurationMinutes"<>pv.duration_minutes or v."VisitType"<>pv.visit_type or v."Status"='Cancelled'))
                 or exists(select 1 from visit_care_worker_assignments a join rota_publication_visits pv on pv.visit_id=a.visit_id
                   where pv.publication_id=p.id and a.organization_id=p.organization_id
                     and not exists(select 1 from rota_publication_additional_workers pa where pa.publication_id=p.id
                       and pa.visit_id=a.visit_id and pa.care_worker_id=a.care_worker_id))
                 or exists(select 1 from rota_publication_additional_workers pa where pa.publication_id=p.id
                   and not exists(select 1 from visit_care_worker_assignments a where a.visit_id=pa.visit_id
                     and a.care_worker_id=pa.care_worker_id and a.organization_id=p.organization_id))) as "NeedsRepublish"
        from rota_publications p
        where p.organization_id={tenant.OrganizationId} and ({tenant.IsOrganizationWide} or p.branch_id={tenant.BranchId})
          and p.week_start>={DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-21))}
          and not exists(select 1 from rota_publications newer where newer.organization_id=p.organization_id
            and newer.branch_id=p.branch_id and newer.week_start=p.week_start and newer.revision>p.revision)
        order by p.week_start desc,p.branch_id
        """).ToListAsync(cancellationToken);
    return Results.Ok(publications);
});
phase1.MapPost("/rota-publications", async (PublishRotaRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, CancellationToken cancellationToken) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;
    if (request.BranchId == Guid.Empty || !tenant.CanAccess(tenant.OrganizationId, request.BranchId)
        || !await context.Branches.AsNoTracking().AnyAsync(x => x.Id == request.BranchId && x.OrganizationId == tenant.OrganizationId, cancellationToken)) return Results.NotFound();
    if (request.WeekStart.DayOfWeek != DayOfWeek.Monday) return Error("Choose a Monday as the rota week start.");
    return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync($"select pg_advisory_xact_lock(hashtextextended({$"rota:{tenant.OrganizationId}:{request.BranchId}:{request.WeekStart}"},0))", cancellationToken);
        var unassigned = await context.Database.SqlQuery<int>($"""
            select count(*)::int as "Value" from unassigned_visits where organization_id={tenant.OrganizationId}
              and branch_id={request.BranchId} and status='Open' and (starts_at at time zone 'Europe/London')::date>={request.WeekStart}
              and (starts_at at time zone 'Europe/London')::date<{request.WeekStart.AddDays(7)}
            """).SingleAsync(cancellationToken);
        if (unassigned > 0) return Results.Conflict(new { message = $"Allocate or cancel {unassigned} unassigned calls before publishing this week." });
        var visitIds = await context.Database.SqlQuery<Guid>($"""
            select v."Id" as "Value" from "Visits" v where v."OrganizationId"={tenant.OrganizationId}
              and coalesce(v."BranchId",{TenantDefaults.BranchId})={request.BranchId} and v."Status"<>'Cancelled'
              and (v."StartsAt" at time zone 'Europe/London')::date>={request.WeekStart}
              and (v."StartsAt" at time zone 'Europe/London')::date<{request.WeekStart.AddDays(7)}
            """).ToListAsync(cancellationToken);
        if (visitIds.Count == 0) return Results.Conflict(new { message = "There are no scheduled visits to publish for this branch and week." });
        var revision = await context.Database.SqlQuery<int>($"""
            select coalesce(max(revision),0)+1 as "Value" from rota_publications
            where organization_id={tenant.OrganizationId} and branch_id={request.BranchId} and week_start={request.WeekStart}
            """).SingleAsync(cancellationToken);
        var publicationId = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            insert into rota_publications(id,organization_id,branch_id,week_start,revision,published_by)
            values({publicationId},{tenant.OrganizationId},{request.BranchId},{request.WeekStart},{revision},{currentUser.UserName})
            """, cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            insert into rota_publication_visits(publication_id,visit_id,care_worker_id,starts_at,duration_minutes,visit_type)
            select {publicationId},v."Id",v."CareWorkerId",v."StartsAt",v."DurationMinutes",v."VisitType"
            from "Visits" v where v."Id"=any({visitIds.ToArray()})
            """, cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            insert into rota_publication_additional_workers(publication_id,visit_id,care_worker_id)
            select {publicationId},a.visit_id,a.care_worker_id from visit_care_worker_assignments a
            where a.organization_id={tenant.OrganizationId} and a.visit_id=any({visitIds.ToArray()})
            """, cancellationToken);
        AddAudit(context, tenant, currentUser, "rota.published", "RotaPublication", publicationId, branchId: request.BranchId);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Created($"/api/phase1/rota-publications/{publicationId}", new { id = publicationId, revision, visitCount = visitIds.Count });
    });
});
phase1.MapGet("/rota-publications/mine", async (CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, CancellationToken cancellationToken) =>
{
    if (!currentUser.IsCareWorker || currentUser.CareWorkerId is not Guid workerId) return Error("Care worker access is required.", StatusCodes.Status403Forbidden);
    var publications = await context.Database.SqlQuery<WorkerRotaPublication>($"""
        select p.id as "Id",p.week_start as "WeekStart",p.revision as "Revision",p.published_at as "PublishedAt",
               count(pv.visit_id)::int as "VisitCount",(a.publication_id is not null) as "Acknowledged"
        from rota_publications p join rota_publication_visits pv on pv.publication_id=p.id
        join "Visits" v on v."Id"=pv.visit_id and v."CareWorkerId"=pv.care_worker_id
          and v."StartsAt"=pv.starts_at and v."DurationMinutes"=pv.duration_minutes
          and v."VisitType"=pv.visit_type and v."Status"<>'Cancelled'
        left join rota_publication_acknowledgements a on a.publication_id=p.id and a.care_worker_id={workerId}
        where p.organization_id={tenant.OrganizationId}
          and (pv.care_worker_id={workerId} or exists(select 1 from rota_publication_additional_workers pa
            join visit_care_worker_assignments a on a.visit_id=pa.visit_id and a.care_worker_id=pa.care_worker_id
            where pa.publication_id=p.id and pa.visit_id=pv.visit_id and pa.care_worker_id={workerId}
              and a.organization_id=p.organization_id))
          and p.week_start>={DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-21))}
          and not exists(select 1 from rota_publications newer where newer.organization_id=p.organization_id
            and newer.branch_id=p.branch_id and newer.week_start=p.week_start and newer.revision>p.revision)
          and (select count(*) from "Visits" current_visit where current_visit."OrganizationId"=p.organization_id
            and coalesce(current_visit."BranchId",{TenantDefaults.BranchId})=p.branch_id
            and current_visit."Status"<>'Cancelled'
            and (current_visit."StartsAt" at time zone 'Europe/London')::date>=p.week_start
            and (current_visit."StartsAt" at time zone 'Europe/London')::date<p.week_start+7)
            = (select count(*) from rota_publication_visits snapshot where snapshot.publication_id=p.id)
          and not exists(select 1 from unassigned_visits u where u.organization_id=p.organization_id and u.branch_id=p.branch_id
            and u.status='Open' and (u.starts_at at time zone 'Europe/London')::date>=p.week_start
            and (u.starts_at at time zone 'Europe/London')::date<p.week_start+7)
          and not exists(select 1 from rota_publication_visits snapshot join "Visits" current_visit on current_visit."Id"=snapshot.visit_id
            where snapshot.publication_id=p.id and (current_visit."CareWorkerId"<>snapshot.care_worker_id
              or current_visit."StartsAt"<>snapshot.starts_at or current_visit."DurationMinutes"<>snapshot.duration_minutes
              or current_visit."VisitType"<>snapshot.visit_type or current_visit."Status"='Cancelled'))
          and not exists(select 1 from visit_care_worker_assignments assignment join rota_publication_visits snapshot on snapshot.visit_id=assignment.visit_id
            where snapshot.publication_id=p.id and assignment.organization_id=p.organization_id
              and not exists(select 1 from rota_publication_additional_workers pa where pa.publication_id=p.id
                and pa.visit_id=assignment.visit_id and pa.care_worker_id=assignment.care_worker_id))
          and not exists(select 1 from rota_publication_additional_workers pa where pa.publication_id=p.id
            and not exists(select 1 from visit_care_worker_assignments assignment where assignment.visit_id=pa.visit_id
              and assignment.care_worker_id=pa.care_worker_id and assignment.organization_id=p.organization_id))
        group by p.id,a.publication_id order by p.week_start desc
        """).ToListAsync(cancellationToken);
    return Results.Ok(publications);
});
phase1.MapPost("/rota-publications/{id:guid}/acknowledge", async (Guid id, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, CancellationToken cancellationToken) =>
{
    if (!currentUser.IsCareWorker || currentUser.CareWorkerId is not Guid workerId) return Error("Care worker access is required.", StatusCodes.Status403Forbidden);
    var count = await context.Database.ExecuteSqlInterpolatedAsync($"""
        insert into rota_publication_acknowledgements(publication_id,care_worker_id,acknowledged_by)
        select p.id,{workerId},{currentUser.UserName} from rota_publications p
        where p.id={id} and p.organization_id={tenant.OrganizationId}
          and not exists(select 1 from rota_publications newer where newer.organization_id=p.organization_id
            and newer.branch_id=p.branch_id and newer.week_start=p.week_start and newer.revision>p.revision)
          and (select count(*) from "Visits" current_visit where current_visit."OrganizationId"=p.organization_id
            and coalesce(current_visit."BranchId",{TenantDefaults.BranchId})=p.branch_id
            and current_visit."Status"<>'Cancelled'
            and (current_visit."StartsAt" at time zone 'Europe/London')::date>=p.week_start
            and (current_visit."StartsAt" at time zone 'Europe/London')::date<p.week_start+7)
            = (select count(*) from rota_publication_visits snapshot where snapshot.publication_id=p.id)
          and not exists(select 1 from unassigned_visits u where u.organization_id=p.organization_id and u.branch_id=p.branch_id
            and u.status='Open' and (u.starts_at at time zone 'Europe/London')::date>=p.week_start
            and (u.starts_at at time zone 'Europe/London')::date<p.week_start+7)
          and not exists(select 1 from rota_publication_visits snapshot join "Visits" current_visit on current_visit."Id"=snapshot.visit_id
            where snapshot.publication_id=p.id and (current_visit."CareWorkerId"<>snapshot.care_worker_id
              or current_visit."StartsAt"<>snapshot.starts_at or current_visit."DurationMinutes"<>snapshot.duration_minutes
              or current_visit."VisitType"<>snapshot.visit_type or current_visit."Status"='Cancelled'))
          and not exists(select 1 from visit_care_worker_assignments assignment join rota_publication_visits snapshot on snapshot.visit_id=assignment.visit_id
            where snapshot.publication_id=p.id and assignment.organization_id=p.organization_id
              and not exists(select 1 from rota_publication_additional_workers pa where pa.publication_id=p.id
                and pa.visit_id=assignment.visit_id and pa.care_worker_id=assignment.care_worker_id))
          and not exists(select 1 from rota_publication_additional_workers pa where pa.publication_id=p.id
            and not exists(select 1 from visit_care_worker_assignments assignment where assignment.visit_id=pa.visit_id
              and assignment.care_worker_id=pa.care_worker_id and assignment.organization_id=p.organization_id))
          and exists(select 1 from rota_publication_visits pv join "Visits" v on v."Id"=pv.visit_id
            where pv.publication_id=p.id and (pv.care_worker_id={workerId} or exists(
              select 1 from rota_publication_additional_workers pa join visit_care_worker_assignments a
                on a.visit_id=pa.visit_id and a.care_worker_id=pa.care_worker_id
              where pa.publication_id=p.id and pa.visit_id=pv.visit_id and pa.care_worker_id={workerId}
                and a.organization_id=p.organization_id))
              and v."StartsAt"=pv.starts_at and v."DurationMinutes"=pv.duration_minutes
              and v."VisitType"=pv.visit_type and v."Status"<>'Cancelled')
        on conflict(publication_id,care_worker_id) do nothing
        """, cancellationToken);
    if (count == 0) return Results.Conflict(new { message = "Rota is already acknowledged or has changed; ask the coordinator to republish it." });
    AddAudit(context, tenant, currentUser, "rota.acknowledged", "RotaPublication", id);
    await context.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
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
        conflicts = FindVisitConflicts(context, tenant, visit.CareWorkerId, visit.StartsAt, visit.DurationMinutes, visit.Id).Count,
        runName = AppointmentWorkflow.RunName(context, tenant.OrganizationId, visit.BranchId ?? TenantDefaults.BranchId, visit.Id),
        appearance = AppointmentConfigurationStore.Load(context, tenant.OrganizationId, visit.BranchId).RosterAppearance,
        monitoring = AppointmentWorkflow.Monitoring(visit, AppointmentConfigurationStore.Load(context, tenant.OrganizationId, visit.BranchId).MonitoringSettings, DateTimeOffset.UtcNow)
    }));
});
phase1.MapPost("/visits/conflicts", (CreateVisitRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;

    if (request.ServiceUserId == Guid.Empty || request.CareWorkerId == Guid.Empty || string.IsNullOrWhiteSpace(request.VisitType) || request.DurationMinutes <= 0)
    {
        return Error("Service user, care worker, visit type, and a positive duration are required.");
    }

    var workerIds = VisitWorkerIds(request.CareWorkerId, request.AdditionalCareWorkerIds);
    var validation = ValidateVisitReferences(request.ServiceUserId, request.CareWorkerId, context, tenant)
        ?? workerIds.Skip(1).Select(id => ValidateCareWorkerReference(id, context, tenant)).FirstOrDefault(result => result is not null)
        ?? ValidateAppointmentBranch(request.ServiceUserId, workerIds, context, tenant);
    if (validation is not null) return validation;

    if (request.ExcludeVisitId is Guid excluded)
    {
        var excludedVisit = context.Visits.AsNoTracking().FirstOrDefault(x => x.Id == excluded && x.OrganizationId == tenant.OrganizationId && x.ServiceUserId == request.ServiceUserId);
        if (excludedVisit is null || !tenant.CanAccess(excludedVisit.OrganizationId, excludedVisit.BranchId)) return Results.NotFound();
    }

    return Results.Ok(AssessVisitBooking(request, context, tenant));
});
phase1.MapGet("/unassigned-visits", async (CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, CancellationToken cancellationToken) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager, UserRole.BackOffice);
    if (denied is not null) return denied;
    var items = await context.Database.SqlQuery<UnassignedVisitQueueItem>($"""
        select u.id as "Id",u.service_user_id as "ServiceUserId",s."FullName" as "ServiceUserName",
               u.branch_id as "BranchId",u.starts_at as "StartsAt",u.visit_type as "VisitType",
               u.duration_minutes as "DurationMinutes",u.required_skills as "RequiredSkills",u.reason as "Reason"
        from unassigned_visits u join "ServiceUsers" s on s."Id"=u.service_user_id
        where u.organization_id={tenant.OrganizationId} and u.status='Open' and u.starts_at>={DateTimeOffset.UtcNow}
          and ({tenant.IsOrganizationWide} or u.branch_id={tenant.BranchId})
        order by u.starts_at
        """).ToListAsync(cancellationToken);
    return Results.Ok(items);
});
phase1.MapPost("/unassigned-visits", async (CreateUnassignedVisitRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, CancellationToken cancellationToken) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;
    if (request.ServiceUserId == Guid.Empty || string.IsNullOrWhiteSpace(request.VisitType) || request.VisitType.Trim().Length > 200 || request.DurationMinutes is < 1 or > 1440 || request.StartsAt <= DateTimeOffset.UtcNow)
        return Error("An active client, future start, visit type, and duration of 1 to 1440 minutes are required.");
    var validation = ValidateServiceUserReference(request.ServiceUserId, context, tenant);
    if (validation is not null) return validation;
    var client = await context.ServiceUsers.AsNoTracking().SingleAsync(x => x.Id == request.ServiceUserId && x.OrganizationId == tenant.OrganizationId, cancellationToken);
    if (new[] { "Archived", "Discharged", "Suspended" }.Contains(client.Status, StringComparer.OrdinalIgnoreCase))
        return Error("Visits cannot be scheduled for an archived, discharged, or suspended client.");
    var branchId = client.BranchId ?? TenantDefaults.BranchId;
    var id = Guid.NewGuid();
    await context.Database.ExecuteSqlInterpolatedAsync($"""
        insert into unassigned_visits(id,service_user_id,organization_id,branch_id,starts_at,visit_type,duration_minutes,required_skills,reason,created_by)
        values({id},{request.ServiceUserId},{tenant.OrganizationId},{branchId},{request.StartsAt},{request.VisitType.Trim()},{request.DurationMinutes},{request.RequiredSkills?.Trim() ?? ""},{request.Reason?.Trim() ?? ""},{currentUser.UserName})
        """, cancellationToken);
    AddAudit(context, tenant, currentUser, "unassigned_visit.created", "UnassignedVisit", id, branchId: branchId);
    await context.SaveChangesAsync(cancellationToken);
    return Results.Created($"/api/phase1/unassigned-visits/{id}", new { id });
});
phase1.MapPost("/unassigned-visits/{id:guid}/assign", async (Guid id, AssignUnassignedVisitRequest request, ICareRepository repository, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, CancellationToken cancellationToken) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;
    if (request.CareWorkerId == Guid.Empty) return Error("Choose a care worker.");
    return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync($"select pg_advisory_xact_lock(hashtextextended({$"unassigned-visit:{tenant.OrganizationId}:{id}"},0))", cancellationToken);
        var item = await context.Database.SqlQuery<UnassignedVisitState>($"""
            select id as "Id",service_user_id as "ServiceUserId",branch_id as "BranchId",starts_at as "StartsAt",
                   visit_type as "VisitType",duration_minutes as "DurationMinutes",required_skills as "RequiredSkills",status as "Status"
            from unassigned_visits where id={id} and organization_id={tenant.OrganizationId}
            """).SingleOrDefaultAsync(cancellationToken);
        if (item is null || !tenant.CanAccess(tenant.OrganizationId, item.BranchId)) return Results.NotFound();
        if (item.Status != "Open" || item.StartsAt <= DateTimeOffset.UtcNow) return Results.Conflict(new { message = "This unassigned call is no longer available." });
        var validation = ValidateVisitReferences(item.ServiceUserId, request.CareWorkerId, context, tenant)
            ?? ValidateAppointmentBranch(item.ServiceUserId, [request.CareWorkerId], context, tenant);
        if (validation is not null) return validation;
        var create = new CreateVisitRequest(item.ServiceUserId, request.CareWorkerId, item.StartsAt, item.VisitType, item.DurationMinutes, item.RequiredSkills, ChangeReason: "Unassigned call allocated");
        var assessment = AssessVisitBooking(create, context, tenant);
        if (assessment.HasConflicts) return Results.Conflict(new { message = VisitBookingError(assessment), assessment });
        var visit = repository.AddVisit(create);
        await context.Database.ExecuteSqlInterpolatedAsync($"update unassigned_visits set status='Assigned',assigned_visit_id={visit.Id},resolved_by={currentUser.UserName},resolved_at=now() where id={id} and organization_id={tenant.OrganizationId}", cancellationToken);
        RecordScheduleChange(context, tenant, currentUser, visit.Id, "Allocated", null, visit, "Unassigned call allocated");
        AddAudit(context, tenant, currentUser, "unassigned_visit.assigned", "UnassignedVisit", id, branchId: item.BranchId);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(visit);
    });
});
phase1.MapPost("/unassigned-visits/{id:guid}/cancel", async (Guid id, CancelUnassignedVisitRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, CancellationToken cancellationToken) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;
    if (string.IsNullOrWhiteSpace(request.Reason)) return Error("A cancellation reason is required.");
    var count = await context.Database.ExecuteSqlInterpolatedAsync($"""
        update unassigned_visits set status='Cancelled',reason={request.Reason.Trim()},resolved_by={currentUser.UserName},resolved_at=now()
        where id={id} and organization_id={tenant.OrganizationId} and status='Open'
          and ({tenant.IsOrganizationWide} or branch_id={tenant.BranchId})
        """, cancellationToken);
    if (count == 0) return Results.NotFound();
    AddAudit(context, tenant, currentUser, "unassigned_visit.cancelled", "UnassignedVisit", id);
    await context.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
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
        ?? workerIds.Skip(1).Select(id => ValidateCareWorkerReference(id, context, tenant)).FirstOrDefault(result => result is not null)
        ?? ValidateAppointmentBranch(request.ServiceUserId, workerIds, context, tenant);
    if (validation is not null) return validation;

    var assessment = AssessVisitBooking(request with { ExcludeVisitId = null }, context, tenant);
    if (assessment.HasConflicts) return Error(VisitBookingError(assessment));

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
        ?? workerIds.Skip(1).Select(workerId => ValidateCareWorkerReference(workerId, context, tenant)).FirstOrDefault(result => result is not null)
        ?? ValidateAppointmentBranch(request.ServiceUserId, workerIds, context, tenant);
    if (validation is not null) return validation;

    var previousVisit = context.Visits.AsNoTracking().FirstOrDefault(item => item.Id == id);
    if (previousVisit is null || !tenant.CanAccess(previousVisit.OrganizationId, previousVisit.BranchId)) return Results.NotFound();
    var updateBranch = previousVisit.BranchId ?? TenantDefaults.BranchId;
    var requestedBranch = context.ServiceUsers.AsNoTracking().Where(x => x.Id == request.ServiceUserId && x.OrganizationId == tenant.OrganizationId).Select(x => x.BranchId).FirstOrDefault() ?? TenantDefaults.BranchId;
    if (requestedBranch != updateBranch) return Error("A visit cannot be moved to another branch through rescheduling.");
    var assessment = AssessVisitBooking(request with { ExcludeVisitId = id }, context, tenant);
    if (assessment.HasConflicts) return Error(VisitBookingError(assessment));
    if (previousVisit.StartsAt != request.StartsAt && context.Database.IsNpgsql())
    {
        var maxChange = context.Database.SqlQuery<int>($"""select coalesce(max_change_minutes,0) as "Value" from appointment_start_variance_settings where organization_id={tenant.OrganizationId}""").SingleOrDefault();
        if (maxChange > 0 && Math.Abs((request.StartsAt - previousVisit.StartsAt).TotalMinutes) > maxChange)
            return Error($"The new start is more than {maxChange} minutes from the scheduled start. Adjust the visit or ask an administrator to change the appointment variance setting.");
    }
    var visit = repository.UpdateVisit(id, request);
    if (visit is not null) SyncVisitWorkerAssignments(context, tenant, visit.Id, request.CareWorkerId, request.AdditionalCareWorkerIds);
    if (visit is not null) RecordScheduleChange(context, tenant, currentUser, visit.Id, previousVisit?.CareWorkerId != visit.CareWorkerId ? "Reassigned" : previousVisit?.StartsAt != visit.StartsAt ? "Rescheduled" : "Updated", previousVisit, visit, request.ChangeReason);
    return visit is null ? Results.NotFound() : Results.Ok(visit);
});
phase1.MapPost("/visits/{id:guid}/move", async (Guid id, MoveVisitRequest request, ICareRepository repository, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, CancellationToken cancellationToken) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;
    var previous = await context.Visits.AsNoTracking().SingleOrDefaultAsync(visit => visit.Id == id && visit.OrganizationId == tenant.OrganizationId, cancellationToken);
    if (previous is null || !tenant.CanAccess(previous.OrganizationId, previous.BranchId)) return Results.NotFound();
    if (previous.Status != VisitStatus.Scheduled || previous.StartsAt <= DateTimeOffset.UtcNow) return Results.Conflict(new { message = "Only future scheduled visits can be moved." });
    if (previous.StartsAt != request.ExpectedStartsAt) return Results.Conflict(new { message = "The visit changed. Refresh the rota and try again." });
    if (request.StartsAt <= DateTimeOffset.UtcNow) return Error("Choose a future start time.");
    var targetWorkerId = request.CareWorkerId ?? previous.CareWorkerId;
    var workerValidation = ValidateVisitReferences(previous.ServiceUserId, targetWorkerId, context, tenant);
    if (workerValidation is not null) return workerValidation;
    var maxChange = await context.Database.SqlQuery<int>($"""
        select coalesce(max_change_minutes,0) as "Value" from appointment_start_variance_settings
        where organization_id={tenant.OrganizationId}
        """).SingleOrDefaultAsync(cancellationToken);
    if (maxChange > 0 && Math.Abs((request.StartsAt - previous.StartsAt).TotalMinutes) > maxChange)
        return Error($"The new start is more than {maxChange} minutes from the scheduled start.");
    var additionalWorkers = await context.Database.SqlQuery<Guid>($"""
        select care_worker_id as "Value" from visit_care_worker_assignments
        where visit_id={id} and organization_id={tenant.OrganizationId}
        """).ToListAsync(cancellationToken);
    if (additionalWorkers.Contains(targetWorkerId)) return Error("The primary care worker is already assigned as an additional worker.");
    workerValidation = ValidateAppointmentBranch(previous.ServiceUserId, VisitWorkerIds(targetWorkerId, additionalWorkers), context, tenant);
    if (workerValidation is not null) return workerValidation;
    var move = new CreateVisitRequest(previous.ServiceUserId, targetWorkerId, request.StartsAt, previous.VisitType,
        previous.DurationMinutes, previous.RequiredSkills, additionalWorkers, "Moved on rota", id);
    var assessment = AssessVisitBooking(move, context, tenant);
    if (assessment.HasConflicts) return Results.Conflict(new { message = VisitBookingError(assessment), assessment });
    var updated = repository.UpdateVisit(id, move);
    if (updated is null) return Results.NotFound();
    SyncVisitWorkerAssignments(context, tenant, id, updated.CareWorkerId, additionalWorkers);
    RecordScheduleChange(context, tenant, currentUser, id, targetWorkerId == previous.CareWorkerId ? "Rescheduled" : "Reassigned", previous, updated, "Moved on rota");
    return Results.Ok(updated);
});
phase1.MapGet("/visit-cover", async (CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, CancellationToken cancellationToken) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;
    var items = await context.Database.SqlQuery<VisitCoverQueueItem>($"""
        select r.id as "Id",r.visit_id as "VisitId",r.branch_id as "BranchId",r.reason as "Reason",r.status as "Status",
               v."ServiceUserId" as "ServiceUserId",s."FullName" as "ServiceUserName",
               v."CareWorkerId" as "CurrentWorkerId",w."FullName" as "CurrentWorkerName",
               v."StartsAt" as "StartsAt",v."DurationMinutes" as "DurationMinutes",v."VisitType" as "VisitType",
               (select count(*)::int from visit_cover_offers o where o.cover_request_id=r.id and o.status='Pending') as "PendingOffers"
        from visit_cover_requests r join "Visits" v on v."Id"=r.visit_id
        join "ServiceUsers" s on s."Id"=v."ServiceUserId"
        join "CareWorkers" w on w."Id"=v."CareWorkerId"
        where r.organization_id={tenant.OrganizationId} and ({tenant.IsOrganizationWide} or r.branch_id={tenant.BranchId ?? TenantDefaults.BranchId})
          and r.status='Open' order by v."StartsAt"
        """).ToListAsync(cancellationToken);
    return Results.Ok(items);
});
phase1.MapGet("/visit-cover/offers/mine", async (CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, CancellationToken cancellationToken) =>
{
    if (!currentUser.IsCareWorker || currentUser.CareWorkerId is not Guid workerId) return Error("Care worker access is required.", StatusCodes.Status403Forbidden);
    var items = await context.Database.SqlQuery<VisitCoverOfferItem>($"""
        select o.id as "Id",o.cover_request_id as "CoverRequestId",r.visit_id as "VisitId",
               s."FullName" as "ServiceUserName",v."StartsAt" as "StartsAt",v."DurationMinutes" as "DurationMinutes",
               v."VisitType" as "VisitType",r.reason as "Reason",o.status as "Status"
        from visit_cover_offers o join visit_cover_requests r on r.id=o.cover_request_id
        join "Visits" v on v."Id"=r.visit_id join "ServiceUsers" s on s."Id"=v."ServiceUserId"
        where o.organization_id={tenant.OrganizationId} and o.care_worker_id={workerId}
          and ({tenant.IsOrganizationWide} or o.branch_id={tenant.BranchId ?? TenantDefaults.BranchId})
          and o.status='Pending' and r.status='Open' order by v."StartsAt"
        """).ToListAsync(cancellationToken);
    return Results.Ok(items);
});
phase1.MapPost("/visits/{id:guid}/cover", async (Guid id, OpenVisitCoverRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, CancellationToken cancellationToken) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;
    if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 500) return Error("A cover reason of up to 500 characters is required.");
    var visit = await context.Visits.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == tenant.OrganizationId, cancellationToken);
    if (visit is null || !tenant.CanAccess(visit.OrganizationId, visit.BranchId)) return Results.NotFound();
    if (visit.Status != VisitStatus.Scheduled || visit.StartsAt <= DateTimeOffset.UtcNow) return Results.Conflict(new { message = "Only a future scheduled visit can request cover." });
    var coverId = Guid.NewGuid();
    try
    {
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            insert into visit_cover_requests(id,visit_id,organization_id,branch_id,original_worker_id,reason,created_by)
            values({coverId},{visit.Id},{tenant.OrganizationId},{visit.BranchId ?? TenantDefaults.BranchId},{visit.CareWorkerId},{request.Reason.Trim()},{currentUser.UserName})
            """, cancellationToken);
    }
    catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
    {
        return Results.Conflict(new { message = "This visit already has an open cover request." });
    }
    AddAudit(context, tenant, currentUser, "visit_cover.opened", "VisitCoverRequest", coverId, branchId: visit.BranchId);
    await context.SaveChangesAsync(cancellationToken);
    return Results.Created($"/api/phase1/visit-cover/{coverId}", new { id = coverId });
});
phase1.MapPost("/visit-cover/{id:guid}/offers", async (Guid id, OfferVisitCoverRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, CancellationToken cancellationToken) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;
    return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var cover = await context.Database.SqlQuery<VisitCoverState>($"""
            select r.id as "Id",r.visit_id as "VisitId",r.branch_id as "BranchId",r.status as "Status"
            from visit_cover_requests r where r.id={id} and r.organization_id={tenant.OrganizationId}
            """).SingleOrDefaultAsync(cancellationToken);
        if (cover is null || !tenant.CanAccess(tenant.OrganizationId, cover.BranchId)) return Results.NotFound();
        await context.Database.ExecuteSqlInterpolatedAsync($"select pg_advisory_xact_lock(hashtextextended({$"visit-cover:{tenant.OrganizationId}:{cover.VisitId}"},0))", cancellationToken);
        cover = await context.Database.SqlQuery<VisitCoverState>($"""
            select r.id as "Id",r.visit_id as "VisitId",r.branch_id as "BranchId",r.status as "Status"
            from visit_cover_requests r where r.id={id} and r.organization_id={tenant.OrganizationId}
            """).SingleAsync(cancellationToken);
        if (cover.Status != "Open") return Results.Conflict(new { message = "Cover request is no longer open." });
        var visit = await context.Visits.AsNoTracking().SingleOrDefaultAsync(x => x.Id == cover.VisitId && x.OrganizationId == tenant.OrganizationId, cancellationToken);
        var worker = await context.CareWorkers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.CareWorkerId && x.OrganizationId == tenant.OrganizationId, cancellationToken);
        if (visit is null || worker is null || worker.BranchId != cover.BranchId) return Results.NotFound();
        if (visit.Status != VisitStatus.Scheduled || visit.StartsAt <= DateTimeOffset.UtcNow) return Results.Conflict(new { message = "Visit is no longer available for cover." });
        if (worker.Id == visit.CareWorkerId) return Error("Choose a replacement care worker.");
        var doubleUp = await context.Database.SqlQuery<bool>($"select exists(select 1 from visit_care_worker_assignments where visit_id={visit.Id} and organization_id={tenant.OrganizationId}) as \"Value\"").SingleAsync(cancellationToken);
        if (doubleUp) return Results.Conflict(new { message = "Double-up visits need coordinator reassignment." });
        var assessment = AssessVisitBooking(new CreateVisitRequest(visit.ServiceUserId, worker.Id, visit.StartsAt, visit.VisitType, visit.DurationMinutes, visit.RequiredSkills, ExcludeVisitId: visit.Id), context, tenant);
        if (assessment.HasConflicts) return Results.Conflict(new { message = VisitBookingError(assessment), assessment });
        var offerId = Guid.NewGuid();
        var created = await context.Database.ExecuteSqlInterpolatedAsync($"""
            insert into visit_cover_offers(id,cover_request_id,organization_id,branch_id,care_worker_id,offered_by)
            select {offerId},r.id,{tenant.OrganizationId},r.branch_id,{worker.Id},{currentUser.UserName}
            from visit_cover_requests r where r.id={id} and r.organization_id={tenant.OrganizationId} and r.status='Open'
            on conflict (cover_request_id,care_worker_id) do update
              set status='Pending',offered_by=excluded.offered_by,offered_at=now(),responded_at=null,response_reason=''
              where visit_cover_offers.status='Declined'
            """, cancellationToken);
        if (created == 0) return Results.Conflict(new { message = "Cover request is closed or this worker already has a pending offer." });
        offerId = await context.Database.SqlQuery<Guid>($"select id as \"Value\" from visit_cover_offers where cover_request_id={id} and care_worker_id={worker.Id} and organization_id={tenant.OrganizationId}").SingleAsync(cancellationToken);
        AddAudit(context, tenant, currentUser, "visit_cover.offered", "VisitCoverOffer", offerId, branchId: cover.BranchId);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Created($"/api/phase1/visit-cover/offers/{offerId}", new { id = offerId });
    });
});
phase1.MapPost("/visit-cover/{id:guid}/close", async (Guid id, CloseVisitCoverRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, CancellationToken cancellationToken) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;
    if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 500) return Error("A close reason of up to 500 characters is required.");
    return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var cover = await context.Database.SqlQuery<VisitCoverState>($"""
            select id as "Id",visit_id as "VisitId",branch_id as "BranchId",status as "Status"
            from visit_cover_requests where id={id} and organization_id={tenant.OrganizationId}
            """).SingleOrDefaultAsync(cancellationToken);
        if (cover is null || !tenant.CanAccess(tenant.OrganizationId, cover.BranchId)) return Results.NotFound();
        await context.Database.ExecuteSqlInterpolatedAsync($"select pg_advisory_xact_lock(hashtextextended({$"visit-cover:{tenant.OrganizationId}:{cover.VisitId}"},0))", cancellationToken);
        var changed = await context.Database.ExecuteSqlInterpolatedAsync($"""
            update visit_cover_requests set status='Closed',resolved_at=now(),resolved_by={currentUser.UserName},resolution_reason={request.Reason.Trim()}
            where id={id} and organization_id={tenant.OrganizationId} and status='Open'
            """, cancellationToken);
        if (changed == 0) return Results.Conflict(new { message = "Cover request has already been resolved." });
        await context.Database.ExecuteSqlInterpolatedAsync($"update visit_cover_offers set status='Closed',responded_at=now(),response_reason={request.Reason.Trim()} where cover_request_id={id} and organization_id={tenant.OrganizationId} and status='Pending'", cancellationToken);
        AddAudit(context, tenant, currentUser, "visit_cover.closed", "VisitCoverRequest", id, branchId: cover.BranchId);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.NoContent();
    });
});
phase1.MapPost("/visit-cover/offers/{id:guid}/decline", async (Guid id, DeclineVisitCoverRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, CancellationToken cancellationToken) =>
{
    if (!currentUser.IsCareWorker || currentUser.CareWorkerId is not Guid workerId) return Error("Care worker access is required.", StatusCodes.Status403Forbidden);
    if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 500) return Error("A decline reason of up to 500 characters is required.");
    var changed = await context.Database.ExecuteSqlInterpolatedAsync($"""
        update visit_cover_offers o set status='Declined',responded_at=now(),response_reason={request.Reason.Trim()}
        from visit_cover_requests r where o.id={id} and o.cover_request_id=r.id and o.organization_id={tenant.OrganizationId}
          and o.care_worker_id={workerId} and ({tenant.IsOrganizationWide} or o.branch_id={tenant.BranchId ?? TenantDefaults.BranchId})
          and r.status='Open' and o.status='Pending'
        """, cancellationToken);
    if (changed == 0) return Results.Conflict(new { message = "Offer is unavailable or already answered." });
    AddAudit(context, tenant, currentUser, "visit_cover.declined", "VisitCoverOffer", id);
    await context.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
});
phase1.MapPost("/visit-cover/offers/{id:guid}/accept", async (Guid id, ICareRepository repository, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, CancellationToken cancellationToken) =>
{
    if (!currentUser.IsCareWorker || currentUser.CareWorkerId is not Guid workerId) return Error("Care worker access is required.", StatusCodes.Status403Forbidden);
    return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var offer = await context.Database.SqlQuery<VisitCoverOfferState>($"""
            select o.id as "Id",o.cover_request_id as "CoverRequestId",r.visit_id as "VisitId",r.branch_id as "BranchId",
                   o.care_worker_id as "CareWorkerId",o.status as "OfferStatus",r.status as "RequestStatus"
            from visit_cover_offers o join visit_cover_requests r on r.id=o.cover_request_id
            where o.id={id} and o.organization_id={tenant.OrganizationId}
            """).SingleOrDefaultAsync(cancellationToken);
        if (offer is null || offer.CareWorkerId != workerId || !tenant.CanAccess(tenant.OrganizationId, offer.BranchId)) return Results.NotFound();
        await context.Database.ExecuteSqlInterpolatedAsync($"select pg_advisory_xact_lock(hashtextextended({$"visit-cover:{tenant.OrganizationId}:{offer.VisitId}"},0))", cancellationToken);
        offer = await context.Database.SqlQuery<VisitCoverOfferState>($"""
            select o.id as "Id",o.cover_request_id as "CoverRequestId",r.visit_id as "VisitId",r.branch_id as "BranchId",
                   o.care_worker_id as "CareWorkerId",o.status as "OfferStatus",r.status as "RequestStatus"
            from visit_cover_offers o join visit_cover_requests r on r.id=o.cover_request_id
            where o.id={id} and o.organization_id={tenant.OrganizationId}
            """).SingleAsync(cancellationToken);
        if (offer.OfferStatus != "Pending" || offer.RequestStatus != "Open") return Results.Conflict(new { message = "This offer has already been resolved." });
        var visit = await context.Visits.AsNoTracking().SingleOrDefaultAsync(x => x.Id == offer.VisitId && x.OrganizationId == tenant.OrganizationId, cancellationToken);
        if (visit is null || visit.Status != VisitStatus.Scheduled || visit.StartsAt <= DateTimeOffset.UtcNow) return Results.Conflict(new { message = "Visit is no longer available for cover." });
        var doubleUp = await context.Database.SqlQuery<bool>($"select exists(select 1 from visit_care_worker_assignments where visit_id={visit.Id} and organization_id={tenant.OrganizationId}) as \"Value\"").SingleAsync(cancellationToken);
        if (doubleUp) return Results.Conflict(new { message = "Double-up visits need coordinator reassignment." });
        var assessment = AssessVisitBooking(new CreateVisitRequest(visit.ServiceUserId, workerId, visit.StartsAt, visit.VisitType, visit.DurationMinutes, visit.RequiredSkills, ExcludeVisitId: visit.Id), context, tenant);
        if (assessment.HasConflicts) return Results.Conflict(new { message = VisitBookingError(assessment), assessment });
        var request = new CreateVisitRequest(visit.ServiceUserId, workerId, visit.StartsAt, visit.VisitType, visit.DurationMinutes, visit.RequiredSkills, ChangeReason: "Cover offer accepted");
        var updated = repository.UpdateVisit(visit.Id, request);
        if (updated is null) return Results.NotFound();
        await context.Database.ExecuteSqlInterpolatedAsync($"update visit_cover_requests set status='Covered',resolved_at=now(),resolved_by={currentUser.UserName},resolution_reason={"Cover offer accepted"} where id={offer.CoverRequestId} and organization_id={tenant.OrganizationId}", cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync($"update visit_cover_offers set status=case when id={id} then 'Accepted' else 'Closed' end,responded_at=now() where cover_request_id={offer.CoverRequestId} and status='Pending'", cancellationToken);
        RecordScheduleChange(context, tenant, currentUser, visit.Id, "CoverAccepted", visit, updated, "Cover offer accepted");
        AddAudit(context, tenant, currentUser, "visit_cover.accepted", "VisitCoverOffer", id, branchId: offer.BranchId);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(updated);
    });
});
phase1.MapPost("/visits/recurring", (CreateRecurringVisitRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;

    if (request.ServiceUserId == Guid.Empty || request.CareWorkerId == Guid.Empty || Missing(request.VisitType, request.Frequency) || request.DurationMinutes is < 1 or > 1440 || request.Occurrences <= 0)
    {
        return Error("Service user, care worker, visit type, frequency, duration, and occurrences are required.");
    }

    if (request.Occurrences > 60)
    {
        return Error("Recurring visit generation is limited to 60 occurrences.");
    }
    if (!new[] { "Daily", "Weekly", "Fortnightly" }.Contains(request.Frequency.Trim(), StringComparer.OrdinalIgnoreCase))
        return Error("Frequency must be Daily, Weekly, or Fortnightly.");

    var validation = ValidateVisitReferences(request.ServiceUserId, request.CareWorkerId, context, tenant);
    if (validation is not null) return validation;
    var branchValidation = ValidateAppointmentBranch(request.ServiceUserId, VisitWorkerIds(request.CareWorkerId, request.AdditionalCareWorkerIds), context, tenant);
    if (branchValidation is not null) return branchValidation;

    var workerIds = VisitWorkerIds(request.CareWorkerId, request.AdditionalCareWorkerIds);
    var secondaryValidation = workerIds.Skip(1).Select(id => ValidateCareWorkerReference(id, context, tenant)).FirstOrDefault(result => result is not null);
    if (secondaryValidation is not null) return secondaryValidation;
    List<DateTimeOffset> starts;
    try { starts = ExpandRecurringStarts(request.StartsAt, request.Frequency, request.Occurrences).ToList(); }
    catch (ArgumentException error) { return Error(error.Message); }
    var recurringBranch = context.ServiceUsers.AsNoTracking().Where(x => x.Id == request.ServiceUserId && x.OrganizationId == tenant.OrganizationId).Select(x => x.BranchId).FirstOrDefault() ?? TenantDefaults.BranchId;
    foreach (var startsAt in starts)
    {
        var candidate = new CreateVisitRequest(request.ServiceUserId, request.CareWorkerId, startsAt, request.VisitType, request.DurationMinutes, request.RequiredSkills, request.AdditionalCareWorkerIds);
        var assessment = AssessVisitBooking(candidate, context, tenant);
        if (assessment.HasConflicts) return Error($"Recurring series cannot be scheduled at {startsAt:O}: {VisitBookingError(assessment)}");
    }

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
        recurringBranch)).ToList();
    var seriesId = Guid.NewGuid();
    context.Database.CreateExecutionStrategy().Execute(() =>
    {
        context.ChangeTracker.Clear();
        using var transaction = context.Database.BeginTransaction();
        context.Visits.AddRange(visits);
        AddAudit(context, tenant, currentUser, $"visit.recurring_scheduled:{visits.Count}", nameof(Visit), visits.First().Id);
        context.SaveChanges();
        context.Database.ExecuteSqlInterpolated($"""
            insert into recurring_visit_series(id,organization_id,branch_id,service_user_id,frequency,created_by)
            values({seriesId},{tenant.OrganizationId},{recurringBranch},{request.ServiceUserId},{request.Frequency},{currentUser.UserName})
            """);
        for (var index = 0; index < visits.Count; index++)
        {
            context.Database.ExecuteSqlInterpolated($"insert into recurring_visit_members(series_id,visit_id,ordinal) values({seriesId},{visits[index].Id},{index + 1})");
            foreach (var additionalId in workerIds.Where(workerId => workerId != request.CareWorkerId))
                context.Database.ExecuteSqlInterpolated($"""
                    insert into visit_care_worker_assignments(visit_id,care_worker_id,organization_id,branch_id,assignment_role)
                    values({visits[index].Id},{additionalId},{tenant.OrganizationId},{recurringBranch},{"Additional"})
                    """);
        }
        transaction.Commit();
    });
    foreach (var visit in visits) RecordScheduleChange(context, tenant, currentUser, visit.Id, "RecurringCreated", null, visit, "Recurring series created");
    return Results.Created($"/api/phase1/visits/recurring-series/{seriesId}", new { seriesId, count = visits.Count, visits });
});
phase1.MapGet("/visits/recurring-series", async (CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, CancellationToken cancellationToken) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager, UserRole.BackOffice);
    if (denied is not null) return denied;
    var series = await context.Database.SqlQuery<RecurringSeriesSummary>($"""
        select s.id as "Id",s.service_user_id as "ServiceUserId",person."FullName" as "ServiceUserName",
               s.branch_id as "BranchId",s.frequency as "Frequency",first_visit."StartsAt" as "NextStartsAt",
               first_visit."CareWorkerId" as "CareWorkerId",first_visit."VisitType" as "VisitType",
               first_visit."DurationMinutes" as "DurationMinutes",first_visit."RequiredSkills" as "RequiredSkills",
               (select count(*)::int from recurring_visit_members members join "Visits" v on v."Id"=members.visit_id
                 where members.series_id=s.id and v."Status"='Scheduled' and v."StartsAt">{DateTimeOffset.UtcNow}) as "FutureCount"
        from recurring_visit_series s join "ServiceUsers" person on person."Id"=s.service_user_id
        join lateral (select v.* from recurring_visit_members members join "Visits" v on v."Id"=members.visit_id
          where members.series_id=s.id and v."Status"='Scheduled' and v."StartsAt">{DateTimeOffset.UtcNow}
          order by v."StartsAt" limit 1) first_visit on true
        where s.organization_id={tenant.OrganizationId} and ({tenant.IsOrganizationWide} or s.branch_id={tenant.BranchId})
        order by first_visit."StartsAt"
        """).ToListAsync(cancellationToken);
    return Results.Ok(series);
});
phase1.MapPut("/visits/recurring-series/{id:guid}", async (Guid id, EditRecurringSeriesRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, CancellationToken cancellationToken) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;
    if (request.CareWorkerId == Guid.Empty || string.IsNullOrWhiteSpace(request.VisitType) || request.DurationMinutes is < 1 or > 1440 || string.IsNullOrWhiteSpace(request.ChangeReason))
        return Error("Care worker, visit type, duration, and change reason are required.");
    if (request.StartTime is not null && !TimeOnly.TryParseExact(request.StartTime, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
        return Error("Enter a UK start time in HH:mm format.");
    var series = await context.Database.SqlQuery<RecurringSeriesState>($"""
        select id as "Id",service_user_id as "ServiceUserId",branch_id as "BranchId"
        from recurring_visit_series where id={id} and organization_id={tenant.OrganizationId}
        """).SingleOrDefaultAsync(cancellationToken);
    if (series is null || !tenant.CanAccess(tenant.OrganizationId, series.BranchId)) return Results.NotFound();
    var validation = ValidateVisitReferences(series.ServiceUserId, request.CareWorkerId, context, tenant);
    if (validation is not null) return validation;
    var ids = await context.Database.SqlQuery<Guid>($"""
        select members.visit_id as "Value" from recurring_visit_members members join "Visits" v on v."Id"=members.visit_id
        where members.series_id={id} and v."Status"='Scheduled' and v."StartsAt">{DateTimeOffset.UtcNow}
        order by members.ordinal
        """).ToListAsync(cancellationToken);
    if (ids.Count == 0) return Results.Conflict(new { message = "This series has no future scheduled visits to edit." });
    var visits = await context.Visits.AsNoTracking().Where(visit => ids.Contains(visit.Id)).ToListAsync(cancellationToken);
    var changes = new List<(Visit Previous, Guid[] Additional, DateTimeOffset StartsAt)>();
    foreach (var visit in visits)
    {
        var startsAt = visit.StartsAt;
        if (request.StartTime is not null)
        {
            var localDate = AppointmentWorkflow.UkDate(visit.StartsAt);
            if (!TryUkStart(localDate, TimeOnly.ParseExact(request.StartTime, "HH:mm"), out startsAt))
                return Error($"{localDate:yyyy-MM-dd} at {request.StartTime} does not exist in UK time because the clocks change.");
            if (startsAt <= DateTimeOffset.UtcNow) return Error("All edited visits must remain in the future.");
        }
        var additional = request.AdditionalCareWorkerIds?.Distinct().ToArray() ?? await context.Database.SqlQuery<Guid>($"""
            select care_worker_id as "Value" from visit_care_worker_assignments
            where visit_id={visit.Id} and organization_id={tenant.OrganizationId}
            """).ToArrayAsync(cancellationToken);
        var workers = VisitWorkerIds(request.CareWorkerId, additional);
        validation = workers.Skip(1).Select(workerId => ValidateCareWorkerReference(workerId, context, tenant)).FirstOrDefault(result => result is not null)
            ?? ValidateAppointmentBranch(series.ServiceUserId, workers, context, tenant);
        if (validation is not null) return validation;
        var candidate = new CreateVisitRequest(series.ServiceUserId, request.CareWorkerId, startsAt, request.VisitType,
            request.DurationMinutes, request.RequiredSkills ?? "", additional, request.ChangeReason, visit.Id);
        var assessment = AssessVisitBooking(candidate, context, tenant);
        if (assessment.HasConflicts) return Results.Conflict(new { message = $"Visit {visit.StartsAt:O}: {VisitBookingError(assessment)}", assessment });
        changes.Add((visit, additional, startsAt));
    }
    return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync($"select pg_advisory_xact_lock(hashtextextended({$"recurring-series:{tenant.OrganizationId}:{id}"},0))", cancellationToken);
        foreach (var (previous, additional, startsAt) in changes)
        {
            var changed = await context.Visits.Where(visit => visit.Id == previous.Id && visit.StartsAt == previous.StartsAt && visit.Status == VisitStatus.Scheduled)
                .ExecuteUpdateAsync(setters => setters.SetProperty(visit => visit.CareWorkerId, request.CareWorkerId)
                    .SetProperty(visit => visit.StartsAt, startsAt)
                    .SetProperty(visit => visit.VisitType, request.VisitType.Trim())
                    .SetProperty(visit => visit.DurationMinutes, request.DurationMinutes)
                    .SetProperty(visit => visit.RequiredSkills, request.RequiredSkills ?? ""), cancellationToken);
            if (changed != 1) return Results.Conflict(new { message = "A visit changed while editing the series. Refresh and try again." });
            await context.Database.ExecuteSqlInterpolatedAsync($"delete from visit_care_worker_assignments where visit_id={previous.Id} and organization_id={tenant.OrganizationId}", cancellationToken);
            foreach (var additionalId in additional.Where(workerId => workerId != request.CareWorkerId))
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    insert into visit_care_worker_assignments(visit_id,care_worker_id,organization_id,branch_id,assignment_role)
                    values({previous.Id},{additionalId},{tenant.OrganizationId},{series.BranchId},{"Additional"})
                    """, cancellationToken);
        }
        await context.Database.ExecuteSqlInterpolatedAsync($"update recurring_visit_series set updated_at=now(),updated_by={currentUser.UserName} where id={id} and organization_id={tenant.OrganizationId}", cancellationToken);
        AddAudit(context, tenant, currentUser, "visit.recurring_series_updated", "RecurringVisitSeries", id, branchId: series.BranchId);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        foreach (var (previous, _, startsAt) in changes)
            RecordScheduleChange(context, tenant, currentUser, previous.Id, "RecurringUpdated", previous,
                previous with { CareWorkerId = request.CareWorkerId, StartsAt = startsAt, VisitType = request.VisitType.Trim(), DurationMinutes = request.DurationMinutes, RequiredSkills = request.RequiredSkills ?? "" }, request.ChangeReason);
        return Results.Ok(new { updatedCount = changes.Count });
    });
});
phase1.MapPatch("/visits/{id:guid}/status", (Guid id, UpdateVisitStatusRequest request, ICareRepository repository, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;
    var previousVisit = context.Visits.AsNoTracking().FirstOrDefault(item => item.Id == id);
    if (previousVisit is null || !tenant.CanAccess(previousVisit.OrganizationId, previousVisit.BranchId)) return Results.NotFound();
    if (request.Status == VisitStatus.Cancelled && !ValidVisitCancellationReason(context, tenant, request.Reason))
        return Error("Choose an active cancellation reason configured in Administration.");

    var visit = repository.UpdateVisitStatus(id, request.Status);
    if (visit is not null) RecordScheduleChange(context, tenant, currentUser, id, request.Status == VisitStatus.Cancelled ? "Cancelled" : "StatusChanged", previousVisit, visit, request.Reason);
    if (visit is not null && request.Status == VisitStatus.Cancelled)
    {
        AddAudit(context, tenant, currentUser, "visit.cancelled", nameof(Visit), id);
        context.SaveChanges();
    }
    return visit is null ? Results.NotFound() : Results.Ok(visit);
});
phase1.MapDelete("/visits/{id:guid}", (Guid id, string? reason, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;
    var visit = context.Visits.Find(id);
    if (visit is null || !tenant.CanAccess(visit.OrganizationId, visit.BranchId)) return Results.NotFound();
    if (!ValidVisitCancellationReason(context, tenant, reason ?? "Visit archived by delete request"))
        return Error("Choose an active cancellation reason configured in Administration.");
    if (visit.Status == VisitStatus.Completed || visit.Status == VisitStatus.InProgress) return Results.Conflict(new { message = "Started or completed visits require a correction record, not deletion." });
    context.Entry(visit).CurrentValues.SetValues(visit with { Status = VisitStatus.Cancelled });
    RecordScheduleChange(context, tenant, currentUser, id, "Cancelled", visit, visit with { Status = VisitStatus.Cancelled }, reason ?? "Visit archived by delete request");
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
phase1.MapGet("/documents", (CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireDocumentRole(currentUser, allowCareWorker: true);
    if (denied is not null) return denied;

    var documents = context.Documents.AsNoTracking()
        .Where(item => (tenant.IsPlatformOwner || item.OrganizationId == tenant.OrganizationId) &&
            (tenant.IsOrganizationWide || tenant.BranchId == null || item.BranchId == tenant.BranchId) &&
            item.Category != "Withdrawn");
    if (currentUser.IsCareWorker)
    {
        if (currentUser.CareWorkerId is not Guid workerId)
            return Error("Care worker account is not linked to a care worker profile.", StatusCodes.Status403Forbidden);
        var personIds = DocumentPersonIdsForWorker(context, tenant, workerId);
        documents = documents.Where(item => personIds.Contains(item.ServiceUserId));
    }
    return Results.Ok(documents.OrderByDescending(item => item.UploadedAt).ToList().Select(DocumentResponse));
});
phase1.MapGet("/documents/{id:guid}", (Guid id, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireDocumentRole(currentUser, allowCareWorker: true);
    if (denied is not null) return denied;
    var document = context.Documents.AsNoTracking().FirstOrDefault(item => item.Id == id);
    return document is null || document.Category == "Withdrawn" || !CanAccessDocument(document, context, tenant, currentUser)
        ? Results.NotFound()
        : Results.Ok(DocumentResponse(document));
});
phase1.MapPost("/documents", (CreateDocumentRequest request, ICurrentUserContext currentUser) =>
{
    var denied = RequireDocumentRole(currentUser, allowCareWorker: false);
    return denied ?? Error("Document metadata cannot be created directly. Upload the file through /api/phase1/documents/upload.", StatusCodes.Status405MethodNotAllowed);
});
phase1.MapPost("/documents/upload", async (HttpRequest request, IWebHostEnvironment environment, IConfiguration configuration, IHttpClientFactory httpClientFactory, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireDocumentRole(currentUser, allowCareWorker: false);
    if (denied is not null) return denied;
    if (!request.HasFormContentType)
    {
        return Error("Multipart form data is required.");
    }

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    var serviceUserIdValue = form["serviceUserId"].ToString();
    var category = form["category"].ToString();
    if (file is null || file.Length == 0 || !Guid.TryParse(serviceUserIdValue, out var serviceUserId) || Missing(category))
    {
        return Error("File, service user, and category are required.");
    }
    if (category.Trim().Length > 100 || string.Equals(category.Trim(), "Withdrawn", StringComparison.OrdinalIgnoreCase))
        return Error("Choose a valid active document category.");

    var serviceUserValidation = ValidateServiceUserReference(serviceUserId, context, tenant);
    if (serviceUserValidation is not null) return serviceUserValidation;

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

    var document = new DocumentItem(Guid.NewGuid(), serviceUserId, safeFileName, category.Trim(), storagePath,
        currentUser.UserName, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId);
    try
    {
        context.Documents.Add(document);
        AddAudit(context, tenant, currentUser, "document.uploaded", nameof(DocumentItem), document.Id, branchId: document.BranchId);
        await context.SaveChangesAsync(request.HttpContext.RequestAborted);
    }
    catch
    {
        await DeleteUploadedDocumentAsync(storagePath, configuration, httpClientFactory, request.HttpContext.RequestAborted);
        throw;
    }
    return Results.Created($"/api/phase1/documents/{document.Id}", DocumentResponse(document));
});
phase1.MapGet("/documents/{id:guid}/download-url", async (Guid id, HttpRequest request, CareDbContext context, IConfiguration configuration, IHttpClientFactory httpClientFactory, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireDocumentRole(currentUser, allowCareWorker: true);
    if (denied is not null) return denied;
    var document = context.Documents.AsNoTracking().FirstOrDefault(item => item.Id == id);
    if (document is null || document.Category == "Withdrawn" || !CanAccessDocument(document, context, tenant, currentUser))
    {
        return Results.NotFound();
    }

    if (!document.StoragePath.StartsWith("supabase://", StringComparison.OrdinalIgnoreCase))
    {
        AddAudit(context, tenant, currentUser, "document.download_authorized", nameof(DocumentItem), document.Id, branchId: document.BranchId);
        await context.SaveChangesAsync();
        return Results.Ok(new { provider = "Local", url = $"{request.Scheme}://{request.Host}/api/phase1/documents/{id}/content" });
    }

    var signedUrl = await CreateSupabaseSignedUrl(document.StoragePath, configuration, httpClientFactory);
    AddAudit(context, tenant, currentUser, "document.download_url_issued", nameof(DocumentItem), document.Id, branchId: document.BranchId);
    await context.SaveChangesAsync();
    return Results.Ok(new { provider = "Supabase", url = signedUrl, expiresInSeconds = 900 });
});
phase1.MapGet("/documents/{id:guid}/content", (Guid id, CareDbContext context, IWebHostEnvironment environment, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireDocumentRole(currentUser, allowCareWorker: true);
    if (denied is not null) return denied;
    var document = context.Documents.AsNoTracking().FirstOrDefault(item => item.Id == id);
    if (document is null || document.Category == "Withdrawn" || !CanAccessDocument(document, context, tenant, currentUser)) return Results.NotFound();
    if (document.StoragePath.StartsWith("supabase://", StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
    var uploadRoot = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "App_Data", "uploads"));
    var filePath = Path.GetFullPath(document.StoragePath);
    if (!filePath.StartsWith(uploadRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath)) return Results.NotFound();
    AddAudit(context, tenant, currentUser, "document.downloaded", nameof(DocumentItem), document.Id, branchId: document.BranchId);
    context.SaveChanges();
    return Results.File(filePath, "application/octet-stream", document.FileName, enableRangeProcessing: true);
});
phase1.MapPut("/documents/{id:guid}", (Guid id, CreateDocumentRequest request, ICurrentUserContext currentUser) =>
{
    var denied = RequireDocumentRole(currentUser, allowCareWorker: false);
    return denied ?? Error("Uploaded document ownership and storage metadata are immutable. Withdraw this record and upload a replacement.", StatusCodes.Status405MethodNotAllowed);
});
phase1.MapDelete("/documents/{id:guid}", (Guid id, string? reason, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireDocumentRole(currentUser, allowCareWorker: false);
    if (denied is not null) return denied;
    if (string.IsNullOrWhiteSpace(reason)) return Error("A withdrawal reason is required.");
    if (reason.Trim().Length > 300) return Error("The withdrawal reason must be 300 characters or fewer.");
    var document = context.Documents.FirstOrDefault(item => item.Id == id);
    if (document is null || !tenant.CanAccess(document.OrganizationId, document.BranchId)) return Results.NotFound();
    if (document.Category == "Withdrawn") return Results.NoContent();
    context.Entry(document).CurrentValues.SetValues(document with { Category = "Withdrawn" });
    AddAudit(context, tenant, currentUser, $"document.withdrawn: {reason.Trim()}", nameof(DocumentItem), id, branchId: document.BranchId);
    context.SaveChanges();
    return Results.NoContent();
});
phase1.MapGet("/medications", (ICareRepository repository) => Results.Ok(repository.GetMedications()));
phase1.MapGet("/medications/{id:guid}", (Guid id, CareDbContext context, ITenantContext tenant) =>
{
    var medication = context.Medications.AsNoTracking().FirstOrDefault(item => item.Id == id);
    return medication is null || !tenant.CanAccess(medication.OrganizationId, medication.BranchId) ? Results.NotFound() : Results.Ok(medication);
});
phase1.MapPost("/medications", async (CreateMedicationRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, IMedicationTerminologyService terminology, CancellationToken cancellationToken) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;
    if (request.ServiceUserId == Guid.Empty || Missing(request.Name, request.Dosage, request.Route, request.Schedule))
        return Error("Service user, medication name, dosage, route, and schedule are required.");
    var serviceUserValidation = ValidateServiceUserReference(request.ServiceUserId, context, tenant);
    if (serviceUserValidation is not null) return serviceUserValidation;
    var (terminologyError, concept) = await ValidateMedicationTerminologyAsync(request, terminology, cancellationToken);
    if (terminologyError is not null) return terminologyError;
    var medication = new Medication(Guid.NewGuid(), request.ServiceUserId, request.Name.Trim(), request.Dosage.Trim(), request.Route.Trim(), request.Schedule.Trim(), request.IsPrn, request.Pharmacy.Trim(), request.AllergyWarning.Trim(), tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId, concept?.Code, concept?.Display, concept?.System);
    context.Medications.Add(medication);
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "medication.created", currentUser.UserName, nameof(Medication), medication.Id, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    await context.SaveChangesAsync(cancellationToken);
    return Results.Created($"/api/phase1/medications/{medication.Id}", medication);
});
phase1.MapPut("/medications/{id:guid}", async (Guid id, CreateMedicationRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, IMedicationTerminologyService terminology, CancellationToken cancellationToken) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;
    if (request.ServiceUserId == Guid.Empty || Missing(request.Name, request.Dosage, request.Route, request.Schedule))
        return Error("Service user, medication name, dosage, route, and schedule are required.");
    var serviceUserValidation = ValidateServiceUserReference(request.ServiceUserId, context, tenant);
    if (serviceUserValidation is not null) return serviceUserValidation;
    var medication = await context.Medications.FindAsync([id], cancellationToken);
    if (medication is null || !tenant.CanAccess(medication.OrganizationId, medication.BranchId)) return Results.NotFound();
    var (terminologyError, concept) = await ValidateMedicationTerminologyAsync(request, terminology, cancellationToken);
    if (terminologyError is not null) return terminologyError;
    var verifiedProfile = context.Database.SqlQueryRaw<int>(
        "select 1 as \"Value\" from medication_safety_profiles where medication_id={0} and organization_id={1} and branch_id={2} and reconciliation_status='Verified' limit 1",
        id, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId).Any();
    if (verifiedProfile &&
        (medication.ServiceUserId != request.ServiceUserId || medication.Name != request.Name.Trim() ||
         medication.Dosage != request.Dosage.Trim() || medication.Route != request.Route.Trim() ||
         medication.Schedule != request.Schedule.Trim() || medication.IsPrn != request.IsPrn ||
         medication.DmdCode != concept?.Code || medication.AllergyWarning != request.AllergyWarning.Trim()))
        return Results.Conflict(new { message = "Verified medication instructions require a reviewed change request. A different medicine identity requires a new reconciled medication." });
    var updated = medication with
    {
        ServiceUserId = request.ServiceUserId, Name = request.Name.Trim(), Dosage = request.Dosage.Trim(),
        Route = request.Route.Trim(), Schedule = request.Schedule.Trim(), IsPrn = request.IsPrn,
        Pharmacy = request.Pharmacy.Trim(), AllergyWarning = request.AllergyWarning.Trim(),
        DmdCode = concept?.Code, DmdDisplay = concept?.Display, DmdSystem = concept?.System
    };
    context.Entry(medication).CurrentValues.SetValues(updated);
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "medication.updated", currentUser.UserName, nameof(Medication), id, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    await context.SaveChangesAsync(cancellationToken);
    return Results.Ok(updated);
});
phase1.MapDelete("/medications/{id:guid}", (Guid id, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;

    var medication = context.Medications.Find(id);
    if (medication is null || !tenant.CanAccess(medication.OrganizationId, medication.BranchId)) return Results.NotFound();
    var verifiedForStop = context.Database.ProviderName != "Microsoft.EntityFrameworkCore.Sqlite" && context.Database.SqlQueryRaw<int>(
        "select 1 as \"Value\" from medication_safety_profiles where medication_id={0} and organization_id={1} and branch_id={2} and reconciliation_status='Verified' limit 1",
        id, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId).Any();
    if (verifiedForStop) return Results.Conflict(new { message = "Stop a verified medication through a reviewed medication change request." });
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
phase1.MapPost("/mar", async (CreateMedicationAdministrationRecordRequest request, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser, CancellationToken cancellationToken) =>
await context.Database.CreateExecutionStrategy().ExecuteAsync<IResult>(async () =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareCoordinator, UserRole.CareManager);
    if (denied is not null) return denied;

    await using var scheduleTransaction = await context.Database.BeginTransactionAsync(cancellationToken);

    var discontinued = context.Database.SqlQueryRaw<int>(
        "select 1 as \"Value\" from medication_safety_profiles where medication_id={0} and organization_id={1} and branch_id={2} and reconciliation_status='Discontinued' limit 1",
        request.MedicationId, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId).Any();
    if (discontinued) return Results.Conflict(new { message = "This medication has been stopped. A new verified order is required before scheduling." });

    var validation = ValidateMedicationAdministrationReferences(request.MedicationId, request.VisitId, request.CareWorkerId, context, tenant);
    if (validation is not null) return validation;

    var personId = context.Medications.AsNoTracking().Where(item => item.Id == request.MedicationId)
        .Select(item => item.ServiceUserId).Single();
    if (!await MedicationSupportAuthority.AllowsAdministrationAsync(context, tenant.OrganizationId,
            tenant.BranchId ?? TenantDefaults.BranchId, personId, request.MedicationId,
            request.ScheduledAt > DateTimeOffset.UtcNow ? request.ScheduledAt : DateTimeOffset.UtcNow,
            cancellationToken))
        return Results.Conflict(new { message = MedicationSupportAuthority.MissingMessage });

    var pendingEffectiveChange = context.Database.SqlQueryRaw<int>(
        "select 1 as \"Value\" from medication_change_requests where medication_id={0} and organization_id={1} and branch_id={2} and status in ('Pending','Approved') and effective_at<={3} limit 1",
        request.MedicationId, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId, request.ScheduledAt).Any();
    if (pendingEffectiveChange) return Results.Conflict(new { message = "A medication change is due before this dose. Review and activate the change before scheduling." });
    var profileVersion = context.Database.SqlQueryRaw<int?>(
        "select profile_version as \"Value\" from medication_safety_profiles where medication_id={0} and organization_id={1} and branch_id={2} limit 1",
        request.MedicationId, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId).SingleOrDefault();
    var scheduledTimesJson = context.Database.SqlQueryRaw<string>(
        "select scheduled_local_times::text as \"Value\" from medication_safety_profiles where medication_id={0} and organization_id={1} and branch_id={2} limit 1",
        request.MedicationId, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId).SingleOrDefault();
    var fixedTimes = scheduledTimesJson is null ? [] : JsonSerializer.Deserialize<string[]>(scheduledTimesJson) ?? [];
    var scheduledMedication = context.Medications.AsNoTracking().Single(item => item.Id == request.MedicationId);
    DateTimeOffset[] slotInstants = [request.ScheduledAt];
    if (!scheduledMedication.IsPrn && fixedTimes.Length > 0)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var local = TimeZoneInfo.ConvertTime(request.ScheduledAt, zone);
        if (local.Ticks % TimeSpan.TicksPerMinute != 0 ||
            !fixedTimes.Contains(local.ToString("HH:mm", CultureInfo.InvariantCulture), StringComparer.Ordinal))
            return Results.Conflict(new { message = "Scheduled time must match an approved fixed daily UK dose time." });
        if (zone.IsAmbiguousTime(local.DateTime))
            slotInstants = zone.GetAmbiguousTimeOffsets(local.DateTime)
                .Select(offset => new DateTimeOffset(local.DateTime, offset).ToUniversalTime()).Order().ToArray();
    }
    var outsideActiveDates = context.Database.SqlQueryRaw<int>(
        "select 1 as \"Value\" from medication_safety_profiles where medication_id={0} and organization_id={1} and branch_id={2} and (start_date>{3} or end_date<{3}) limit 1",
        request.MedicationId, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId, request.ScheduledAt).Any();
    if (outsideActiveDates) return Results.Conflict(new { message = "The scheduled dose is outside the prescribed active dates." });
    var orderSnapshot = context.Database.SqlQueryRaw<string>("""
        select json_build_object(
          'medicationName',m."Name",'dosage',m."Dosage",'route',m."Route",'schedule',m."Schedule",
          'isPrn',m."IsPrn",'form',coalesce(p.form,''),'strength',coalesce(p.strength,''),
          'prnIndication',coalesce(p.prn_indication,''),'doseWindowMinutes',coalesce(p.dose_window_minutes,60),
          'requiresWitness',coalesce(p.requires_witness,false),'indication',coalesce(p.indication,''),
          'doseUnit',coalesce(p.dose_unit,''),'frequency',coalesce(p.frequency,''),
          'administrationInstructions',coalesce(p.administration_instructions,''),
          'scheduledLocalTimes',coalesce(p.scheduled_local_times,'[]'::jsonb)
        )::text as "Value"
        from "Medications" m left join medication_safety_profiles p
          on p.medication_id=m."Id" and p.organization_id=m."OrganizationId" and p.branch_id=m."BranchId"
        where m."Id"={0} and m."OrganizationId"={1} and m."BranchId"={2}
        """, request.MedicationId, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId).Single();
    if (context.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true)
    {
        var lockKey = $"emar-slot:{tenant.OrganizationId}:{request.MedicationId}:{slotInstants[0].UtcTicks}";
        await context.Database.ExecuteSqlInterpolatedAsync($"select pg_advisory_xact_lock(hashtextextended({lockKey},0))", cancellationToken);
        await context.Database.ExecuteSqlInterpolatedAsync($"select 1 from medication_safety_profiles where medication_id={request.MedicationId} and organization_id={tenant.OrganizationId} and branch_id={tenant.BranchId ?? TenantDefaults.BranchId} for share", cancellationToken);
        var lockedVersion = context.Database.SqlQueryRaw<int?>(
            "select profile_version as \"Value\" from medication_safety_profiles where medication_id={0} and organization_id={1} and branch_id={2} limit 1",
            request.MedicationId, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId).SingleOrDefault();
        if (lockedVersion != profileVersion)
            return Results.Conflict(new { message = "The medication instruction changed while scheduling. Reload the current order." });
    }
    var sameTime = await context.MedicationAdministrationRecords.AsNoTracking().Where(item =>
        item.MedicationId == request.MedicationId && slotInstants.Contains(item.ScheduledAt) &&
        item.OrganizationId == tenant.OrganizationId && item.BranchId == (tenant.BranchId ?? TenantDefaults.BranchId)).ToListAsync(cancellationToken);
    var existing = sameTime.FirstOrDefault(item => item.MedicationProfileVersion == profileVersion);
    if (existing is not null)
        return existing.ScheduledAt == request.ScheduledAt && existing.VisitId == request.VisitId && existing.CareWorkerId == request.CareWorkerId
            ? Results.Ok(existing) : Results.Conflict(new { message = "This medication already has a MAR dose at the selected time. Review the existing assignment." });
    if (sameTime.Any(item => item.Outcome is "Scheduled" or "Administered"))
        return Results.Conflict(new { message = "An earlier order already has a pending or administered dose at this time. Review its history and close any pending dose with an appropriate non-administration outcome before rescheduling." });
    var record = new MedicationAdministrationRecord(Guid.NewGuid(), request.MedicationId, request.VisitId, request.CareWorkerId, request.ScheduledAt, null, "Scheduled", request.Notes, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId, profileVersion, orderSnapshot);
    context.MedicationAdministrationRecords.Add(record);
    context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "emar.scheduled", currentUser.UserName, nameof(MedicationAdministrationRecord), record.Id, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
    context.SaveChanges();
    await scheduleTransaction.CommitAsync(cancellationToken);
    return Results.Created($"/api/phase1/mar/{record.Id}", record);
}));
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
phase1.MapPost("/payroll-runs/generate", (ICurrentUserContext currentUser) =>
{
    var denied = RequireAnyRole(currentUser, UserRole.Administrator, UserRole.BackOffice);
    return denied ?? Results.Conflict(new { message = "Generate payroll from an approved, locked timesheet period." });
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
phase1.MapPost("/admin/users", (CreateAdminUserRequest request, ICareRepository repository, ICurrentUserContext currentUser, CareDbContext context, ITenantContext tenant, IConfiguration configuration, IWebHostEnvironment environment) =>
{
    var denied = RequireAdministrator(currentUser);
    if (denied is not null) return denied;

    if (request.Role == UserRole.BackOffice)
        return Results.BadRequest(new { message = "Platform BackOffice access cannot be granted by an organisation administrator." });

    if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing") && !configuration.GetValue<bool>("AccountInvitations:AllowLegacyPasswordProvisioning"))
    {
        return Results.Problem("Direct password provisioning is disabled. Use the account invitation endpoint.", statusCode: StatusCodes.Status410Gone);
    }

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
    if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length is < 10 or > 500)
        return Results.BadRequest(new { message = "A reason of 10 to 500 characters is required for role changes." });
    if (!Enum.IsDefined(request.Role))
        return Results.BadRequest(new { message = "Select a valid role." });
    if (request.Role == UserRole.BackOffice)
        return Results.BadRequest(new { message = "Platform BackOffice access cannot be granted by an organisation administrator." });

    try
    {
        var user = repository.UpdateUserRole(id, request.Role, request.Reason.Trim());
        return user is null ? Results.NotFound() : Results.Ok(user);
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { message = exception.Message });
    }
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

static bool InvalidContact(ProfileContactRequest? contact)
{
    if (contact is null) return false;
    var email = contact.Email?.Trim() ?? "";
    return email.Length > 254 || (email.Length > 0 && !LooksLikeEmail(email))
        || (contact.MobilePhone?.Length ?? 0) > 40
        || (contact.Address?.Length ?? 0) > 500
        || (contact.Postcode?.Length ?? 0) > 16
        || (contact.EmergencyContactName?.Length ?? 0) > 200
        || (contact.EmergencyContactRelationship?.Length ?? 0) > 100
        || (contact.EmergencyContactPhone?.Length ?? 0) > 40
        || contact.PreferredContactMethod is not (null or "" or "Phone" or "Email" or "Post");
}

static bool ContactEmailInUse(CareDbContext context, ITenantContext tenant, ProfileContactRequest? contact, string subjectType, Guid? subjectId = null)
{
    var email = contact?.Email?.Trim().ToLowerInvariant();
    if (string.IsNullOrEmpty(email)) return false;
    var profiles = context.ProfileContactDetails.AsNoTracking()
        .Where(profile => profile.OrganizationId == tenant.OrganizationId && profile.Email.ToLower() == email);
    if (subjectId is Guid existingId)
        profiles = profiles.Where(profile => profile.SubjectType != subjectType || profile.SubjectId != existingId);
    return profiles.Any();
}

static bool HasRespiteConflict(CareDbContext context, ITenantContext tenant, Guid serviceUserId, DateTimeOffset startsAt, int durationMinutes)
{
    if (!context.Database.IsNpgsql() || durationMinutes <= 0) return false;
    var endsAt = startsAt.AddMinutes(durationMinutes);
    return context.Database.SqlQuery<bool>($"""
        select exists(select 1 from client_respite_episodes
        where organization_id={tenant.OrganizationId} and service_user_id={serviceUserId}
          and status='Scheduled' and starts_at<{endsAt} and ends_at>{startsAt}) as "Value"
        """).Single();
}

static IResult Error(string message, int statusCode = StatusCodes.Status400BadRequest) =>
    Results.Json(new { message }, statusCode: statusCode);

static async Task<(IResult? Error, MedicationTerminologyConcept? Concept)> ValidateMedicationTerminologyAsync(
    CreateMedicationRequest request, IMedicationTerminologyService terminology, CancellationToken cancellationToken)
{
    const string dmdSystem = "https://dmd.nhs.uk";
    var code = request.DmdCode?.Trim();
    var display = request.DmdDisplay?.Trim();
    var system = request.DmdSystem?.Trim();
    var supplied = new[] { code, display, system }.Count(value => !string.IsNullOrWhiteSpace(value));
    if (supplied == 0) return (null, null);
    if (supplied != 3) return (Error("dm+d code, display, and system must be supplied together."), null);
    if (!string.Equals(system, dmdSystem, StringComparison.OrdinalIgnoreCase))
        return (Error("The medication terminology system must be https://dmd.nhs.uk."), null);
    try
    {
        var concept = await terminology.LookupAsync(code!, cancellationToken);
        if (concept is null) return (Error("The selected dm+d medication code was not found."), null);
        if (concept.Inactive) return (Error("The selected dm+d medication is inactive and cannot be used."), null);
        if (!string.Equals(concept.System, dmdSystem, StringComparison.OrdinalIgnoreCase))
            return (Error("The terminology server returned an unexpected code system.", StatusCodes.Status503ServiceUnavailable), null);
        if (!string.Equals(concept.Display.Trim(), display, StringComparison.OrdinalIgnoreCase))
            return (Error("The submitted medication name does not match the selected dm+d code."), null);
        return (null, concept with { Code = concept.Code.Trim(), Display = concept.Display.Trim(), System = dmdSystem });
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return (Error("NHS dm+d validation timed out. No medication changes were saved.", StatusCodes.Status503ServiceUnavailable), null);
    }
    catch (HttpRequestException)
    {
        return (Error("NHS dm+d validation is temporarily unavailable. No medication changes were saved.", StatusCodes.Status503ServiceUnavailable), null);
    }
}

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

static IResult? RequireDocumentRole(ICurrentUserContext currentUser, bool allowCareWorker) =>
    allowCareWorker
        ? RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareManager, UserRole.CareCoordinator, UserRole.CareWorker)
        : RequireAnyRole(currentUser, UserRole.Administrator, UserRole.CareManager, UserRole.CareCoordinator);

static bool CanAccessDocument(DocumentItem document, CareDbContext context, ITenantContext tenant, ICurrentUserContext currentUser)
{
    if (!tenant.CanAccess(document.OrganizationId, document.BranchId)) return false;
    if (!currentUser.IsCareWorker) return true;
    return currentUser.CareWorkerId is Guid workerId &&
        DocumentPersonIdsForWorker(context, tenant, workerId).Contains(document.ServiceUserId);
}

static HashSet<Guid> DocumentPersonIdsForWorker(CareDbContext context, ITenantContext tenant, Guid workerId)
{
    var people = context.Visits.AsNoTracking()
        .Where(visit => visit.OrganizationId == tenant.OrganizationId && visit.CareWorkerId == workerId &&
            (tenant.IsOrganizationWide || tenant.BranchId == null || visit.BranchId == tenant.BranchId))
        .Select(visit => visit.ServiceUserId)
        .ToHashSet();
    if (!context.Database.IsNpgsql()) return people;
    var additional = tenant.IsOrganizationWide || tenant.BranchId is not Guid branchId
        ? context.Database.SqlQuery<Guid>($"""
            select distinct v."ServiceUserId" as "Value"
            from "Visits" v
            join visit_care_worker_assignments a on a.visit_id=v."Id"
            where v."OrganizationId"={tenant.OrganizationId} and a.organization_id={tenant.OrganizationId}
              and a.care_worker_id={workerId}
            """).ToList()
        : context.Database.SqlQuery<Guid>($"""
            select distinct v."ServiceUserId" as "Value"
            from "Visits" v
            join visit_care_worker_assignments a on a.visit_id=v."Id"
            where v."OrganizationId"={tenant.OrganizationId} and a.organization_id={tenant.OrganizationId}
              and a.care_worker_id={workerId} and v."BranchId"={branchId}
            """).ToList();
    people.UnionWith(additional);
    return people;
}

static object DocumentResponse(DocumentItem document) => new
{
    document.Id,
    document.ServiceUserId,
    document.FileName,
    document.Category,
    document.UploadedBy,
    document.UploadedAt
};

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

    if (visit.CareWorkerId != currentUser.CareWorkerId)
        return Error("Care workers can only access their assigned visits.", StatusCodes.Status403Forbidden);
    return PublishedRotaAccess.IsVisibleToWorker(context, tenant, visit, currentUser.CareWorkerId.Value)
        ? null
        : Error("This visit is awaiting rota publication.", StatusCodes.Status403Forbidden);
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
    var validation = ValidateServiceUserReference(serviceUserId, context, tenant)
        ?? ValidateCareWorkerReference(careWorkerId, context, tenant);
    if (validation is not null) return validation;
    var client = context.ServiceUsers.AsNoTracking().First(item => item.Id == serviceUserId);
    return new[] { "Archived", "Discharged", "Suspended" }.Contains(client.Status, StringComparer.OrdinalIgnoreCase)
        ? Error("Visits cannot be scheduled for an archived, discharged, or suspended client.")
        : null;
}

static IResult? ValidateAppointmentBranch(Guid serviceUserId, IReadOnlyCollection<Guid> workerIds, CareDbContext context, ITenantContext tenant)
{
    var clientBranch = context.ServiceUsers.AsNoTracking().Where(x => x.Id == serviceUserId && x.OrganizationId == tenant.OrganizationId).Select(x => x.BranchId).FirstOrDefault() ?? TenantDefaults.BranchId;
    var workers = context.CareWorkers.AsNoTracking().Where(x => workerIds.Contains(x.Id) && x.OrganizationId == tenant.OrganizationId).Select(x => new { x.Id, x.BranchId }).ToList();
    return workers.Count != workerIds.Count || workers.Any(x => (x.BranchId ?? TenantDefaults.BranchId) != clientBranch)
        ? Error("The service user and all assigned care workers must belong to the same branch.")
        : null;
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

    return ValidateServiceUserReference(serviceUserId, context, tenant)
        ?? ValidateCareWorkerReference(careWorkerId, context, tenant);
}

static VisitBookingAssessment AssessVisitBooking(CreateVisitRequest request, CareDbContext context, ITenantContext tenant)
{
    var workerIds = VisitWorkerIds(request.CareWorkerId, request.AdditionalCareWorkerIds);
    var conflicts = workerIds.SelectMany(id => FindVisitConflicts(context, tenant, id, request.StartsAt, request.DurationMinutes, request.ExcludeVisitId)).ToList();
    var availabilityConflicts = workerIds.SelectMany(id => FindWorkerAvailabilityConflicts(context, tenant, id, request.StartsAt, request.DurationMinutes)).ToList();
    var safetyConflicts = workerIds.SelectMany(id => FindWorkerSafetyConflicts(context, tenant, id, request.StartsAt, request.RequiredSkills)).ToList();
    var operationalConflicts = workerIds.SelectMany(id => FindWorkerOperationalConflicts(context, tenant, id, request.ServiceUserId, request.StartsAt, request.DurationMinutes, request.ExcludeVisitId)).ToList();
    var branch = context.ServiceUsers.AsNoTracking().Where(x => x.Id == request.ServiceUserId && x.OrganizationId == tenant.OrganizationId).Select(x => x.BranchId).FirstOrDefault() ?? TenantDefaults.BranchId;
    var policy = workerIds.Select(id => AppointmentWorkflow.Check(context, tenant, branch, id, request.StartsAt, request.DurationMinutes, request.ExcludeVisitId)).ToArray();
    var bookingBlocks = policy.SelectMany(x => x.Blocks)
        .Concat(HasRespiteConflict(context, tenant, request.ServiceUserId, request.StartsAt, request.DurationMinutes) ? ["Client has a respite episode during this visit."] : Array.Empty<string>())
        .Distinct().ToArray();
    var bookingWarnings = policy.SelectMany(x => x.Warnings).Distinct().ToArray();
    return new VisitBookingAssessment(conflicts, availabilityConflicts, safetyConflicts, operationalConflicts, bookingBlocks, bookingWarnings);
}

static string VisitBookingError(VisitBookingAssessment assessment)
{
    if (assessment.BookingBlocks.Length > 0) return string.Join(" ", assessment.BookingBlocks);
    if (assessment.Conflicts.Count > 0) return "Care worker already has a conflicting visit.";
    if (assessment.AvailabilityConflicts.Count > 0) return "Care worker is unavailable for the requested visit time. Check structured availability rules.";
    if (assessment.SafetyConflicts.Count > 0) return "Care worker does not meet the visit's current compliance or skill requirements.";
    return "One or more assigned workers fail availability, compliance, working-time, absence, or travel requirements.";
}

static List<object> FindVisitConflicts(CareDbContext context, ITenantContext tenant, Guid careWorkerId, DateTimeOffset startsAt, int durationMinutes, Guid? excludeVisitId = null)
{
    var endsAt = startsAt.AddMinutes(durationMinutes);
    var additionalVisitIds = ReadAdditionalVisitIds(context, tenant, careWorkerId);
    return context.Visits.AsNoTracking()
        .Where(visit => visit.Status != VisitStatus.Cancelled && (visit.CareWorkerId == careWorkerId || additionalVisitIds.Contains(visit.Id)) && (excludeVisitId == null || visit.Id != excludeVisitId))
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
    var visits=context.Visits.AsNoTracking().AsEnumerable().Where(v=>v.Status!=VisitStatus.Cancelled&&TenantVisible(tenant,v.OrganizationId,v.BranchId)&&(v.CareWorkerId==workerId||additional.Contains(v.Id))&&(excludeVisitId is null||v.Id!=excludeVisitId)).ToList();
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
    var branch = context.Visits.AsNoTracking().Where(x => x.Id == visitId && x.OrganizationId == tenant.OrganizationId).Select(x => x.BranchId).FirstOrDefault() ?? tenant.BranchId ?? TenantDefaults.BranchId;
    var connection=context.Database.GetDbConnection();var opened=connection.State!=System.Data.ConnectionState.Open;
    try{if(opened)connection.Open();using(var delete=connection.CreateCommand()){delete.CommandText="delete from visit_care_worker_assignments where visit_id=@visit and organization_id=@organization";AddParameter(delete,"visit",visitId);AddParameter(delete,"organization",tenant.OrganizationId);delete.ExecuteNonQuery();}foreach(var workerId in VisitWorkerIds(primaryWorkerId,additionalWorkerIds).Where(id=>id!=primaryWorkerId)){using var insert=connection.CreateCommand();insert.CommandText="insert into visit_care_worker_assignments(visit_id,care_worker_id,organization_id,branch_id,assignment_role) values(@visit,@worker,@organization,@branch,'Additional')";AddParameter(insert,"visit",visitId);AddParameter(insert,"worker",workerId);AddParameter(insert,"organization",tenant.OrganizationId);AddParameter(insert,"branch",branch);insert.ExecuteNonQuery();}}
    catch(System.Data.Common.DbException){if((additionalWorkerIds?.Count??0)>0)throw;}
    finally{if(opened&&connection.State==System.Data.ConnectionState.Open)connection.Close();}
}

static bool ValidVisitCancellationReason(CareDbContext context, ITenantContext tenant, string? reason)
{
    if (!context.Database.IsNpgsql()) return !string.IsNullOrWhiteSpace(reason);
    var activeReasons = context.Database.SqlQuery<string>($"""select value as "Value" from care_operation_options where organization_id={tenant.OrganizationId} and kind='CancellationReason' and active=true""").ToArray();
    if (activeReasons.Length == 0) return !string.IsNullOrWhiteSpace(reason);
    return activeReasons.Contains(reason?.Trim(), StringComparer.OrdinalIgnoreCase);
}

static void RecordScheduleChange(CareDbContext context, ITenantContext tenant, ICurrentUserContext user, Guid visitId, string changeType, object? oldValue, object? newValue, string? reason)
{
    var branch = context.Visits.AsNoTracking().Where(x => x.Id == visitId && x.OrganizationId == tenant.OrganizationId).Select(x => x.BranchId).FirstOrDefault() ?? tenant.BranchId ?? TenantDefaults.BranchId;
    var connection=context.Database.GetDbConnection();var opened=connection.State!=System.Data.ConnectionState.Open;
    try{if(opened)connection.Open();using var command=connection.CreateCommand();command.CommandText="insert into schedule_change_history(id,visit_id,organization_id,branch_id,change_type,old_values_json,new_values_json,reason,changed_by) values(@id,@visit,@organization,@branch,@type,@old,@new,@reason,@actor)";AddParameter(command,"id",Guid.NewGuid());AddParameter(command,"visit",visitId);AddParameter(command,"organization",tenant.OrganizationId);AddParameter(command,"branch",branch);AddParameter(command,"type",changeType);AddParameter(command,"old",System.Text.Json.JsonSerializer.Serialize(oldValue));AddParameter(command,"new",System.Text.Json.JsonSerializer.Serialize(newValue));AddParameter(command,"reason",string.IsNullOrWhiteSpace(reason)?changeType:reason.Trim());AddParameter(command,"actor",user.UserName);command.ExecuteNonQuery();}
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
    var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    var local = TimeZoneInfo.ConvertTime(startsAt, zone);
    var firstDate = DateOnly.FromDateTime(local.DateTime);
    var time = TimeOnly.FromDateTime(local.DateTime);
    var dayStep = frequency.Trim().ToLowerInvariant() switch
    {
        "daily" => 1, "weekly" => 7, "fortnightly" => 14,
        _ => throw new ArgumentException("Frequency must be Daily, Weekly, or Fortnightly.")
    };
    for (var index = 0; index < occurrences; index++)
    {
        var day = firstDate.AddDays(index * dayStep);
        if (!TryUkStart(day, time, out var occurrence))
            throw new ArgumentException($"{day:yyyy-MM-dd} at {time:HH:mm} does not exist in UK time because the clocks change.");
        yield return occurrence;
    }
}

static bool TryUkStart(DateOnly date, TimeOnly time, out DateTimeOffset startsAt)
{
    var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
    startsAt = default;
    if (zone.IsInvalidTime(local)) return false;
    var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
    startsAt = new DateTimeOffset(local, offset).ToUniversalTime();
    return true;
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

    if (visit.Status == VisitStatus.Cancelled)
    {
        return Results.Conflict(new { message = "A cancelled visit cannot be used to schedule a MAR dose." });
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
    var emarBranchId = tenant.BranchId ?? (tenant.IsOrganizationWide ? TenantDefaults.BranchId : Guid.Empty);
    if (!EmarPilotGate.IsEnabledForBranch(configuration, emarBranchId)) return Results.Json(new { message = "eMAR administration is disabled for this branch until the medication clinical-safety gate is approved." }, statusCode: StatusCodes.Status423Locked);
    var record = context.MedicationAdministrationRecords.Find(id);
    if (record is null || !tenant.CanAccess(record.OrganizationId, record.BranchId)) return Results.NotFound();

    var denied = RequireAssignedVisitForCareWorker(record.VisitId, context, tenant, currentUser);
    if (denied is not null) return denied;

    return Results.Json(new { message = "This legacy MAR endpoint cannot record final outcomes. Use the governed eMAR record endpoint." }, statusCode: StatusCodes.Status410Gone);

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

static async Task DeleteUploadedDocumentAsync(string storagePath, IConfiguration configuration, IHttpClientFactory httpClientFactory, CancellationToken cancellationToken)
{
    if (!storagePath.StartsWith("supabase://", StringComparison.OrdinalIgnoreCase))
    {
        if (File.Exists(storagePath)) File.Delete(storagePath);
        return;
    }

    var supabaseUrl = configuration["Supabase:Url"];
    var serviceRoleKey = configuration["Supabase:ServiceRoleKey"];
    if (string.IsNullOrWhiteSpace(supabaseUrl) || string.IsNullOrWhiteSpace(serviceRoleKey)) return;
    var (bucket, objectKey) = ParseSupabaseStoragePath(storagePath);
    var deleteUrl = $"{supabaseUrl.TrimEnd('/')}/storage/v1/object/{Uri.EscapeDataString(bucket)}/{Uri.EscapeDataString(objectKey).Replace("%2F", "/", StringComparison.Ordinal)}";
    using var delete = new HttpRequestMessage(HttpMethod.Delete, deleteUrl);
    delete.Headers.TryAddWithoutValidation("apikey", serviceRoleKey);
    delete.Headers.TryAddWithoutValidation("Authorization", $"Bearer {serviceRoleKey}");
    using var response = await httpClientFactory.CreateClient().SendAsync(delete, cancellationToken);
    if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
        throw new InvalidOperationException("Document metadata could not be saved and the uploaded object could not be cleaned up.");
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
public sealed record OpenVisitCoverRequest(string Reason);
public sealed record OfferVisitCoverRequest(Guid CareWorkerId);
public sealed record CreateUnassignedVisitRequest(Guid ServiceUserId, DateTimeOffset StartsAt, string VisitType, int DurationMinutes, string RequiredSkills = "", string Reason = "");
public sealed record PublishRotaRequest(Guid BranchId, DateOnly WeekStart);
public sealed record MoveVisitRequest(DateTimeOffset ExpectedStartsAt, DateTimeOffset StartsAt, Guid? CareWorkerId = null);
public sealed record RecurringSeriesState(Guid Id, Guid ServiceUserId, Guid BranchId);
public sealed record RecurringSeriesSummary(Guid Id, Guid ServiceUserId, string ServiceUserName, Guid BranchId, string Frequency, DateTimeOffset NextStartsAt, Guid CareWorkerId, string VisitType, int DurationMinutes, string RequiredSkills, int FutureCount);
public sealed record EditRecurringSeriesRequest(Guid CareWorkerId, string VisitType, int DurationMinutes, string RequiredSkills, string ChangeReason, IReadOnlyCollection<Guid>? AdditionalCareWorkerIds = null, string? StartTime = null);
public sealed record RotaPublicationSummary(Guid Id, Guid BranchId, DateOnly WeekStart, int Revision, DateTimeOffset PublishedAt, string PublishedBy, int VisitCount, int WorkerCount, int AcknowledgedCount, bool NeedsRepublish);
public sealed record WorkerRotaPublication(Guid Id, DateOnly WeekStart, int Revision, DateTimeOffset PublishedAt, int VisitCount, bool Acknowledged);
public sealed record AssignUnassignedVisitRequest(Guid CareWorkerId);
public sealed record CancelUnassignedVisitRequest(string Reason);
public sealed record UnassignedVisitQueueItem(Guid Id, Guid ServiceUserId, string ServiceUserName, Guid BranchId, DateTimeOffset StartsAt, string VisitType, int DurationMinutes, string RequiredSkills, string Reason);
public sealed record UnassignedVisitState(Guid Id, Guid ServiceUserId, Guid BranchId, DateTimeOffset StartsAt, string VisitType, int DurationMinutes, string RequiredSkills, string Status);
public sealed record CloseVisitCoverRequest(string Reason);
public sealed record DeclineVisitCoverRequest(string? Reason);
public sealed record VisitCoverState(Guid Id, Guid VisitId, Guid BranchId, string Status);
public sealed record VisitCoverOfferState(Guid Id, Guid CoverRequestId, Guid VisitId, Guid BranchId, Guid CareWorkerId, string OfferStatus, string RequestStatus);
public sealed record VisitCoverQueueItem(Guid Id, Guid VisitId, Guid BranchId, string Reason, string Status, Guid ServiceUserId, string ServiceUserName, Guid CurrentWorkerId, string CurrentWorkerName, DateTimeOffset StartsAt, int DurationMinutes, string VisitType, int PendingOffers);
public sealed record VisitCoverOfferItem(Guid Id, Guid CoverRequestId, Guid VisitId, string ServiceUserName, DateTimeOffset StartsAt, int DurationMinutes, string VisitType, string Reason, string Status);
public sealed record VisitBookingAssessment(List<object> Conflicts, List<object> AvailabilityConflicts, List<object> SafetyConflicts, List<object> OperationalConflicts, string[] BookingBlocks, string[] BookingWarnings)
{
    public bool HasConflicts => Conflicts.Count > 0 || AvailabilityConflicts.Count > 0 || SafetyConflicts.Count > 0 || OperationalConflicts.Count > 0 || BookingBlocks.Length > 0;
}
public sealed record CreateMedicationRequest(Guid ServiceUserId, string Name, string Dosage, string Route, string Schedule, bool IsPrn, string Pharmacy, string AllergyWarning, string? DmdCode = null, string? DmdDisplay = null, string? DmdSystem = null);
public sealed record CreateMedicationAdministrationRecordRequest(Guid MedicationId, Guid VisitId, Guid CareWorkerId, DateTimeOffset ScheduledAt, string Notes);
public sealed record CompleteMedicationAdministrationRequest(DateTimeOffset? AdministeredAt, string Notes);
public sealed record CreateOrganizationRequest(string Name, string Plan);
public sealed record CreateBranchRequest(string Name, string Region, Guid? OrganizationId);
public sealed record FamilyPreferencesRequest(bool EmailNotifications, bool SmsNotifications, bool MonthlyDigest, bool IncidentAlerts);
public sealed record RecordPaymentRequest(decimal Amount, string Reference);
public sealed record RejectFinancialRequest(string Reason);

public partial class Program;
