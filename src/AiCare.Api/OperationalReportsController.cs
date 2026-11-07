using System.Text;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[ApiController]
[Authorize(Policy = "Phase1User", Roles = "Administrator,CareManager,CareCoordinator,BackOffice")]
[Route("api/phase1/operational-reports")]
public sealed class OperationalReportsController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user) : ControllerBase
{
    private static readonly OperationalReportDefinition[] Definitions =
    [
        new("carer-roster", "Carer roster", "Carer reports", false),
        new("carer-diary", "Carer diary", "Carer reports", false),
        new("client-roster", "Client roster", "Client reports", false),
        new("client-diary", "Client diary", "Client reports", false),
        new("client-schedule", "Client schedule", "Client reports", false),
        new("unassigned-appointments", "Unassigned appointments", "Client reports", false),
        new("planned-hours", "Planned Hours Report", "Finance reports", false),
        new("stats-by-carer", "Stats by carer", "Carer reports", false),
        new("stats-by-client", "Stats by client", "Client reports", false),
        new("stats-by-date", "Stats by date", "Client reports", false),
        new("call-care-monitoring", "Call & Care Monitoring", "Care reports", false),
        new("care-plans", "Care plans", "Client reports", false),
        new("outstanding-checklist", "Outstanding checklist items", "Checklist reports", false),
        new("checklist-history", "Checklist history", "Checklist reports", false),
        new("checklist-history-by-date", "Checklist history by date", "Checklist reports", false),
        new("training-matrix", "Training matrix", "Training reports", false),
        new("acquired-training", "Acquired training", "Training reports", false),
        new("planned-training", "Planned training", "Training reports", false),
        new("required-training", "Required training", "Training reports", false),
        new("time-off-requests", "Time Off Requests", "Time off", false),
        new("time-off-summary", "Time Off Summary", "Time off", false),
        new("availability", "Availability report", "Carer reports", false),
        new("carer-contact-details", "Contact details", "Carer reports", false),
        new("carer-personal-details", "Personal details", "Carer reports", false),
        new("carer-reasons-for-leaving", "Reasons for leaving", "Carer reports", false),
        new("client-contact-details", "Contact details", "Client reports", false),
        new("client-personal-details", "Personal details", "Client reports", false),
        new("uninvoiced-appointments", "Uninvoiced appointments", "Finance reports", false),
        new("untimesheeted-appointments", "Untimesheeted appointments", "Finance reports", false),
        new("export-appointment-data", "Export appointment data", "Finance reports", false),
        new("finance-stats", "Finance stats", "Finance reports", false),
        new("finance-stats-by-client", "Finance stats by client", "Finance reports", false),
        new("activity-logs", "Activity logs", "Exports", false),
        new("clients-medication", "Clients medication report", "Client reports", true),
        new("medication-administration", "Medication administration report", "Client reports", true)
    ];

    [HttpGet("catalogue")]
    public IActionResult Catalogue() => Ok(Definitions.Where(Allowed));

    [HttpGet("presets")]
    public async Task<IActionResult> Presets(CancellationToken token)
    {
        if (user.UserId is null) return Unauthorized();
        var rows = await db.Database.SqlQuery<OperationalReportPreset>($"""select id as "Id", name as "Name", report_key as "ReportKey", from_date as "From", to_date as "To", worker_id as "WorkerId", client_id as "ClientId", actor_filter as "Actor", action_filter as "Action", entity_type_filter as "EntityType" from operational_report_presets where organization_id={tenant.OrganizationId} and owner_user_id={user.UserId.Value} order by created_at desc""").ToListAsync(token);
        return Ok(rows.Where(row => Definitions.Any(definition => definition.Key == row.ReportKey && Allowed(definition))));
    }

