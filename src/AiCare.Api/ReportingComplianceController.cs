using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AiCare.Api;

[ApiController]
[Authorize(Roles = "Administrator,BackOffice,CareManager,CareCoordinator")]
[Route("api/phase1/reporting-compliance")]
public sealed class ReportingComplianceController(
    CareDbContext db,
    ITenantContext tenant,
    ICurrentUserContext user) : ControllerBase
{
    private static readonly HashSet<string> SupportedMetrics = new(StringComparer.OrdinalIgnoreCase)
    {
        "Service users",
        "Completed visits",
        "Open incidents",
        "Invoice total",
        "Audit events"
    };

    private static readonly HashSet<string> EvidenceTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Record",
        "Report",
        "Document",
        "Audit",
        "Policy",
        "Certificate"
    };

    private static readonly HashSet<string> EvidenceStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Draft",
        "Ready",
        "Archived"
    };

    private static readonly HashSet<string> ActionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Evidence review",
        "Remediation",
        "Escalation",
        "Review",
        "Follow-up"
    };

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard(CancellationToken cancellationToken) => Ok(new
    {
        generatedReports = await Scalar("select count(*) from report_runs where organization_id=@organization and (@organizationWide or branch_id=@branch)", cancellationToken),
        openEvidence = await Scalar("select count(*) from compliance_evidence_items where organization_id=@organization and (@organizationWide or branch_id=@branch) and status<>'Archived'", cancellationToken),
        overdueEvidence = await Scalar("select count(*) from compliance_evidence_items where organization_id=@organization and (@organizationWide or branch_id=@branch) and status<>'Archived' and review_due_at<now()", cancellationToken),
        openActions = await Scalar("select count(*) from compliance_actions where organization_id=@organization and (@organizationWide or branch_id=@branch) and status='Open'", cancellationToken),
        overdueActions = await Scalar("select count(*) from compliance_actions where organization_id=@organization and (@organizationWide or branch_id=@branch) and status='Open' and due_at<now()", cancellationToken),
        recentReports = await Reports(cancellationToken),
        evidence = await Evidence(cancellationToken),
        actions = await Actions(cancellationToken)
    });

    [HttpPost("report-runs")]
    public async Task<IActionResult> RunReport(RunGovernedReportRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Metrics is null || request.Metrics.Count == 0)
        {
            return BadRequest(new { message = "Report name and at least one metric are required." });
        }

        var requestedMetrics = request.Metrics
            .Select(metric => metric?.Trim() ?? string.Empty)
            .Where(metric => metric.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var unsupported = requestedMetrics.Where(metric => !SupportedMetrics.Contains(metric)).ToArray();
        if (requestedMetrics.Length == 0 || unsupported.Length > 0)
        {
            return BadRequest(new
            {
                message = unsupported.Length == 0
                    ? "At least one supported metric is required."
                    : $"Unsupported report metrics: {string.Join(", ", unsupported)}."
            });
        }

        if (!TryParseFilters(request.Filters, out var range, out var filterError))
        {
            return BadRequest(new { message = filterError });
        }

        if (request.ReportDefinitionId is Guid definition &&
            !await ReportDefinitionAllowed(definition, cancellationToken))
        {
            return NotFound();
        }

        if (request.ReportCatalogueId is Guid catalogue &&
            !await CatalogueAllowed(catalogue, cancellationToken))
        {
            return NotFound();
        }

        var metrics = await BuildMetrics(requestedMetrics, range, cancellationToken);
        var id = Guid.NewGuid();
        await ExecuteInTransaction(async () =>
        {
        await Exec(
            "insert into report_runs(id,report_definition_id,report_catalogue_id,organization_id,branch_id,name,category,format,status,filters_json,metrics_json,generated_by) values(@id,@definition,@catalogue,@organization,@branch,@name,@category,@format,'Generated',cast(@filters as jsonb),cast(@metrics as jsonb),@actor)",
            command =>
            {
                Add(command, "id", id);
                Add(command, "definition", request.ReportDefinitionId);
                Add(command, "catalogue", request.ReportCatalogueId);
                Add(command, "name", request.Name.Trim());
                Add(command, "category", Clean(request.Category, "Operational"));
                Add(command, "format", Clean(request.Format, "JSON"));
                Add(command, "filters", JsonSerializer.Serialize(request.Filters ?? new Dictionary<string, string>()));
                Add(command, "metrics", JsonSerializer.Serialize(metrics));
            },
            cancellationToken);
        Audit("report_run.generated", "ReportRun", id);
        await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

        return Created(
            $"/api/phase1/reporting-compliance/report-runs/{id}",
            new { id, request.Name, metrics, filters = request.Filters, generatedAt = DateTimeOffset.UtcNow });
    }

    [HttpGet("report-runs/{id:guid}/csv")]
    public async Task<IActionResult> ReportCsv(Guid id, CancellationToken cancellationToken)
    {
        var rows = await Query(
            "select name,metrics_json from report_runs where id=@id and organization_id=@organization and (@organizationWide or branch_id=@branch)",
            command => Add(command, "id", id),
            reader => new { Name = reader.GetString(0), Metrics = reader.GetString(1) },
            cancellationToken);
        if (rows.Count == 0)
        {
            return NotFound();
        }

        var metrics = JsonSerializer.Deserialize<Dictionary<string, decimal>>(rows[0].Metrics) ?? new();
        var csv = new StringBuilder("metric,value\n");
        foreach (var item in metrics)
        {
            csv.Append(item.Key).Append(',').Append(item.Value).Append('\n');
        }

        Audit("report_run.exported", "ReportRun", id);
        await db.SaveChangesAsync(cancellationToken);
        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"{rows[0].Name}.csv");
    }

    [HttpPost("evidence")]
    public async Task<IActionResult> AddEvidence(CreateComplianceEvidenceRequest request, CancellationToken cancellationToken)
    {
        var evidenceType = Clean(request.EvidenceType, "Record");
        var status = Clean(request.Status, "Ready");
        if (string.IsNullOrWhiteSpace(request.Domain) ||
            string.IsNullOrWhiteSpace(request.Requirement) ||
            string.IsNullOrWhiteSpace(request.EvidenceReference) ||
            !EvidenceTypes.Contains(evidenceType) ||
            !EvidenceStatuses.Contains(status) ||
            !ValidGovernanceDate(request.ReviewDueAt))
        {
            return BadRequest(new { message = "Domain, requirement, reference, valid evidence type/status, and a reasonable review date are required." });
        }

        var id = Guid.NewGuid();
        await ExecuteInTransaction(async () =>
        {
        await Exec(
            "insert into compliance_evidence_items(id,organization_id,branch_id,domain,requirement,evidence_type,evidence_reference,status,owner,review_due_at,notes,created_by) values(@id,@organization,@branch,@domain,@requirement,@type,@reference,@status,@owner,@due,@notes,@actor)",
            command =>
            {
                Add(command, "id", id);
                Add(command, "domain", request.Domain.Trim());
                Add(command, "requirement", request.Requirement.Trim());
                Add(command, "type", evidenceType);
                Add(command, "reference", request.EvidenceReference.Trim());
                Add(command, "status", status);
                Add(command, "owner", string.IsNullOrWhiteSpace(request.Owner) ? user.UserName : request.Owner.Trim());
                Add(command, "due", request.ReviewDueAt);
                Add(command, "notes", request.Notes?.Trim() ?? string.Empty);
            },
            cancellationToken);
        Audit("compliance_evidence.created", "ComplianceEvidence", id);
        await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
        return Created($"/api/phase1/reporting-compliance/evidence/{id}", new { id });
    }

    [HttpPost("actions")]
    public async Task<IActionResult> AddAction(CreateComplianceActionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ActionType) ||
            string.IsNullOrWhiteSpace(request.Detail) ||
            !ActionTypes.Contains(request.ActionType.Trim()) ||
            !ValidGovernanceDate(request.DueAt))
        {
            return BadRequest(new { message = "A valid action type, detail, and reasonable due date are required." });
        }

        if (request.EvidenceId is Guid evidenceId && !await EvidenceAllowed(evidenceId, cancellationToken))
        {
            return NotFound();
        }

        var id = Guid.NewGuid();
        await ExecuteInTransaction(async () =>
        {
        await Exec(
            "insert into compliance_actions(id,organization_id,branch_id,evidence_id,action_type,detail,owner,status,due_at,created_by) values(@id,@organization,@branch,@evidence,@type,@detail,@owner,'Open',@due,@actor)",
            command =>
            {
                Add(command, "id", id);
                Add(command, "evidence", request.EvidenceId);
                Add(command, "type", request.ActionType.Trim());
                Add(command, "detail", request.Detail.Trim());
                Add(command, "owner", string.IsNullOrWhiteSpace(request.Owner) ? user.UserName : request.Owner.Trim());
                Add(command, "due", request.DueAt);
            },
            cancellationToken);
        Audit("compliance_action.created", "ComplianceAction", id);
        await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
        return Created($"/api/phase1/reporting-compliance/actions/{id}", new { id });
    }

    [HttpPatch("actions/{id:guid}")]
    public async Task<IActionResult> CompleteAction(Guid id, CompleteComplianceActionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Outcome))
        {
            return BadRequest(new { message = "Action outcome is required." });
        }

        var changed = 0;
        await ExecuteInTransaction(async () =>
        {
            changed = await Exec(
                "update compliance_actions set status='Completed',completed_at=now(),detail=detail||@outcomeLine where id=@id and organization_id=@organization and (@organizationWide or branch_id=@branch) and status='Open'",
                command =>
                {
                    Add(command, "id", id);
                    Add(command, "outcomeLine", Environment.NewLine + "Outcome: " + request.Outcome.Trim());
                },
                cancellationToken);
            if (changed > 0)
            {
                Audit("compliance_action.completed", "ComplianceAction", id);
                await db.SaveChangesAsync(cancellationToken);
            }
        }, cancellationToken);

        return changed == 0 ? NotFound() : NoContent();
    }
    [HttpGet("evidence-pack.csv")]
    public async Task<IActionResult> EvidencePack(CancellationToken cancellationToken)
    {
        var rows = await Evidence(cancellationToken);
        var csv = new StringBuilder("domain,requirement,evidence_type,evidence_reference,status,owner,review_due_at\n");
        foreach (var evidence in rows)
        {
            csv.Append(Escape(evidence.Domain)).Append(',')
                .Append(Escape(evidence.Requirement)).Append(',')
                .Append(Escape(evidence.EvidenceType)).Append(',')
                .Append(Escape(evidence.EvidenceReference)).Append(',')
                .Append(Escape(evidence.Status)).Append(',')
                .Append(Escape(evidence.Owner)).Append(',')
                .Append(evidence.ReviewDueAt?.ToString("O") ?? string.Empty)
                .Append('\n');
        }

        Audit("compliance_evidence_pack.exported", "ComplianceEvidence", null);
        await db.SaveChangesAsync(cancellationToken);
        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", "compliance-evidence-pack.csv");
    }

    private async Task<bool> CatalogueAllowed(Guid id, CancellationToken cancellationToken)
    {
        await using var command = await Command(
            "select exists(select 1 from governed_report_catalogue where id=@id and organization_id=@organization and active=true and (@organizationWide or branch_id is null or branch_id=@branch) and @role=any(allowed_roles))",
            cancellationToken);
        Add(command, "id", id);
        Add(command, "role", user.Role?.ToString() ?? string.Empty);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<bool> ReportDefinitionAllowed(Guid id, CancellationToken cancellationToken)
    {
        await using var command = await Command(
            "select exists(select 1 from \"Reports\" where \"Id\"=@id and \"OrganizationId\"=@organization and (@organizationWide or \"BranchId\" is null or \"BranchId\"=@branch))",
            cancellationToken);
        Add(command, "id", id);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    private Task<bool> EvidenceAllowed(Guid id, CancellationToken cancellationToken) =>
        Exists(
            "select exists(select 1 from compliance_evidence_items where id=@id and organization_id=@organization and (@organizationWide or branch_id=@branch))",
            command => Add(command, "id", id),
            cancellationToken);

    private async Task<Dictionary<string, decimal>> BuildMetrics(
        IReadOnlyCollection<string> metrics,
        ReportDateRange? range,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var metric in metrics)
        {
            var key = SupportedMetrics.Single(candidate => candidate.Equals(metric, StringComparison.OrdinalIgnoreCase));
            result[key] = key switch
            {
                "Service users" => await Scalar(
                    "select count(*) from \"ServiceUsers\" where \"OrganizationId\"=@organization and (@organizationWide or \"BranchId\"=@branch)",
                    cancellationToken),
                "Completed visits" => await Scalar(
                    "select count(*) from \"Visits\" where \"OrganizationId\"=@organization and (@organizationWide or \"BranchId\"=@branch) and \"Status\"='Completed'" + DatePredicate("\"StartsAt\"", range),
                    command => BindRange(command, range),
                    cancellationToken),
                "Open incidents" => await Scalar(
                    "select count(*) from \"Incidents\" where \"OrganizationId\"=@organization and (@organizationWide or \"BranchId\"=@branch) and \"Status\"<>'Closed'" + DatePredicate("\"ReportedAt\"", range),
                    command => BindRange(command, range),
                    cancellationToken),
                "Invoice total" => await Money(
                    "select coalesce(sum(\"Amount\"),0) from \"Invoices\" where \"OrganizationId\"=@organization and (@organizationWide or \"BranchId\"=@branch) and \"Status\"<>'Void'" + DatePredicate("\"IssuedAt\"", range),
                    command => BindRange(command, range),
                    cancellationToken),
                "Audit events" => await Scalar(
                    "select count(*) from \"AuditEvents\" where \"OrganizationId\"=@organization and (@organizationWide or \"BranchId\"=@branch)" + DatePredicate("\"CreatedAt\"", range),
                    command => BindRange(command, range),
                    cancellationToken),
                _ => throw new InvalidOperationException("Metric validation and execution are out of sync.")
            };
        }

        return result;
    }

    private static bool TryParseFilters(
        IReadOnlyDictionary<string, string>? filters,
        out ReportDateRange? range,
        out string error)
    {
        range = null;
        error = string.Empty;
        if (filters is null || filters.Count == 0)
        {
            return true;
        }

        var normalized = new Dictionary<string, string>(filters, StringComparer.OrdinalIgnoreCase);
        var unsupported = normalized.Keys.Where(key => key is not ("period" or "from" or "to")).ToArray();
        if (unsupported.Length > 0)
        {
            error = $"Unsupported report filters: {string.Join(", ", unsupported)}.";
            return false;
        }

        var hasPeriod = normalized.TryGetValue("period", out var period) && !string.IsNullOrWhiteSpace(period);
        var hasBounds = normalized.ContainsKey("from") || normalized.ContainsKey("to");
        if (hasPeriod && hasBounds)
        {
            error = "Use either period or from/to report filters.";
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (hasPeriod)
        {
            range = period!.Trim().ToLowerInvariant() switch
            {
                "last 7 days" => new ReportDateRange(now.AddDays(-7), now),
                "last 30 days" => new ReportDateRange(now.AddDays(-30), now),
                "this month" => new ReportDateRange(new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero), now),
                "all time" => null,
                _ => null
            };
            if (range is null && !period.Trim().Equals("all time", StringComparison.OrdinalIgnoreCase))
            {
                error = "Period must be Last 7 days, Last 30 days, This month, or All time.";
                return false;
            }

            return true;
        }

        if (!normalized.TryGetValue("from", out var fromText) ||
            !normalized.TryGetValue("to", out var toText) ||
            !DateTimeOffset.TryParse(fromText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var from) ||
            !DateTimeOffset.TryParse(toText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var to) ||
            to <= from)
        {
            error = "Custom report filters require valid from and to timestamps with to later than from.";
            return false;
        }

        range = new ReportDateRange(from.ToUniversalTime(), to.ToUniversalTime());
        return true;
    }

    private static string DatePredicate(string column, ReportDateRange? range) =>
        range is null ? string.Empty : $" and {column}>=@dateFrom and {column}<@dateTo";

    private static void BindRange(DbCommand command, ReportDateRange? range)
    {
        if (range is null)
        {
            return;
        }

        Add(command, "dateFrom", range.From);
        Add(command, "dateTo", range.To);
    }

    private static bool ValidGovernanceDate(DateTimeOffset? value)
    {
        if (value is null)
        {
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        return value >= now.AddYears(-10) && value <= now.AddYears(10);
    }

    private Task<List<ReportRunResponse>> Reports(CancellationToken cancellationToken) =>
        Query(
            "select id,name,category,format,status,generated_by,generated_at from report_runs where organization_id=@organization and (@organizationWide or branch_id=@branch) order by generated_at desc limit 20",
            _ => { },
            reader => new ReportRunResponse(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6)),
            cancellationToken);

    private Task<List<ComplianceEvidenceResponse>> Evidence(CancellationToken cancellationToken) =>
        Query(
            "select id,domain,requirement,evidence_type,evidence_reference,status,owner,review_due_at,notes,created_at from compliance_evidence_items where organization_id=@organization and (@organizationWide or branch_id=@branch) order by created_at desc limit 100",
            _ => { },
            reader => new ComplianceEvidenceResponse(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7), reader.GetString(8), reader.GetFieldValue<DateTimeOffset>(9)),
            cancellationToken);

    private Task<List<ComplianceActionResponse>> Actions(CancellationToken cancellationToken) =>
        Query(
            "select id,evidence_id,action_type,detail,owner,status,due_at,completed_at,created_at from compliance_actions where organization_id=@organization and (@organizationWide or branch_id=@branch) order by created_at desc limit 100",
            _ => { },
            reader => new ComplianceActionResponse(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6), reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7), reader.GetFieldValue<DateTimeOffset>(8)),
            cancellationToken);

    private Task<int> Scalar(string sql, CancellationToken cancellationToken) =>
        Scalar(sql, _ => { }, cancellationToken);

    private async Task<int> Scalar(string sql, Action<DbCommand> bind, CancellationToken cancellationToken)
    {
        await using var command = await Command(sql, cancellationToken);
        bind(command);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<decimal> Money(string sql, Action<DbCommand> bind, CancellationToken cancellationToken)
    {
        await using var command = await Command(sql, cancellationToken);
        bind(command);
        return Convert.ToDecimal(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<bool> Exists(string sql, Action<DbCommand> bind, CancellationToken cancellationToken)
    {
        await using var command = await Command(sql, cancellationToken);
        bind(command);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<int> Exec(string sql, Action<DbCommand> bind, CancellationToken cancellationToken)
    {
        await using var command = await Command(sql, cancellationToken);
        bind(command);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<List<T>> Query<T>(
        string sql,
        Action<DbCommand> bind,
        Func<DbDataReader, T> map,
        CancellationToken cancellationToken)
    {
        var results = new List<T>();
        await using var command = await Command(sql, cancellationToken);
        bind(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(map(reader));
        }

        return results;
    }

    private async Task ExecuteInTransaction(Func<Task> operation, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await operation();
            await transaction.CommitAsync(cancellationToken);
        });
    }

    private async Task<DbCommand> Command(string sql, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        Add(command, "organization", tenant.OrganizationId);
        Add(command, "branch", tenant.BranchId ?? TenantDefaults.BranchId);
        Add(command, "organizationWide", tenant.IsOrganizationWide);
        Add(command, "actor", user.UserName);
        return command;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        if (command.Parameters.Contains(name))
        {
            return;
        }

        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private void Audit(string action, string entity, Guid? id) =>
        db.AuditEvents.Add(new AuditEvent(
            Guid.NewGuid(),
            action,
            user.UserName,
            entity,
            id,
            DateTimeOffset.UtcNow,
            tenant.OrganizationId,
            tenant.BranchId ?? TenantDefaults.BranchId));

    private static string Clean(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string Escape(string value) =>
        value.Contains(',') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    private sealed record ReportDateRange(DateTimeOffset From, DateTimeOffset To);
}

public sealed record RunGovernedReportRequest(
    Guid? ReportDefinitionId,
    string Name,
    string Category,
    string Format,
    IReadOnlyCollection<string> Metrics,
    Dictionary<string, string> Filters,
    Guid? ReportCatalogueId = null);

public sealed record CreateComplianceEvidenceRequest(
    string Domain,
    string Requirement,
    string EvidenceType,
    string EvidenceReference,
    string Status,
    string? Owner,
    DateTimeOffset? ReviewDueAt,
    string? Notes);

public sealed record CreateComplianceActionRequest(
    Guid? EvidenceId,
    string ActionType,
    string Detail,
    string? Owner,
    DateTimeOffset? DueAt);

public sealed record CompleteComplianceActionRequest(string Outcome);
public sealed record ReportRunResponse(Guid Id, string Name, string Category, string Format, string Status, string GeneratedBy, DateTimeOffset GeneratedAt);
public sealed record ComplianceEvidenceResponse(Guid Id, string Domain, string Requirement, string EvidenceType, string EvidenceReference, string Status, string Owner, DateTimeOffset? ReviewDueAt, string Notes, DateTimeOffset CreatedAt);
public sealed record ComplianceActionResponse(Guid Id, Guid? EvidenceId, string ActionType, string Detail, string Owner, string Status, DateTimeOffset? DueAt, DateTimeOffset? CompletedAt, DateTimeOffset CreatedAt);
