using AiCare.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiCare.Infrastructure;

public sealed class RenderTestPatientSeeder(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<RenderTestPatientSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsProduction() || !configuration.GetValue<bool>("TestingData:Enabled"))
        {
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();

            var adminTenant = await db.AppUsers.AsNoTracking()
                .Where(user => user.IsActive && user.Role == UserRole.Administrator)
                .OrderBy(user => user.UserName)
                .Select(user => new { user.OrganizationId, user.BranchId })
                .FirstOrDefaultAsync(cancellationToken);

            var organizationId = adminTenant?.OrganizationId ?? TenantDefaults.OrganizationId;
            var branchId = adminTenant?.BranchId ?? TenantDefaults.BranchId;
            var patients = BuildPatients(organizationId, branchId);
            var patientIds = patients.Select(patient => patient.Id).ToArray();
            var existingIds = await db.ServiceUsers.AsNoTracking()
                .Where(patient => patientIds.Contains(patient.Id))
                .Select(patient => patient.Id)
                .ToArrayAsync(cancellationToken);
            var existing = existingIds.ToHashSet();
            var missing = patients.Where(patient => !existing.Contains(patient.Id)).ToArray();

            if (missing.Length == 0)
            {
                logger.LogInformation("Render test patient seed already present: {PatientCount} deterministic records.", patients.Length);
                return;
            }

            db.ServiceUsers.AddRange(missing);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Seeded {InsertedCount} Render test patients; {TotalCount} deterministic test patients are now available for tenant {OrganizationId}.",
                missing.Length,
                patients.Length,
                organizationId);
        }
        catch (Exception exception)
        {
            // Test-data seeding must not take the API offline. The failure is still visible in Render logs.
            logger.LogError(exception, "Unable to seed Render test patients.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static ServiceUser[] BuildPatients(Guid organizationId, Guid branchId) =>
    [
        Patient("90000000-0000-0000-0000-000000000001", "Amina Rahman", new DateOnly(1942, 4, 18), "+44 7700 900101", "Personal care, medication prompts and falls prevention", "Test Contact · +44 7700 901101", "Sara Malik", RiskLevel.High, "Falls review due", "1 Test Lane, London", "Penicillin", "Hypertension; osteoarthritis", "Local Authority", "Female", "Uses walking frame", "Mild short-term memory impairment", "Speak slowly and confirm understanding", "Halal meals and family involvement", "Halal; soft diet", organizationId, branchId),
        Patient("90000000-0000-0000-0000-000000000002", "George Miller", new DateOnly(1938, 11, 2), "+44 7700 900102", "Morning personal care and mobility support", "Test Contact · +44 7700 901102", "Omar Shah", RiskLevel.Medium, "Stable", "2 Test Lane, London", "None known", "Type 2 diabetes", "Private", "Male", "Walks with stick", "No known impairment", "Wears hearing aids", "Prefers quiet morning visits", "Diabetic diet", organizationId, branchId),
        Patient("90000000-0000-0000-0000-000000000003", "Maryam Noor", new DateOnly(1951, 7, 9), "+44 7700 900103", "Medication support, hydration and blood-pressure observations", "Test Contact · +44 7700 901103", "Sara Malik", RiskLevel.Medium, "Care note review", "3 Test Lane, London", "Sulfonamides", "Hypertension", "NHS Continuing Healthcare", "Female", "Independent indoors", "No known impairment", "Prefers Urdu or simple English", "Family should be included in major care changes", "Low-salt halal diet", organizationId, branchId),
        Patient("90000000-0000-0000-0000-000000000004", "David Thompson", new DateOnly(1946, 1, 27), "+44 7700 900104", "Post-discharge reablement and wound observation", "Test Contact · +44 7700 901104", "Omar Shah", RiskLevel.High, "Reablement active", "4 Test Lane, London", "Latex", "Post-operative hip replacement", "NHS Discharge to Assess", "Male", "Requires supervised transfers", "Occasional post-operative confusion", "Use written prompts for exercises", "Values independence and predictable routines", "High-protein diet", organizationId, branchId),
        Patient("90000000-0000-0000-0000-000000000005", "Fatima Ali", new DateOnly(1935, 9, 14), "+44 7700 900105", "Dementia support, meals, hydration and companionship", "Test Contact · +44 7700 901105", "Nadia Rehman", RiskLevel.High, "Enhanced monitoring", "5 Test Lane, London", "None known", "Alzheimer's disease", "Local Authority", "Female", "Needs standby assistance", "Moderate dementia", "One instruction at a time; visual prompts help", "Female carers preferred for personal care", "Halal; finger foods when unsettled", organizationId, branchId),
        Patient("90000000-0000-0000-0000-000000000006", "Robert Evans", new DateOnly(1955, 5, 30), "+44 7700 900106", "Medication prompts and support with shopping and meals", "Test Contact · +44 7700 901106", "Sara Malik", RiskLevel.Low, "Stable", "6 Test Lane, London", "None known", "COPD", "Private", "Male", "Independent", "No known impairment", "Allow extra time when breathless", "Enjoys local football and community activities", "Regular diet", organizationId, branchId),
        Patient("90000000-0000-0000-0000-000000000007", "Zainab Hussain", new DateOnly(1949, 12, 6), "+44 7700 900107", "Personal care, continence support and skin integrity checks", "Test Contact · +44 7700 901107", "Nadia Rehman", RiskLevel.Medium, "Skin review due", "7 Test Lane, London", "Adhesive dressings", "Chronic venous insufficiency", "Local Authority", "Female", "Uses rollator", "No known impairment", "Prefers face-to-face explanations", "Privacy and dignity are especially important", "Halal; fluid encouragement", organizationId, branchId),
        Patient("90000000-0000-0000-0000-000000000008", "Arthur Wilson", new DateOnly(1940, 3, 22), "+44 7700 900108", "Parkinson's support, medication timing and safe transfers", "Test Contact · +44 7700 901108", "Omar Shah", RiskLevel.High, "Medication timing priority", "8 Test Lane, London", "None known", "Parkinson's disease", "NHS Continuing Healthcare", "Male", "Frame indoors; wheelchair outdoors", "Cognition fluctuates when tired", "Allow time to respond; speech can be quiet", "Likes visits at consistent times", "Easy-to-chew diet", organizationId, branchId),
        Patient("90000000-0000-0000-0000-000000000009", "Susan Clarke", new DateOnly(1958, 8, 11), "+44 7700 900109", "Multiple-sclerosis support, personal care and fatigue management", "Test Contact · +44 7700 901109", "Nadia Rehman", RiskLevel.Medium, "Stable", "9 Test Lane, London", "Codeine", "Multiple sclerosis", "Direct Payment", "Female", "Wheelchair user", "No known impairment", "Prefers written visit plan", "Values choice and control over visit sequence", "Vegetarian", organizationId, branchId),
        Patient("90000000-0000-0000-0000-000000000010", "Ibrahim Khan", new DateOnly(1944, 6, 3), "+44 7700 900110", "Diabetes support, foot checks and meal preparation", "Test Contact · +44 7700 901110", "Sara Malik", RiskLevel.Medium, "Foot check scheduled", "10 Test Lane, London", "None known", "Type 2 diabetes; peripheral neuropathy", "Local Authority", "Male", "Independent with stick outdoors", "No known impairment", "Prefers clear written medication prompts", "Prayer times should be respected", "Halal diabetic diet", organizationId, branchId),
        Patient("90000000-0000-0000-0000-000000000011", "Margaret Green", new DateOnly(1932, 2, 17), "+44 7700 900111", "Palliative comfort care, repositioning and family support", "Test Contact · +44 7700 901111", "Nadia Rehman", RiskLevel.High, "Palliative plan active", "11 Test Lane, London", "Morphine intolerance", "Advanced heart failure", "NHS Continuing Healthcare", "Female", "Bed-to-chair with two-person support", "Alert with periods of fatigue", "Short conversations; check comfort frequently", "Family presence is important", "Small frequent meals", organizationId, branchId),
        Patient("90000000-0000-0000-0000-000000000012", "Thomas Reed", new DateOnly(1960, 10, 25), "+44 7700 900112", "Stroke rehabilitation, communication support and personal care", "Test Contact · +44 7700 901112", "Omar Shah", RiskLevel.Medium, "Reablement active", "12 Test Lane, London", "None known", "Previous stroke with right-sided weakness", "NHS Discharge to Assess", "Male", "Quad cane; supervision on stairs", "No known cognitive impairment", "Expressive aphasia; use yes/no questions when helpful", "Prefers to attempt tasks independently first", "Regular diet; fluids monitored", organizationId, branchId)
    ];

    private static ServiceUser Patient(
        string id,
        string fullName,
        DateOnly dateOfBirth,
        string phoneNumber,
        string careNeeds,
        string emergencyContact,
        string preferredCareWorker,
        RiskLevel risk,
        string status,
        string address,
        string allergies,
        string medicalConditions,
        string fundingSource,
        string gender,
        string mobilityStatus,
        string cognitiveStatus,
        string communicationNeeds,
        string culturalPreferences,
        string dietaryRequirements,
        Guid organizationId,
        Guid branchId) =>
        new(
            Guid.Parse(id),
            fullName,
            dateOfBirth,
            phoneNumber,
            careNeeds,
            emergencyContact,
            preferredCareWorker,
            risk,
            status,
            address,
            allergies,
            medicalConditions,
            fundingSource,
            gender,
            string.Empty,
            mobilityStatus,
            cognitiveStatus,
            communicationNeeds,
            culturalPreferences,
            dietaryRequirements,
            organizationId,
            branchId);
}
