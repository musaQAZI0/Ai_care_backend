using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed class OperationalReportsRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task AdministratorCanRunSaveAndExportAFilteredReport()
    {
        await factory.EnsureClinicalSeedAsync();
        var workerId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var workerName = $"Report Worker {workerId:N}";
        var clientName = $"Report Client {clientId:N}";
        var visitId = Guid.NewGuid();
        var completedCourseId = Guid.NewGuid();
        var plannedCourseId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var activityAction = $"report.regression.{Guid.NewGuid():N}";
        var workerAccountName = $"report.worker.{workerId:N}";
        var coordinatorName = $"report.coordinator.{Guid.NewGuid():N}";
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var starts = new DateTimeOffset(today.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.CareWorkers.Add(new CareWorker(workerId, workerName, "Care", "Flexible", 0, 0, "Valid", "Compliant", "Local", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.AppUsers.Add(new AppUser(Guid.NewGuid(), workerAccountName, $"{workerAccountName}@aicare.local", PasswordHasher.HashPassword("Admin123!"), UserRole.CareWorker, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId, workerId, null));
            db.AppUsers.Add(new AppUser(Guid.NewGuid(), coordinatorName, $"{coordinatorName}@aicare.local", PasswordHasher.HashPassword("Admin123!"), UserRole.CareCoordinator, true, TenantDefaults.OrganizationId, TenantDefaults.BranchId, null, null));
            db.ServiceUsers.Add(new ServiceUser(clientId, clientName, new DateOnly(1950, 1, 1), "+10000000000", "Care", "Contact", "", RiskLevel.Low, "Active", "Address", "None", "None", "Private", "Regression", "", "Independent", "Independent", "Verbal", "None", "Standard", TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.Visits.Add(new Visit(visitId, clientId, workerId, starts, "Personal care", 45, "Care", VisitStatus.Completed, null, null, null, null, null, null, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.Invoices.Add(new Invoice(invoiceId, clientId, "Private", 72m, "Generated", starts, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), activityAction, "report-filter-actor", "ReportFixture", visitId, DateTimeOffset.UtcNow, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""insert into finance_invoice_details(invoice_id,organization_id,branch_id,invoice_number,invoice_date,due_date,payment_terms,provider_name,customer_name,net_amount,vat_rate,vat_amount,gross_amount) values({invoiceId},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},{"REPORT-" + invoiceId.ToString("N")},{today},{today.AddDays(14)},'14 days','AiCare', {clientName},60.00,20.00,12.00,72.00)""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""insert into finance_invoice_lines(id,invoice_id,service_user_id,organization_id,branch_id,description,quantity,unit_rate,amount) values({Guid.NewGuid()},{invoiceId},{clientId},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},'Report care service',1.00,60.00,60.00)""");
            var diaryId = Guid.NewGuid();
            var pendingTaskId = Guid.NewGuid();
            var completedTaskId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""insert into operational_diary_entries(id,organization_id,branch_id,service_user_id,care_worker_id,category,priority,status,subject,detail,event_at,created_by) values({diaryId},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},{clientId},{workerId},'Care','Routine','Open','Report diary entry','Care review completed',{starts},'regression')""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""insert into visit_tasks(id,visit_id,service_user_id,care_worker_id,organization_id,branch_id,title,status) values({pendingTaskId},{visitId},{clientId},{workerId},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},'Report pending task','Pending')""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""insert into visit_tasks(id,visit_id,service_user_id,care_worker_id,organization_id,branch_id,title,status,outcome,completed_at) values({completedTaskId},{visitId},{clientId},{workerId},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},'Report completed task','Completed','Done',{starts})""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""insert into training_catalogue_courses(id,organization_id,branch_id,code,title,is_mandatory) values({completedCourseId},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},{"DONE-" + completedCourseId.ToString("N")},'Report completed course',true),({plannedCourseId},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},{"PLAN-" + plannedCourseId.ToString("N")},'Report planned course',true)""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""insert into worker_training_bookings(id,course_id,care_worker_id,organization_id,branch_id,scheduled_at,status,completed_at,expires_at,booked_by) values({Guid.NewGuid()},{completedCourseId},{workerId},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},{starts},'Completed',{DateTimeOffset.UtcNow},{DateTimeOffset.UtcNow.AddYears(1)},'regression')""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""insert into worker_training_bookings(id,course_id,care_worker_id,organization_id,branch_id,scheduled_at,status,booked_by) values({Guid.NewGuid()},{plannedCourseId},{workerId},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},{DateTimeOffset.UtcNow.AddDays(2)},'Booked','regression')""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""insert into worker_absences(id,care_worker_id,organization_id,branch_id,absence_type,starts_at,ends_at,status,notes) values({Guid.NewGuid()},{workerId},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},'Leave',{starts},{starts.AddHours(8)},'Pending','Report leave request')""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""insert into worker_availability_rules(id,care_worker_id,organization_id,branch_id,day_of_week,start_time,end_time,is_available,effective_from,notes) values({Guid.NewGuid()},{workerId},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},{(int)today.DayOfWeek},{new TimeOnly(9, 0)},{new TimeOnly(17, 0)},true,{today},'Report availability rule')""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""insert into worker_employment_profiles(care_worker_id,organization_id,branch_id,recruitment_status,employment_status,contract_type,job_title,contracted_weekly_minutes,start_date,end_date,line_manager,status_reason) values({workerId},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},'Hired','Leaver','Permanent','Care Assistant',1800,{today.AddYears(-1)},{today},'Report Manager','Relocated for family')""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""insert into person_contacts(id,service_user_id,organization_id,branch_id,contact_type,full_name,relationship,phone_number,email,is_primary,is_emergency) values({Guid.NewGuid()},{clientId},{TenantDefaults.OrganizationId},{TenantDefaults.BranchId},'Family','Report Emergency Contact','Daughter','+10000000001','contact@example.test',true,true)""");
        }

        using var admin = factory.CreateClient();
        var login = await admin.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "Admin123!", mfaCode = (string?)null });
        login.EnsureSuccessStatusCode();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<Login>())!.Token);
        using var coordinator = factory.CreateClient();
        var coordinatorLogin = await coordinator.PostAsJsonAsync("/api/auth/login", new { userName = coordinatorName, password = "Admin123!", mfaCode = (string?)null });
        coordinatorLogin.EnsureSuccessStatusCode();
        coordinator.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await coordinatorLogin.Content.ReadFromJsonAsync<Login>())!.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await coordinator.GetAsync("/api/phase1/operational-reports/carer-contact-details")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await coordinator.GetAsync("/api/phase1/operational-reports/carer-personal-details")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await coordinator.GetAsync("/api/phase1/operational-reports/carer-reasons-for-leaving")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await coordinator.GetAsync("/api/phase1/operational-reports/client-contact-details")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await coordinator.GetAsync("/api/phase1/operational-reports/client-personal-details")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await coordinator.GetAsync("/api/phase1/operational-reports/uninvoiced-appointments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await coordinator.GetAsync("/api/phase1/operational-reports/untimesheeted-appointments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await coordinator.GetAsync("/api/phase1/operational-reports/finance-stats")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await coordinator.GetAsync("/api/phase1/operational-reports/activity-logs")).StatusCode);

        var catalogue = await admin.GetAsync("/api/phase1/operational-reports/catalogue");
        Assert.Equal(HttpStatusCode.OK, catalogue.StatusCode);
        Assert.Contains("carer-roster", await catalogue.Content.ReadAsStringAsync());

        var filters = $"from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}&workerId={workerId}&clientId={clientId}";
        var report = await admin.GetAsync($"/api/phase1/operational-reports/carer-roster?{filters}");
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        var reportJson = JsonDocument.Parse(await report.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, reportJson.GetProperty("total").GetInt32());
        Assert.Contains(workerName, reportJson.GetRawText());
        Assert.Contains(clientName, reportJson.GetRawText());

        foreach (var key in new[]
                 {
                     "client-roster", "client-schedule", "unassigned-appointments", "planned-hours",
                     "stats-by-carer", "stats-by-client", "stats-by-date", "call-care-monitoring",
                     "care-plans", "clients-medication", "medication-administration",
                     "carer-diary", "client-diary", "outstanding-checklist", "checklist-history", "checklist-history-by-date",
                     "training-matrix", "acquired-training", "planned-training", "required-training",
                     "time-off-requests", "time-off-summary", "availability", "carer-contact-details",
                     "carer-personal-details", "carer-reasons-for-leaving", "client-contact-details", "client-personal-details",
                     "uninvoiced-appointments", "untimesheeted-appointments", "export-appointment-data",
                     "finance-stats", "finance-stats-by-client"
                 })
        {
            var response = await admin.GetAsync($"/api/phase1/operational-reports/{key}?{filters}");
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{key}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            var body = await response.Content.ReadAsStringAsync();
            if (key.EndsWith("diary")) Assert.Contains("Report diary entry", body);
            if (key == "outstanding-checklist") { Assert.Contains("Report pending task", body); Assert.DoesNotContain("Report completed task", body); }
            if (key.StartsWith("checklist-history")) { Assert.Contains("Report completed task", body); Assert.DoesNotContain("Report pending task", body); }
            if (key == "training-matrix") { Assert.Contains("Report completed course", body); Assert.Contains("Report planned course", body); }
            if (key == "acquired-training") { Assert.Contains("Report completed course", body); Assert.DoesNotContain("Report planned course", body); }
            if (key == "required-training") { Assert.Contains("Report planned course", body); Assert.DoesNotContain("Report completed course", body); }
            if (key == "time-off-requests") Assert.Contains("Report leave request", body);
            if (key == "time-off-summary") { Assert.Contains("Pending", body); Assert.Contains("8", body); }
            if (key == "availability") Assert.Contains("Report availability rule", body);
            if (key == "carer-contact-details") Assert.Contains($"{workerAccountName}@aicare.local", body);
            if (key == "carer-personal-details") { Assert.Contains("Care Assistant", body); Assert.Contains("Report Manager", body); }
            if (key == "carer-reasons-for-leaving") Assert.Contains("Relocated for family", body);
            if (key == "client-contact-details") { Assert.Contains("Report Emergency Contact", body); Assert.Contains("contact@example.test", body); }
            if (key == "client-personal-details") { Assert.Contains("1950-01-01", body); Assert.Contains("Report Client", body); }
            if (key == "uninvoiced-appointments") Assert.Contains("No billable event", body);
            if (key == "untimesheeted-appointments") Assert.Contains("No timesheet entry", body);
            if (key == "export-appointment-data") Assert.Contains(visitId.ToString(), body);
            if (key == "finance-stats") { Assert.Contains("72.00", body); Assert.Contains("Generated", body); }
            if (key == "finance-stats-by-client") { Assert.Contains(clientName, body); Assert.Contains("60.00", body); }
        }
        var plannedThrough = today.AddDays(3);
        var planned = await admin.GetAsync($"/api/phase1/operational-reports/planned-training?from={today:yyyy-MM-dd}&to={plannedThrough:yyyy-MM-dd}&workerId={workerId}");
        Assert.Equal(HttpStatusCode.OK, planned.StatusCode);
        Assert.Contains("Report planned course", await planned.Content.ReadAsStringAsync());
        var allCarers = await admin.GetAsync($"/api/phase1/operational-reports/training-matrix?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, allCarers.StatusCode);
        Assert.True(JsonDocument.Parse(await allCarers.Content.ReadAsStringAsync()).RootElement.GetProperty("total").GetInt32() >= 2);
        var activityFilters = $"from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}&actor=report-filter-actor&action={activityAction}&entityType=ReportFixture";
        var activity = await admin.GetAsync($"/api/phase1/operational-reports/activity-logs?{activityFilters}");
        Assert.Equal(HttpStatusCode.OK, activity.StatusCode);
        var activityJson = JsonDocument.Parse(await activity.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, activityJson.GetProperty("total").GetInt32());
        Assert.Contains(activityAction, activityJson.GetRawText());

        var presetName = $"Daily roster {Guid.NewGuid():N}";
        var save = await admin.PostAsJsonAsync("/api/phase1/operational-reports/presets", new { name = presetName, reportKey = "carer-roster", from = today, to = today, workerId, clientId });
        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        var presetId = JsonDocument.Parse(await save.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var presets = await admin.GetAsync("/api/phase1/operational-reports/presets");
        Assert.Equal(HttpStatusCode.OK, presets.StatusCode);
        Assert.Contains(presetName, await presets.Content.ReadAsStringAsync());
        var activityPreset = await admin.PostAsJsonAsync("/api/phase1/operational-reports/presets", new { name = $"Activity {Guid.NewGuid():N}", reportKey = "activity-logs", from = today, to = today, actor = "report-filter-actor", action = activityAction, entityType = "ReportFixture" });
        Assert.Equal(HttpStatusCode.Created, activityPreset.StatusCode);
        var activityPresetId = JsonDocument.Parse(await activityPreset.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var savedActivity = await admin.GetStringAsync("/api/phase1/operational-reports/presets");
        Assert.Contains(activityAction, savedActivity);
        Assert.Contains("report-filter-actor", savedActivity);

        await StepUpTestGrants.GrantAsync(factory, admin, "export");
        var export = await admin.GetAsync($"/api/phase1/operational-reports/carer-roster.csv?{filters}&reason=Daily%20roster%20review");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("text/csv", export.Content.Headers.ContentType?.MediaType);
        var csv = await export.Content.ReadAsStringAsync();
        Assert.Contains(workerName, csv);
        Assert.Contains(clientName, csv);
        await StepUpTestGrants.GrantAsync(factory, admin, "export");
        var appointmentsCsv = await admin.GetAsync($"/api/phase1/operational-reports/export-appointment-data.csv?{filters}&reason=Finance%20appointment%20review");
        Assert.Equal(HttpStatusCode.OK, appointmentsCsv.StatusCode);
        Assert.Contains(visitId.ToString(), await appointmentsCsv.Content.ReadAsStringAsync());
        await StepUpTestGrants.GrantAsync(factory, admin, "export");
        var activityCsv = await admin.GetAsync($"/api/phase1/operational-reports/activity-logs.csv?{activityFilters}&reason=Security%20activity%20review");
        Assert.Equal(HttpStatusCode.OK, activityCsv.StatusCode);
        Assert.Contains(activityAction, await activityCsv.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/phase1/operational-reports/presets/{presetId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/phase1/operational-reports/presets/{activityPresetId}")).StatusCode);
        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.True(await verifyDb.AuditEvents.AnyAsync(item => item.Action == "report.export.carer-roster"));
        Assert.True(await verifyDb.AuditEvents.AnyAsync(item => item.Action == "report.export.export-appointment-data"));
        Assert.True(await verifyDb.AuditEvents.AnyAsync(item => item.Action == "report.export.activity-logs"));
    }

    private sealed record Login(string Token);
}