    [HttpPost("presets")]
    public async Task<IActionResult> SavePreset(SaveOperationalReportPreset request, CancellationToken token)
    {
        var definition = Definitions.SingleOrDefault(item => item.Key == request.ReportKey);
        if (definition is null) return BadRequest(new { message = "Choose an available report." });
        if (!Allowed(definition)) return Forbid();
        if (user.UserId is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100 || Invalid(new ReportQuery(request.From, request.To, request.WorkerId, request.ClientId, Actor: request.Actor, Action: request.Action, EntityType: request.EntityType)))
            return BadRequest(new { message = "Provide a name and valid report filters." });
        if (request.WorkerId is { } worker && !(await WorkerNames([worker], token)).ContainsKey(worker)) return BadRequest(new { message = "The selected carer is unavailable." });
        if (request.ClientId is { } client && !(await ClientNames([client], token)).ContainsKey(client)) return BadRequest(new { message = "The selected client is unavailable." });
        var id = Guid.NewGuid();
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""insert into operational_report_presets(id,organization_id,branch_id,owner_user_id,name,report_key,from_date,to_date,worker_id,client_id,actor_filter,action_filter,entity_type_filter) values ({id},{tenant.OrganizationId},{tenant.BranchId},{user.UserId.Value},{request.Name.Trim()},{request.ReportKey},{request.From},{request.To},{request.WorkerId},{request.ClientId},{request.Actor?.Trim() ?? ""},{request.Action?.Trim() ?? ""},{request.EntityType?.Trim() ?? ""})""", token);
        }
        catch (Npgsql.PostgresException exception) when (exception.SqlState == "23505")
        {
            return Conflict(new { message = "A saved report with this name already exists." });
        }
        db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "report.preset_created", user.UserName, "OperationalReportPreset", id, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
        await db.SaveChangesAsync(token);
        return Created($"/api/phase1/operational-reports/presets/{id}", new { id });
    }

    [HttpDelete("presets/{id:guid}")]
    public async Task<IActionResult> DeletePreset(Guid id, CancellationToken token)
    {
        if (user.UserId is null) return Unauthorized();
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"delete from operational_report_presets where id={id} and organization_id={tenant.OrganizationId} and owner_user_id={user.UserId.Value}", token);
        if (changed == 0) return NotFound();
        db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), "report.preset_deleted", user.UserName, "OperationalReportPreset", id, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
        await db.SaveChangesAsync(token);
        return NoContent();
    }

    [HttpGet("{key}")]
    public async Task<IActionResult> Run(string key, [FromQuery] ReportQuery query, CancellationToken token)
    {
        var definition = Definitions.SingleOrDefault(item => item.Key == key);
        if (definition is null) return NotFound();
        if (!Allowed(definition)) return Forbid();
        if (Invalid(query)) return BadRequest(new { message = "Use a valid date range of at most 366 days and a page size of 1-100." });
        var report = await Build(definition, query, false, token);
        db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), $"report.run.{key}", user.UserName, "OperationalReport", null, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
        await db.SaveChangesAsync(token);
        return Ok(report);
    }

    [HttpGet("{key}.csv")]
    public async Task<IActionResult> Export(string key, [FromQuery] ReportQuery query, [FromQuery] string? reason, CancellationToken token)
    {
        var definition = Definitions.SingleOrDefault(item => item.Key == key);
        if (definition is null) return NotFound();
        if (!Allowed(definition)) return Forbid();
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10) return BadRequest(new { message = "An export reason of at least 10 characters is required." });
        if (Invalid(query)) return BadRequest(new { message = "Use a valid date range of at most 366 days." });
        var report = await Build(definition, query with { Page = 1, PageSize = 100 }, true, token);
        if (report.Total > 5000) return Conflict(new { message = "This export exceeds 5,000 rows. Narrow the filters." });
        db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), $"report.export.{key}", user.UserName, "OperationalReport", null, DateTimeOffset.UtcNow, tenant.OrganizationId, tenant.BranchId ?? TenantDefaults.BranchId));
        await db.SaveChangesAsync(token);
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(',', report.Columns.Select(Csv)));
        foreach (var row in report.Rows) csv.AppendLine(string.Join(',', row.Select(Csv)));
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), "text/csv", $"{key}-{DateTimeOffset.UtcNow:yyyyMMdd}.csv");
    }

    private bool Allowed(OperationalReportDefinition definition) =>
        user.IsBackOffice ? definition.Key is "planned-hours" or "stats-by-carer" or "training-matrix" or "acquired-training" or "planned-training" or "required-training" or "time-off-requests" or "time-off-summary" or "uninvoiced-appointments" or "untimesheeted-appointments" or "export-appointment-data" or "finance-stats" or "finance-stats-by-client" :
        definition.Key == "activity-logs" ? user.IsAdministrator :
        definition.Key is "carer-contact-details" or "carer-personal-details" or "carer-reasons-for-leaving" or "client-contact-details" or "client-personal-details" or "uninvoiced-appointments" or "untimesheeted-appointments" or "export-appointment-data" or "finance-stats" or "finance-stats-by-client" ? user.HasAnyRole(UserRole.Administrator, UserRole.CareManager) :
        !definition.Medication || user.HasAnyRole(UserRole.Administrator, UserRole.CareManager);
    private static bool Invalid(ReportQuery query) => query.Page < 1 || query.PageSize is < 1 or > 100 || query.From is not null && query.To is not null && (query.To < query.From || query.To.Value.DayNumber - query.From.Value.DayNumber > 366) || new[] { query.Actor, query.Action, query.EntityType }.Any(value => value?.Length > 100);
    private IQueryable<Visit> Visits(ReportQuery query)
    {
        var from = AtStart(query.From ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)));
        var to = AtStart((query.To ?? DateOnly.FromDateTime(DateTime.UtcNow)).AddDays(1));
        var visits = db.Visits.AsNoTracking().Where(item => item.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || item.BranchId == tenant.BranchId) && item.StartsAt >= from && item.StartsAt < to);
        if (query.WorkerId is { } worker) visits = visits.Where(item => item.CareWorkerId == worker);
        if (query.ClientId is { } client) visits = visits.Where(item => item.ServiceUserId == client);
        return visits;
    }
    private static DateTimeOffset AtStart(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    private static string Csv(string value)
    {
        var safe = value.Length > 0 && "=+-@".Contains(value[0]) ? "'" + value : value;
        return '"' + safe.Replace("\"", "\"\"") + '"';
    }
    private async Task<ReportPage> Build(OperationalReportDefinition definition, ReportQuery query, bool export, CancellationToken token)
    {
        var skip = export ? 0 : (query.Page - 1) * query.PageSize;
        var take = export ? 5001 : query.PageSize;
        if (definition.Key == "activity-logs")
        {
            var from = AtStart(query.From ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)));
            var to = AtStart((query.To ?? DateOnly.FromDateTime(DateTime.UtcNow)).AddDays(1));
            var events = db.AuditEvents.AsNoTracking().Where(item => item.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || item.BranchId == tenant.BranchId) && item.CreatedAt >= from && item.CreatedAt < to);
            if (!string.IsNullOrWhiteSpace(query.Actor)) events = events.Where(item => EF.Functions.ILike(item.Actor, $"%{EscapeLike(query.Actor.Trim())}%", "\\"));
            if (!string.IsNullOrWhiteSpace(query.Action)) events = events.Where(item => EF.Functions.ILike(item.Action, $"%{EscapeLike(query.Action.Trim())}%", "\\"));
            if (!string.IsNullOrWhiteSpace(query.EntityType)) events = events.Where(item => EF.Functions.ILike(item.EntityType, $"%{EscapeLike(query.EntityType.Trim())}%", "\\"));
            var total = await events.CountAsync(token);
            var records = await events.OrderByDescending(item => item.CreatedAt).ThenBy(item => item.Id).Skip(skip).Take(take).ToListAsync(token);
            return new(definition.Key, definition.Title, ["Event ID", "When (UTC)", "Actor", "Action", "Entity", "Entity ID", "Branch ID"], records.Select(item => new[] { item.Id.ToString(), item.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"), item.Actor, item.Action, item.EntityType, item.EntityId?.ToString() ?? "", item.BranchId?.ToString() ?? "" }).ToList(), total, query.Page, query.PageSize);
        }
        if (definition.Key == "export-appointment-data")
        {
            var visits = Visits(query);
            var total = await visits.CountAsync(token);
            var records = await visits.OrderBy(item => item.StartsAt).ThenBy(item => item.Id).Skip(skip).Take(take).ToListAsync(token);
            var workers = await WorkerNames(records.Select(item => item.CareWorkerId).Distinct().ToArray(), token);
            var clients = await ClientNames(records.Select(item => item.ServiceUserId).Distinct().ToArray(), token);
            var rows = records.Select(item => new[] { item.Id.ToString(), item.StartsAt.ToString("yyyy-MM-dd HH:mm"), item.ServiceUserId.ToString(), clients.GetValueOrDefault(item.ServiceUserId, "Unknown"), item.CareWorkerId.ToString(), workers.GetValueOrDefault(item.CareWorkerId, "Unknown"), item.VisitType, item.Status.ToString(), item.DurationMinutes.ToString(), item.CheckedInAt?.ToString("yyyy-MM-dd HH:mm") ?? "", item.CheckedOutAt?.ToString("yyyy-MM-dd HH:mm") ?? "", item.CheckedInAt is { } start && item.CheckedOutAt is { } end ? Math.Round((end - start).TotalMinutes, 0).ToString() : "" }).ToList();
            return new(definition.Key, definition.Title, ["Visit ID", "Planned start (UTC)", "Client ID", "Client", "Carer ID", "Carer", "Care", "Status", "Planned minutes", "Check in (UTC)", "Check out (UTC)", "Actual minutes"], rows, total, query.Page, query.PageSize);
        }
        if (definition.Key == "finance-stats")
        {
            var from = query.From ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30));
            var to = query.To ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var stats = db.Database.SqlQuery<OperationalFinanceStatsRow>($"""
                select d.invoice_date as "InvoiceDate", i."Status" as "InvoiceStatus", trim(d.currency) as "Currency",
                       count(*)::integer as "Invoices", sum(d.net_amount) as "NetAmount",
                       sum(d.vat_amount) as "VatAmount", sum(d.gross_amount) as "GrossAmount"
                from finance_invoice_details d join "Invoices" i on i."Id"=d.invoice_id and i."OrganizationId"=d.organization_id
                where d.organization_id={tenant.OrganizationId} and ({tenant.IsOrganizationWide} or d.branch_id={tenant.BranchId})
                  and i."BranchId"=d.branch_id and i."Status"<>'Void' and d.invoice_date >= {from} and d.invoice_date <= {to}
                  and ({query.ClientId}::uuid is null or i."ServiceUserId"={query.ClientId})
                group by d.invoice_date,i."Status",d.currency
                """);
            var total = await stats.CountAsync(token);
            var records = await stats.OrderBy(item => item.InvoiceDate).ThenBy(item => item.InvoiceStatus).ThenBy(item => item.Currency).Skip(skip).Take(take).ToListAsync(token);
            return new(definition.Key, definition.Title, ["Invoice date", "Invoice status", "Currency", "Invoices", "Net", "VAT", "Gross"], records.Select(item => new[] { item.InvoiceDate.ToString("yyyy-MM-dd"), item.InvoiceStatus, item.Currency, item.Invoices.ToString(), item.NetAmount.ToString("0.00"), item.VatAmount.ToString("0.00"), item.GrossAmount.ToString("0.00") }).ToList(), total, query.Page, query.PageSize);
        }
        if (definition.Key == "finance-stats-by-client")
        {
            var from = query.From ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30));
            var to = query.To ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var stats = db.Database.SqlQuery<OperationalClientFinanceStatsRow>($"""
                select l.service_user_id as "ClientId", i."Status" as "InvoiceStatus", trim(d.currency) as "Currency",
                       count(distinct i."Id")::integer as "Invoices", count(*)::integer as "Lines", sum(l.amount) as "NetLineAmount"
                from finance_invoice_lines l join "Invoices" i on i."Id"=l.invoice_id and i."OrganizationId"=l.organization_id
                    join finance_invoice_details d on d.invoice_id=i."Id" and d.organization_id=i."OrganizationId"
                    join "ServiceUsers" s on s."Id"=l.service_user_id and s."OrganizationId"=l.organization_id
                where l.organization_id={tenant.OrganizationId} and ({tenant.IsOrganizationWide} or l.branch_id={tenant.BranchId})
                  and i."BranchId"=l.branch_id and s."BranchId"=l.branch_id and i."Status"<>'Void'
                  and d.invoice_date >= {from} and d.invoice_date <= {to}
                  and ({query.ClientId}::uuid is null or l.service_user_id={query.ClientId})
                group by l.service_user_id,i."Status",d.currency
                """);
            var total = await stats.CountAsync(token);
            var records = await stats.OrderBy(item => item.ClientId).ThenBy(item => item.InvoiceStatus).ThenBy(item => item.Currency).Skip(skip).Take(take).ToListAsync(token);
            var clients = await ClientNames(records.Select(item => item.ClientId).Distinct().ToArray(), token);
            return new(definition.Key, definition.Title, ["Client", "Invoice status", "Currency", "Invoices", "Lines", "Net line amount"], records.Select(item => new[] { clients.GetValueOrDefault(item.ClientId, "Unknown"), item.InvoiceStatus, item.Currency, item.Invoices.ToString(), item.Lines.ToString(), item.NetLineAmount.ToString("0.00") }).ToList(), total, query.Page, query.PageSize);
        }
        if (definition.Key is "uninvoiced-appointments" or "untimesheeted-appointments")
        {
            var uninvoiced = definition.Key == "uninvoiced-appointments";
            var from = AtStart(query.From ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)));
            var to = AtStart((query.To ?? DateOnly.FromDateTime(DateTime.UtcNow)).AddDays(1));
            var exceptions = db.Database.SqlQuery<OperationalFinanceExceptionRow>($"""
                select v."Id" as "VisitId", v."StartsAt" as "StartsAt", v."ServiceUserId" as "ClientId",
                       v."CareWorkerId" as "WorkerId", v."VisitType" as "VisitType", v."DurationMinutes" as "DurationMinutes",
                       coalesce(b.status,'') as "BillableStatus",
                       case when {uninvoiced} and b.id is null then 'No billable event'
                            when {uninvoiced} and b.status='Rejected' then 'Billable event rejected'
                            when {uninvoiced} and b.status='Approved' then 'Ready for invoice'
                            when {uninvoiced} and b.status='Invoiced' then 'Invoiced state without invoice line'
                            when {uninvoiced} then 'Billable event ' || b.status
                            else 'No timesheet entry' end as "Reason"
                from "Visits" v join "ServiceUsers" s on s."Id"=v."ServiceUserId" and s."OrganizationId"=v."OrganizationId"
                    join "CareWorkers" w on w."Id"=v."CareWorkerId" and w."OrganizationId"=v."OrganizationId"
                    left join finance_billable_events b on b.visit_id=v."Id" and b.organization_id=v."OrganizationId"
                where v."OrganizationId"={tenant.OrganizationId} and ({tenant.IsOrganizationWide} or v."BranchId"={tenant.BranchId})
                  and s."BranchId"=v."BranchId" and w."BranchId"=v."BranchId"
                  and v."Status"='Completed' and v."StartsAt" >= {from} and v."StartsAt" < {to}
                  and ({query.WorkerId}::uuid is null or v."CareWorkerId"={query.WorkerId})
                  and ({query.ClientId}::uuid is null or v."ServiceUserId"={query.ClientId})
                  and ({uninvoiced} and not exists(select 1 from finance_invoice_lines l where l.organization_id=v."OrganizationId" and l.visit_id=v."Id")
                       or not {uninvoiced} and not exists(select 1 from timesheet_entries e where e.organization_id=v."OrganizationId" and e.visit_id=v."Id"))
                """);
            var total = await exceptions.CountAsync(token);
            var records = await exceptions.OrderBy(item => item.StartsAt).ThenBy(item => item.VisitId).Skip(skip).Take(take).ToListAsync(token);
            var workers = await WorkerNames(records.Select(item => item.WorkerId).Distinct().ToArray(), token);
            var clients = await ClientNames(records.Select(item => item.ClientId).Distinct().ToArray(), token);
            var rows = records.Select(item => new[] { item.StartsAt.ToString("yyyy-MM-dd HH:mm"), clients.GetValueOrDefault(item.ClientId, "Unknown"), workers.GetValueOrDefault(item.WorkerId, "Unknown"), item.VisitType, item.DurationMinutes.ToString(), item.BillableStatus, item.Reason }).ToList();
            return new(definition.Key, definition.Title, ["Visit (UTC)", "Client", "Carer", "Care", "Planned minutes", "Billable status", "Exception"], rows, total, query.Page, query.PageSize);
        }
        if (definition.Key == "availability")
        {
            var from = query.From ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30));
            var to = query.To ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var rules = db.Database.SqlQuery<OperationalAvailabilityRow>($"""
                select a.id as "Id", a.care_worker_id as "WorkerId", a.day_of_week as "DayOfWeek",
                       a.start_time as "StartTime", a.end_time as "EndTime", a.is_available as "IsAvailable",
                       a.effective_from as "EffectiveFrom", a.effective_to as "EffectiveTo", a.notes as "Notes"
                from worker_availability_rules a join "CareWorkers" w on w."Id"=a.care_worker_id and w."OrganizationId"=a.organization_id
                where a.organization_id={tenant.OrganizationId} and ({tenant.IsOrganizationWide} or a.branch_id={tenant.BranchId})
                  and w."BranchId"=a.branch_id and a.effective_from <= {to}
                  and (a.effective_to is null or a.effective_to >= {from})
                  and ({query.WorkerId}::uuid is null or a.care_worker_id={query.WorkerId})
                """);
            var total = await rules.CountAsync(token);
            var records = await rules.OrderBy(item => item.WorkerId).ThenBy(item => item.DayOfWeek).ThenBy(item => item.StartTime).Skip(skip).Take(take).ToListAsync(token);
            var workers = await WorkerNames(records.Select(item => item.WorkerId).Distinct().ToArray(), token);
            var rows = records.Select(item => new[] { workers.GetValueOrDefault(item.WorkerId, "Unknown"), ((DayOfWeek)item.DayOfWeek).ToString(), item.StartTime.ToString("HH:mm"), item.EndTime.ToString("HH:mm"), item.IsAvailable ? "Available" : "Unavailable", item.EffectiveFrom.ToString("yyyy-MM-dd"), item.EffectiveTo?.ToString("yyyy-MM-dd") ?? "", item.Notes }).ToList();
            return new(definition.Key, definition.Title, ["Carer", "Day", "Start", "End", "Rule", "Effective from", "Effective to", "Notes"], rows, total, query.Page, query.PageSize);
        }
        if (definition.Key == "carer-contact-details")
        {
            var contacts = db.Database.SqlQuery<OperationalWorkerContactRow>($"""
                select w."Id" as "WorkerId", w."FullName" as "Name", w."Specialization" as "Specialization",
                       coalesce(min(u."Email") filter (where u."IsActive"), '') as "AccountEmail"
                from "CareWorkers" w left join "AppUsers" u on u."CareWorkerId"=w."Id" and u."OrganizationId"=w."OrganizationId" and u."BranchId"=w."BranchId"
                where w."OrganizationId"={tenant.OrganizationId} and ({tenant.IsOrganizationWide} or w."BranchId"={tenant.BranchId})
                  and ({query.WorkerId}::uuid is null or w."Id"={query.WorkerId})
                group by w."Id",w."FullName",w."Specialization"
                """);
            var total = await contacts.CountAsync(token);
            var records = await contacts.OrderBy(item => item.Name).ThenBy(item => item.WorkerId).Skip(skip).Take(take).ToListAsync(token);
            return new(definition.Key, definition.Title, ["Carer", "Specialization", "Active account email"], records.Select(item => new[] { item.Name, item.Specialization, item.AccountEmail }).ToList(), total, query.Page, query.PageSize);
        }
        if (definition.Key == "carer-personal-details")
        {
            var profiles = db.Database.SqlQuery<OperationalWorkerProfileRow>($"""
                select w."Id" as "WorkerId", w."FullName" as "Name", w."Specialization" as "Specialization",
                       coalesce(p.employment_status,'Not recorded') as "EmploymentStatus",
                       coalesce(p.job_title,'') as "JobTitle", coalesce(p.contract_type,'') as "ContractType",
                       coalesce(p.contracted_weekly_minutes,0) as "ContractedWeeklyMinutes",
                       p.start_date as "StartDate", p.end_date as "EndDate", coalesce(p.line_manager,'') as "LineManager"
                from "CareWorkers" w left join worker_employment_profiles p on p.care_worker_id=w."Id"
                    and p.organization_id=w."OrganizationId" and p.branch_id=w."BranchId"
                where w."OrganizationId"={tenant.OrganizationId} and ({tenant.IsOrganizationWide} or w."BranchId"={tenant.BranchId})
                  and ({query.WorkerId}::uuid is null or w."Id"={query.WorkerId})
                """);
            var total = await profiles.CountAsync(token);
            var records = await profiles.OrderBy(item => item.Name).ThenBy(item => item.WorkerId).Skip(skip).Take(take).ToListAsync(token);
            var rows = records.Select(item => new[] { item.Name, item.Specialization, item.EmploymentStatus, item.JobTitle, item.ContractType, item.ContractedWeeklyMinutes.ToString(), item.StartDate?.ToString("yyyy-MM-dd") ?? "", item.EndDate?.ToString("yyyy-MM-dd") ?? "", item.LineManager }).ToList();
            return new(definition.Key, definition.Title, ["Carer", "Specialization", "Employment status", "Job title", "Contract", "Weekly minutes", "Start date", "End date", "Line manager"], rows, total, query.Page, query.PageSize);
        }
        if (definition.Key == "carer-reasons-for-leaving")
        {
            var from = query.From ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30));
            var to = query.To ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var leavers = db.Database.SqlQuery<OperationalWorkerLeaverRow>($"""
                select w."Id" as "WorkerId", w."FullName" as "Name", p.job_title as "JobTitle",
                       p.end_date as "EndDate", p.status_reason as "Reason", p.updated_at as "UpdatedAt"
                from worker_employment_profiles p join "CareWorkers" w on w."Id"=p.care_worker_id
                    and w."OrganizationId"=p.organization_id and w."BranchId"=p.branch_id
                where p.organization_id={tenant.OrganizationId} and ({tenant.IsOrganizationWide} or p.branch_id={tenant.BranchId})
                  and p.employment_status='Leaver' and p.end_date >= {from} and p.end_date <= {to}
                  and ({query.WorkerId}::uuid is null or p.care_worker_id={query.WorkerId})
                """);
            var total = await leavers.CountAsync(token);
            var records = await leavers.OrderBy(item => item.EndDate).ThenBy(item => item.WorkerId).Skip(skip).Take(take).ToListAsync(token);
            return new(definition.Key, definition.Title, ["Carer", "Job title", "Leaving date", "Recorded reason", "Last updated (UTC)"], records.Select(item => new[] { item.Name, item.JobTitle, item.EndDate.ToString("yyyy-MM-dd"), item.Reason, item.UpdatedAt.ToString("yyyy-MM-dd HH:mm") }).ToList(), total, query.Page, query.PageSize);
        }
        if (definition.Key == "client-contact-details")
        {
            var contacts = db.Database.SqlQuery<OperationalClientContactRow>($"""
                select s."Id" as "ClientId", s."FullName" as "ClientName", s."PhoneNumber" as "ClientPhone",
                       coalesce(p.contact_type,'') as "ContactType", coalesce(p.full_name,'') as "ContactName",
                       coalesce(p.relationship,'') as "Relationship", coalesce(p.phone_number,'') as "ContactPhone",
                       coalesce(p.email,'') as "ContactEmail", coalesce(p.is_primary,false) as "IsPrimary",
                       coalesce(p.is_emergency,false) as "IsEmergency", p.id as "ContactId"
                from "ServiceUsers" s left join person_contacts p on p.service_user_id=s."Id"
                    and p.organization_id=s."OrganizationId" and p.branch_id=s."BranchId"
                where s."OrganizationId"={tenant.OrganizationId} and ({tenant.IsOrganizationWide} or s."BranchId"={tenant.BranchId})
                  and ({query.ClientId}::uuid is null or s."Id"={query.ClientId})
                """);
            var total = await contacts.CountAsync(token);
            var records = await contacts.OrderBy(item => item.ClientName).ThenByDescending(item => item.IsPrimary).ThenBy(item => item.ContactName).ThenBy(item => item.ContactId).Skip(skip).Take(take).ToListAsync(token);
            var rows = records.Select(item => new[] { item.ClientName, item.ClientPhone, item.ContactType, item.ContactName, item.Relationship, item.ContactPhone, item.ContactEmail, item.IsPrimary ? "Yes" : "No", item.IsEmergency ? "Yes" : "No" }).ToList();
            return new(definition.Key, definition.Title, ["Client", "Client phone", "Contact type", "Contact", "Relationship", "Contact phone", "Contact email", "Primary", "Emergency"], rows, total, query.Page, query.PageSize);
        }
        if (definition.Key == "client-personal-details")
        {
            var people = db.ServiceUsers.AsNoTracking().Where(item => item.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || item.BranchId == tenant.BranchId));
            if (query.ClientId is { } client) people = people.Where(item => item.Id == client);
            var total = await people.CountAsync(token);
            var records = await people.OrderBy(item => item.FullName).ThenBy(item => item.Id).Skip(skip).Take(take).ToListAsync(token);
            var rows = records.Select(item => new[] { item.FullName, item.DateOfBirth.ToString("yyyy-MM-dd"), item.Gender, item.Status, item.PhoneNumber, item.Address, item.CommunicationNeeds }).ToList();
            return new(definition.Key, definition.Title, ["Client", "Date of birth", "Gender", "Status", "Phone", "Address", "Communication needs"], rows, total, query.Page, query.PageSize);
        }
        if (definition.Key == "time-off-requests")
        {
            var from = AtStart(query.From ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)));
            var to = AtStart((query.To ?? DateOnly.FromDateTime(DateTime.UtcNow)).AddDays(1));
            var absences = db.Database.SqlQuery<OperationalTimeOffRow>($"""
                select a.id as "Id", a.care_worker_id as "WorkerId", a.absence_type as "Type",
                       a.starts_at as "StartsAt", a.ends_at as "EndsAt", a.status as "Status",
                       a.notes as "Notes", a.cover_status as "CoverStatus", a.created_at as "CreatedAt"
                from worker_absences a join "CareWorkers" w on w."Id"=a.care_worker_id and w."OrganizationId"=a.organization_id
                where a.organization_id={tenant.OrganizationId} and ({tenant.IsOrganizationWide} or a.branch_id={tenant.BranchId})
                  and w."BranchId"=a.branch_id and a.starts_at < {to} and a.ends_at > {from}
                  and ({query.WorkerId}::uuid is null or a.care_worker_id={query.WorkerId})
                """);
            var total = await absences.CountAsync(token);
            var records = await absences.OrderBy(item => item.StartsAt).ThenBy(item => item.Id).Skip(skip).Take(take).ToListAsync(token);
            var workers = await WorkerNames(records.Select(item => item.WorkerId).Distinct().ToArray(), token);
            var rows = records.Select(item => new[] { workers.GetValueOrDefault(item.WorkerId, "Unknown"), item.Type, item.StartsAt.ToString("yyyy-MM-dd HH:mm"), item.EndsAt.ToString("yyyy-MM-dd HH:mm"), item.Status, item.CoverStatus, item.Notes, item.CreatedAt.ToString("yyyy-MM-dd HH:mm") }).ToList();
            return new(definition.Key, definition.Title, ["Carer", "Type", "Start (UTC)", "End (UTC)", "Status", "Cover", "Notes", "Recorded (UTC)"], rows, total, query.Page, query.PageSize);
        }
        if (definition.Key == "time-off-summary")
        {
            var from = AtStart(query.From ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)));
            var to = AtStart((query.To ?? DateOnly.FromDateTime(DateTime.UtcNow)).AddDays(1));
            var summary = db.Database.SqlQuery<OperationalTimeOffSummaryRow>($"""
                select a.care_worker_id as "WorkerId", a.absence_type as "Type", a.status as "Status",
                       count(*)::integer as "Periods",
                       round(sum(extract(epoch from (least(a.ends_at,{to}) - greatest(a.starts_at,{from}))) / 3600),2) as "Hours"
                from worker_absences a join "CareWorkers" w on w."Id"=a.care_worker_id and w."OrganizationId"=a.organization_id
                where a.organization_id={tenant.OrganizationId} and ({tenant.IsOrganizationWide} or a.branch_id={tenant.BranchId})
                  and w."BranchId"=a.branch_id and a.starts_at < {to} and a.ends_at > {from}
                  and ({query.WorkerId}::uuid is null or a.care_worker_id={query.WorkerId})
                group by a.care_worker_id,a.absence_type,a.status
                """);
            var total = await summary.CountAsync(token);
            var records = await summary.OrderBy(item => item.WorkerId).ThenBy(item => item.Type).ThenBy(item => item.Status).Skip(skip).Take(take).ToListAsync(token);
            var workers = await WorkerNames(records.Select(item => item.WorkerId).Distinct().ToArray(), token);
            var rows = records.Select(item => new[] { workers.GetValueOrDefault(item.WorkerId, "Unknown"), item.Type, item.Status, item.Periods.ToString(), item.Hours.ToString("0.##") }).ToList();
            return new(definition.Key, definition.Title, ["Carer", "Type", "Status", "Recorded periods", "Recorded hours in period"], rows, total, query.Page, query.PageSize);
        }
        if (definition.Key is "training-matrix" or "required-training")
        {
            var asOf = AtStart((query.To ?? DateOnly.FromDateTime(DateTime.UtcNow)).AddDays(1));
            var matrix = db.Database.SqlQuery<OperationalTrainingMatrixRow>($"""
                select w."Id" as "WorkerId", c.id as "CourseId", c.code as "Code", c.title as "Title",
                       b.completed_at as "CompletedAt", b.expires_at as "ExpiresAt", upcoming.scheduled_at as "PlannedAt",
                       case when b.completed_at is not null and (b.expires_at is null or b.expires_at >= {asOf}) then 'Valid'
                            when b.completed_at is not null then 'Expired'
                            when upcoming.scheduled_at is not null then 'Planned' else 'Missing' end as "Status"
                from "CareWorkers" w cross join training_catalogue_courses c
                left join lateral (select x.completed_at,x.expires_at from worker_training_bookings x
                                   where x.care_worker_id=w."Id" and x.course_id=c.id and x.organization_id=w."OrganizationId"
                                     and x.branch_id=w."BranchId" and x.status='Completed' and x.completed_at < {asOf}
                                   order by x.completed_at desc,x.id limit 1) b on true
                left join lateral (select min(x.scheduled_at) as scheduled_at from worker_training_bookings x
                                   where x.care_worker_id=w."Id" and x.course_id=c.id and x.organization_id=w."OrganizationId"
                                     and x.branch_id=w."BranchId" and x.status in ('Booked','Attended') and x.scheduled_at >= {asOf}) upcoming on true
                where w."OrganizationId"={tenant.OrganizationId} and ({tenant.IsOrganizationWide} or w."BranchId"={tenant.BranchId})
                  and c.organization_id={tenant.OrganizationId} and c.active and c.is_mandatory
                  and (c.branch_id is null or c.branch_id=w."BranchId")
                  and ({query.WorkerId}::uuid is null or w."Id"={query.WorkerId})
                """);
            if (definition.Key == "required-training") matrix = matrix.Where(item => item.Status != "Valid");
            var total = await matrix.CountAsync(token);
            var records = await matrix.OrderBy(item => item.WorkerId).ThenBy(item => item.Code).Skip(skip).Take(take).ToListAsync(token);
            var workers = await WorkerNames(records.Select(item => item.WorkerId).Distinct().ToArray(), token);
            var rows = records.Select(item => new[] { workers.GetValueOrDefault(item.WorkerId, "Unknown"), item.Code, item.Title, item.Status, item.CompletedAt?.ToString("yyyy-MM-dd") ?? "", item.ExpiresAt?.ToString("yyyy-MM-dd") ?? "", item.PlannedAt?.ToString("yyyy-MM-dd") ?? "" }).ToList();
            return new(definition.Key, definition.Title, ["Carer", "Course code", "Course", "Status", "Completed (UTC)", "Expires (UTC)", "Planned (UTC)"], rows, total, query.Page, query.PageSize);
        }
        if (definition.Key is "acquired-training" or "planned-training")
        {
            var acquired = definition.Key == "acquired-training";
            var from = AtStart(query.From ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)));
            var to = AtStart((query.To ?? DateOnly.FromDateTime(DateTime.UtcNow)).AddDays(1));
            var bookings = db.Database.SqlQuery<OperationalTrainingBookingRow>($"""
                select b.id as "Id", b.care_worker_id as "WorkerId", c.code as "Code", c.title as "Title",
                       b.status as "Status", b.scheduled_at as "ScheduledAt", b.completed_at as "CompletedAt",
                       b.expires_at as "ExpiresAt", b.completion_evidence as "Evidence"
                from worker_training_bookings b join training_catalogue_courses c on c.id=b.course_id and c.organization_id=b.organization_id
                join "CareWorkers" w on w."Id"=b.care_worker_id and w."OrganizationId"=b.organization_id
                where b.organization_id={tenant.OrganizationId} and ({tenant.IsOrganizationWide} or b.branch_id={tenant.BranchId})
                  and b.branch_id=w."BranchId" and (c.branch_id is null or c.branch_id=b.branch_id)
                  and ({query.WorkerId}::uuid is null or b.care_worker_id={query.WorkerId})
                  and ({acquired} and b.status='Completed' and b.completed_at >= {from} and b.completed_at < {to}
                       or not {acquired} and b.status in ('Booked','Attended') and b.scheduled_at >= {from} and b.scheduled_at < {to})
                """);
            var total = await bookings.CountAsync(token);
            var records = await bookings.OrderBy(item => item.ScheduledAt).ThenBy(item => item.Id).Skip(skip).Take(take).ToListAsync(token);
            var workers = await WorkerNames(records.Select(item => item.WorkerId).Distinct().ToArray(), token);
            var rows = records.Select(item => new[] { workers.GetValueOrDefault(item.WorkerId, "Unknown"), item.Code, item.Title, item.Status, item.ScheduledAt.ToString("yyyy-MM-dd HH:mm"), item.CompletedAt?.ToString("yyyy-MM-dd") ?? "", item.ExpiresAt?.ToString("yyyy-MM-dd") ?? "", item.Evidence }).ToList();
            return new(definition.Key, definition.Title, ["Carer", "Course code", "Course", "Status", "Scheduled (UTC)", "Completed (UTC)", "Expires (UTC)", "Evidence"], rows, total, query.Page, query.PageSize);
        }
        if (definition.Key is "carer-diary" or "client-diary")
        {
            var from = AtStart(query.From ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)));
            var to = AtStart((query.To ?? DateOnly.FromDateTime(DateTime.UtcNow)).AddDays(1));
            var canReadManagerOnlyDiary = User.IsInRole("Administrator") || User.IsInRole("CareManager");
            var diary = db.Database.SqlQuery<OperationalDiaryReportRow>($"""
                select d.id as "Id", d.event_at as "EventAt", d.category as "Category", d.priority as "Priority",
                       d.status as "Status", d.subject as "Subject", d.detail as "Detail",
                       d.service_user_id as "ClientId", d.care_worker_id as "WorkerId"
                from operational_diary_entries d
                where d.organization_id={tenant.OrganizationId} and ({tenant.IsOrganizationWide} or d.branch_id={tenant.BranchId})
                  and d.event_at >= {from} and d.event_at < {to}
                  and ({query.WorkerId}::uuid is null or d.care_worker_id={query.WorkerId})
                  and ({query.ClientId}::uuid is null or d.service_user_id={query.ClientId})
                  and ({definition.Key == "carer-diary"} or d.service_user_id is not null)
                  and ({definition.Key == "client-diary"} or d.care_worker_id is not null)
                  and ({canReadManagerOnlyDiary} or not exists (
                      select 1 from care_operation_options o
                      where o.organization_id=d.organization_id and o.kind='DiaryCategory'
                        and o.value=d.category and o.visibility='ManagersOnly'))
                """);
            var total = await diary.CountAsync(token);
            var records = await diary.OrderByDescending(item => item.EventAt).ThenBy(item => item.Id).Skip(skip).Take(take).ToListAsync(token);
            var workers = await WorkerNames(records.Where(item => item.WorkerId.HasValue).Select(item => item.WorkerId!.Value).Distinct().ToArray(), token);
            var clients = await ClientNames(records.Where(item => item.ClientId.HasValue).Select(item => item.ClientId!.Value).Distinct().ToArray(), token);
            var rows = records.Select(item => new[] { item.EventAt.ToString("yyyy-MM-dd HH:mm"), item.ClientId is { } client ? clients.GetValueOrDefault(client, "Unknown") : "", item.WorkerId is { } worker ? workers.GetValueOrDefault(worker, "Unknown") : "", item.Category, item.Priority, item.Status, item.Subject, item.Detail }).ToList();
            return new(definition.Key, definition.Title, ["Event (UTC)", "Client", "Carer", "Category", "Priority", "Status", "Subject", "Detail"], rows, total, query.Page, query.PageSize);
        }
        if (definition.Key is "outstanding-checklist" or "checklist-history" or "checklist-history-by-date")
        {
            var history = definition.Key != "outstanding-checklist";
            var from = AtStart(query.From ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)));
            var to = AtStart((query.To ?? DateOnly.FromDateTime(DateTime.UtcNow)).AddDays(1));
            var tasks = db.Database.SqlQuery<OperationalChecklistReportRow>($"""
                select t.id as "Id", v."StartsAt" as "VisitAt", t.completed_at as "CompletedAt",
                       t.service_user_id as "ClientId", t.care_worker_id as "WorkerId", t.title as "Title",
                       t.category as "Category", t.status as "Status", t.outcome as "Outcome",
                       t.exception_reason as "ExceptionReason"
                from visit_tasks t join "Visits" v on v."Id"=t.visit_id and v."OrganizationId"=t.organization_id
                where t.organization_id={tenant.OrganizationId} and ({tenant.IsOrganizationWide} or t.branch_id={tenant.BranchId})
                  and t.care_worker_id=v."CareWorkerId" and t.service_user_id=v."ServiceUserId"
                  and ({query.WorkerId}::uuid is null or t.care_worker_id={query.WorkerId})
                  and ({query.ClientId}::uuid is null or t.service_user_id={query.ClientId})
                  and ({history} and t.status='Completed' and t.completed_at >= {from} and t.completed_at < {to}
                       or not {history} and t.status='Pending' and v."StartsAt" >= {from} and v."StartsAt" < {to})
                """);
            var total = await tasks.CountAsync(token);
            var records = history
                ? await tasks.OrderByDescending(item => item.CompletedAt).ThenBy(item => item.Id).Skip(skip).Take(take).ToListAsync(token)
                : await tasks.OrderBy(item => item.VisitAt).ThenBy(item => item.Id).Skip(skip).Take(take).ToListAsync(token);
            var workers = await WorkerNames(records.Select(item => item.WorkerId).Distinct().ToArray(), token);
            var clients = await ClientNames(records.Select(item => item.ClientId).Distinct().ToArray(), token);
            var rows = records.Select(item => new[] { (history ? item.CompletedAt : item.VisitAt)?.ToString("yyyy-MM-dd HH:mm") ?? "", clients.GetValueOrDefault(item.ClientId, "Unknown"), workers.GetValueOrDefault(item.WorkerId, "Unknown"), item.Title, item.Category, item.Status, item.Outcome, item.ExceptionReason }).ToList();
            return new(definition.Key, definition.Title, [history ? "Completed (UTC)" : "Visit (UTC)", "Client", "Carer", "Item", "Category", "Status", "Outcome", "Exception reason"], rows, total, query.Page, query.PageSize);
        }
        if (definition.Key is "carer-roster" or "client-roster" or "client-schedule" or "unassigned-appointments" or "call-care-monitoring")
        {
            var visits = Visits(query);
            if (definition.Key == "unassigned-appointments") visits = visits.Where(item => item.CareWorkerId == Guid.Empty);
            var total = await visits.CountAsync(token);
            var records = await visits.OrderBy(item => item.StartsAt).ThenBy(item => item.Id).Skip(skip).Take(take).ToListAsync(token);
            var workers = await WorkerNames(records.Select(item => item.CareWorkerId).Distinct().ToArray(), token);
            var clients = await ClientNames(records.Select(item => item.ServiceUserId).Distinct().ToArray(), token);
            var rows = records.Select(item => new[] { item.StartsAt.ToString("yyyy-MM-dd HH:mm"), clients.GetValueOrDefault(item.ServiceUserId, "Unknown"), workers.GetValueOrDefault(item.CareWorkerId, "Unassigned"), item.VisitType, item.DurationMinutes.ToString(), item.Status.ToString(), item.CheckedInAt?.ToString("yyyy-MM-dd HH:mm") ?? "", item.CheckedOutAt?.ToString("yyyy-MM-dd HH:mm") ?? "" }).ToList();
            return new(definition.Key, definition.Title, ["Planned start (UTC)", "Client", "Carer", "Care", "Planned minutes", "Status", "Check in (UTC)", "Check out (UTC)"], rows, total, query.Page, query.PageSize);
        }
        if (definition.Key is "planned-hours" or "stats-by-carer" or "stats-by-client" or "stats-by-date")
        {
            var visits = Visits(query);
            if (definition.Key == "stats-by-date")
            {
                var grouped = visits.GroupBy(item => item.StartsAt.Date).Select(group => new { Date = group.Key, Calls = group.Count(), Minutes = group.Sum(item => item.DurationMinutes), Completed = group.Count(item => item.Status == VisitStatus.Completed) });
                var total = await grouped.CountAsync(token);
                var records = await grouped.OrderBy(item => item.Date).Skip(skip).Take(take).ToListAsync(token);
                return new(definition.Key, definition.Title, ["Date (UTC)", "Calls", "Planned minutes", "Completed calls"], records.Select(item => new[] { item.Date.ToString("yyyy-MM-dd"), item.Calls.ToString(), item.Minutes.ToString(), item.Completed.ToString() }).ToList(), total, query.Page, query.PageSize);
            }
            if (definition.Key == "stats-by-client")
            {
                var grouped = visits.GroupBy(item => item.ServiceUserId).Select(group => new { Id = group.Key, Calls = group.Count(), Minutes = group.Sum(item => item.DurationMinutes), Completed = group.Count(item => item.Status == VisitStatus.Completed) });
                var total = await grouped.CountAsync(token);
                var records = await grouped.OrderBy(item => item.Id).Skip(skip).Take(take).ToListAsync(token);
                var names = await ClientNames(records.Select(item => item.Id).ToArray(), token);
                return new(definition.Key, definition.Title, ["Client", "Calls", "Planned minutes", "Completed calls"], records.Select(item => new[] { names.GetValueOrDefault(item.Id, "Unknown"), item.Calls.ToString(), item.Minutes.ToString(), item.Completed.ToString() }).ToList(), total, query.Page, query.PageSize);
            }
            else
            {
                var grouped = visits.GroupBy(item => item.CareWorkerId).Select(group => new { Id = group.Key, Calls = group.Count(), Minutes = group.Sum(item => item.DurationMinutes), Completed = group.Count(item => item.Status == VisitStatus.Completed) });
                var total = await grouped.CountAsync(token);
                var records = await grouped.OrderBy(item => item.Id).Skip(skip).Take(take).ToListAsync(token);
                var names = await WorkerNames(records.Select(item => item.Id).ToArray(), token);
                return new(definition.Key, definition.Title, ["Carer", "Calls", "Planned minutes", "Completed calls"], records.Select(item => new[] { names.GetValueOrDefault(item.Id, "Unassigned"), item.Calls.ToString(), item.Minutes.ToString(), item.Completed.ToString() }).ToList(), total, query.Page, query.PageSize);
            }
        }
        if (definition.Key == "care-plans")
        {
            var plans = db.CarePlans.AsNoTracking().Where(item => item.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || item.BranchId == tenant.BranchId));
            if (query.ClientId is { } client) plans = plans.Where(item => item.ServiceUserId == client);
            var total = await plans.CountAsync(token);
            var records = await plans.OrderBy(item => item.ReviewDueAt).ThenBy(item => item.Id).Skip(skip).Take(take).ToListAsync(token);
            var names = await ClientNames(records.Select(item => item.ServiceUserId).Distinct().ToArray(), token);
            return new(definition.Key, definition.Title, ["Client", "Version", "Status", "Review due (UTC)"], records.Select(item => new[] { names.GetValueOrDefault(item.ServiceUserId, "Unknown"), item.Version, item.Status, item.ReviewDueAt.ToString("yyyy-MM-dd") }).ToList(), total, query.Page, query.PageSize);
        }
        if (definition.Key == "clients-medication")
        {
            var medications = db.Medications.AsNoTracking().Where(item => item.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || item.BranchId == tenant.BranchId));
            if (query.ClientId is { } client) medications = medications.Where(item => item.ServiceUserId == client);
            var total = await medications.CountAsync(token);
            var records = await medications.OrderBy(item => item.Name).ThenBy(item => item.Id).Skip(skip).Take(take).ToListAsync(token);
            var names = await ClientNames(records.Select(item => item.ServiceUserId).Distinct().ToArray(), token);
            return new(definition.Key, definition.Title, ["Client", "Medication", "Dose", "Route", "Schedule", "PRN"], records.Select(item => new[] { names.GetValueOrDefault(item.ServiceUserId, "Unknown"), item.Name, item.Dosage, item.Route, item.Schedule, item.IsPrn ? "Yes" : "No" }).ToList(), total, query.Page, query.PageSize);
        }
        if (definition.Key == "medication-administration")
        {
            var fromTime = AtStart(query.From ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)));
            var to = AtStart((query.To ?? DateOnly.FromDateTime(DateTime.UtcNow)).AddDays(1));
            var administrations = from item in db.MedicationAdministrationRecords.AsNoTracking()
                                  join medication in db.Medications.AsNoTracking() on item.MedicationId equals medication.Id
                                  where item.OrganizationId == tenant.OrganizationId && medication.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || item.BranchId == tenant.BranchId && medication.BranchId == tenant.BranchId) && item.ScheduledAt >= fromTime && item.ScheduledAt < to
                                  select new { item, medication };
            if (query.ClientId is { } client) administrations = administrations.Where(row => row.medication.ServiceUserId == client);
            if (query.WorkerId is { } worker) administrations = administrations.Where(row => row.item.CareWorkerId == worker);
            var total = await administrations.CountAsync(token);
            var records = await administrations.OrderBy(row => row.item.ScheduledAt).ThenBy(row => row.item.Id).Skip(skip).Take(take).ToListAsync(token);
            var names = await ClientNames(records.Select(row => row.medication.ServiceUserId).Distinct().ToArray(), token);
            return new(definition.Key, definition.Title, ["Scheduled (UTC)", "Client", "Medication", "Outcome", "Administered (UTC)"], records.Select(row => new[] { row.item.ScheduledAt.ToString("yyyy-MM-dd HH:mm"), names.GetValueOrDefault(row.medication.ServiceUserId, "Unknown"), row.medication.Name, row.item.Outcome, row.item.AdministeredAt?.ToString("yyyy-MM-dd HH:mm") ?? "" }).ToList(), total, query.Page, query.PageSize);
        }
        throw new InvalidOperationException("Unsupported report key.");
    }
    private Task<Dictionary<Guid, string>> WorkerNames(Guid[] ids, CancellationToken token) => db.CareWorkers.AsNoTracking().Where(item => ids.Contains(item.Id) && item.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || item.BranchId == tenant.BranchId)).ToDictionaryAsync(item => item.Id, item => item.FullName, token);
    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    private Task<Dictionary<Guid, string>> ClientNames(Guid[] ids, CancellationToken token) => db.ServiceUsers.AsNoTracking().Where(item => ids.Contains(item.Id) && item.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || item.BranchId == tenant.BranchId)).ToDictionaryAsync(item => item.Id, item => item.FullName, token);
}

public sealed record OperationalReportDefinition(string Key, string Title, string Group, bool Medication);
public sealed record ReportQuery(DateOnly? From = null, DateOnly? To = null, Guid? WorkerId = null, Guid? ClientId = null, int Page = 1, int PageSize = 50, string? Actor = null, string? Action = null, string? EntityType = null);
public sealed record ReportPage(string Key, string Title, string[] Columns, List<string[]> Rows, int Total, int Page, int PageSize);
public sealed class OperationalReportPreset { public Guid Id { get; set; } public string Name { get; set; } = ""; public string ReportKey { get; set; } = ""; public DateOnly? From { get; set; } public DateOnly? To { get; set; } public Guid? WorkerId { get; set; } public Guid? ClientId { get; set; } public string Actor { get; set; } = ""; public string Action { get; set; } = ""; public string EntityType { get; set; } = ""; }
public sealed record SaveOperationalReportPreset(string Name, string ReportKey, DateOnly? From, DateOnly? To, Guid? WorkerId, Guid? ClientId, string? Actor = null, string? Action = null, string? EntityType = null);
public sealed class OperationalDiaryReportRow { public Guid Id { get; set; } public DateTimeOffset EventAt { get; set; } public string Category { get; set; } = ""; public string Priority { get; set; } = ""; public string Status { get; set; } = ""; public string Subject { get; set; } = ""; public string Detail { get; set; } = ""; public Guid? ClientId { get; set; } public Guid? WorkerId { get; set; } }
public sealed class OperationalChecklistReportRow { public Guid Id { get; set; } public DateTimeOffset VisitAt { get; set; } public DateTimeOffset? CompletedAt { get; set; } public Guid ClientId { get; set; } public Guid WorkerId { get; set; } public string Title { get; set; } = ""; public string Category { get; set; } = ""; public string Status { get; set; } = ""; public string Outcome { get; set; } = ""; public string ExceptionReason { get; set; } = ""; }
public sealed class OperationalTrainingMatrixRow { public Guid WorkerId { get; set; } public Guid CourseId { get; set; } public string Code { get; set; } = ""; public string Title { get; set; } = ""; public string Status { get; set; } = ""; public DateTimeOffset? CompletedAt { get; set; } public DateTimeOffset? ExpiresAt { get; set; } public DateTimeOffset? PlannedAt { get; set; } }
public sealed class OperationalTrainingBookingRow { public Guid Id { get; set; } public Guid WorkerId { get; set; } public string Code { get; set; } = ""; public string Title { get; set; } = ""; public string Status { get; set; } = ""; public DateTimeOffset ScheduledAt { get; set; } public DateTimeOffset? CompletedAt { get; set; } public DateTimeOffset? ExpiresAt { get; set; } public string Evidence { get; set; } = ""; }
public sealed class OperationalTimeOffRow { public Guid Id { get; set; } public Guid WorkerId { get; set; } public string Type { get; set; } = ""; public DateTimeOffset StartsAt { get; set; } public DateTimeOffset EndsAt { get; set; } public string Status { get; set; } = ""; public string Notes { get; set; } = ""; public string CoverStatus { get; set; } = ""; public DateTimeOffset CreatedAt { get; set; } }
public sealed class OperationalTimeOffSummaryRow { public Guid WorkerId { get; set; } public string Type { get; set; } = ""; public string Status { get; set; } = ""; public int Periods { get; set; } public decimal Hours { get; set; } }
public sealed class OperationalAvailabilityRow { public Guid Id { get; set; } public Guid WorkerId { get; set; } public int DayOfWeek { get; set; } public TimeOnly StartTime { get; set; } public TimeOnly EndTime { get; set; } public bool IsAvailable { get; set; } public DateOnly EffectiveFrom { get; set; } public DateOnly? EffectiveTo { get; set; } public string Notes { get; set; } = ""; }
public sealed class OperationalWorkerContactRow { public Guid WorkerId { get; set; } public string Name { get; set; } = ""; public string Specialization { get; set; } = ""; public string AccountEmail { get; set; } = ""; }
public sealed class OperationalWorkerProfileRow { public Guid WorkerId { get; set; } public string Name { get; set; } = ""; public string Specialization { get; set; } = ""; public string EmploymentStatus { get; set; } = ""; public string JobTitle { get; set; } = ""; public string ContractType { get; set; } = ""; public int ContractedWeeklyMinutes { get; set; } public DateOnly? StartDate { get; set; } public DateOnly? EndDate { get; set; } public string LineManager { get; set; } = ""; }
public sealed class OperationalWorkerLeaverRow { public Guid WorkerId { get; set; } public string Name { get; set; } = ""; public string JobTitle { get; set; } = ""; public DateOnly EndDate { get; set; } public string Reason { get; set; } = ""; public DateTimeOffset UpdatedAt { get; set; } }
public sealed class OperationalClientContactRow { public Guid ClientId { get; set; } public string ClientName { get; set; } = ""; public string ClientPhone { get; set; } = ""; public string ContactType { get; set; } = ""; public string ContactName { get; set; } = ""; public string Relationship { get; set; } = ""; public string ContactPhone { get; set; } = ""; public string ContactEmail { get; set; } = ""; public bool IsPrimary { get; set; } public bool IsEmergency { get; set; } public Guid? ContactId { get; set; } }
public sealed class OperationalFinanceExceptionRow { public Guid VisitId { get; set; } public DateTimeOffset StartsAt { get; set; } public Guid ClientId { get; set; } public Guid WorkerId { get; set; } public string VisitType { get; set; } = ""; public int DurationMinutes { get; set; } public string BillableStatus { get; set; } = ""; public string Reason { get; set; } = ""; }
public sealed class OperationalFinanceStatsRow { public DateOnly InvoiceDate { get; set; } public string InvoiceStatus { get; set; } = ""; public string Currency { get; set; } = ""; public int Invoices { get; set; } public decimal NetAmount { get; set; } public decimal VatAmount { get; set; } public decimal GrossAmount { get; set; } }
public sealed class OperationalClientFinanceStatsRow { public Guid ClientId { get; set; } public string InvoiceStatus { get; set; } = ""; public string Currency { get; set; } = ""; public int Invoices { get; set; } public int Lines { get; set; } public decimal NetLineAmount { get; set; } }
