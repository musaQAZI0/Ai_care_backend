using System.Data;
using System.Globalization;
using System.Text.Json;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace AiCare.Api;

[ApiController]
[Authorize(Policy = "Phase1User")]
[Route("api/phase1/emar-safety/expected-slots")]
public sealed class ExpectedMedicationSlotsController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user) : ControllerBase
{
    private sealed record Order(Guid MedicationId, string Name, int Version,
        DateTimeOffset? Start, DateTimeOffset? End, string[] Times, DateTimeOffset? VerifiedAt,
        DateTimeOffset? PendingChangeAt, int Window);
    private sealed record Mar(Guid Id, Guid MedicationId, int? Version, DateTimeOffset ScheduledAt, string Outcome);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid personId, [FromQuery] DateOnly from,
        [FromQuery] DateOnly to, [FromQuery] Guid? branchId, CancellationToken token)
    {
        if (!user.HasAnyRole(UserRole.Administrator, UserRole.CareManager, UserRole.CareCoordinator)) return Forbid();
        if (personId == Guid.Empty || from <= DateOnly.MinValue.AddDays(1) || to >= DateOnly.MaxValue.AddDays(-2) || to < from || to.DayNumber - from.DayNumber > 30)
            return BadRequest(new { message = "Choose a person and a UK date range of at most 31 days." });
        if (!tenant.CanAccess(tenant.OrganizationId, branchId)) return NotFound();
        return await db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
            var result = await BuildReport(personId, from, to, branchId, token);
            await transaction.CommitAsync(token);
            return result;
        });
    }

    private async Task<IActionResult> BuildReport(Guid personId, DateOnly from, DateOnly to, Guid? branchId, CancellationToken token)
    {
        var selectedBranch = branchId ?? (tenant.IsOrganizationWide ? null : tenant.BranchId);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var orders = new List<Order>();
        var mar = new List<Mar>();
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(token);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = """
                select m."Id",m."Name",p.profile_version,
                       p.start_date,p.end_date,p.scheduled_local_times::text,p.reviewed_at,
                       (select min(c.effective_at) from medication_change_requests c
                        where c.medication_id=m."Id" and c.organization_id=m."OrganizationId"
                          and c.branch_id=m."BranchId" and c.status in ('Pending','Approved')),
                       p.dose_window_minutes
                from "Medications" m join medication_safety_profiles p on p.medication_id=m."Id"
                  and p.organization_id=m."OrganizationId" and p.branch_id=m."BranchId"
                where m."ServiceUserId"=@person and m."OrganizationId"=@organization
                  and (@branch is null or m."BranchId"=@branch) and m."IsPrn"=false
                  and p.reconciliation_status='Verified'
                order by m."Name",m."Id"
                """;
            Bind(command, personId, selectedBranch);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) orders.Add(new Order(reader.GetGuid(0), reader.GetString(1),
                reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                JsonSerializer.Deserialize<string[]>(reader.GetString(5)) ?? [],
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7), reader.GetInt32(8)));
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = """
                select r."Id",r."MedicationId",r."MedicationProfileVersion",r."ScheduledAt",r."Outcome"
                from "MedicationAdministrationRecords" r join "Medications" m on m."Id"=r."MedicationId"
                  and m."OrganizationId"=r."OrganizationId" and m."BranchId"=r."BranchId"
                where m."ServiceUserId"=@person and r."OrganizationId"=@organization
                  and (@branch is null or r."BranchId"=@branch)
                  and r."ScheduledAt">=@fromInstant and r."ScheduledAt"<@toInstant
                """;
            Bind(command, personId, selectedBranch);
            command.Parameters.Add(new NpgsqlParameter("fromInstant", NpgsqlDbType.TimestampTz)
                { Value = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).AddDays(-1) });
            command.Parameters.Add(new NpgsqlParameter("toInstant", NpgsqlDbType.TimestampTz)
                { Value = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).AddDays(1) });
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) mar.Add(new Mar(reader.GetGuid(0), reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2), reader.GetFieldValue<DateTimeOffset>(3), reader.GetString(4)));
        }

        var slots = new List<object>();
        var unstructured = new List<object>();
        var now = DateTimeOffset.UtcNow;
        foreach (var order in orders)
        {
            if (order.VerifiedAt is null)
            {
                unstructured.Add(new { medicationId = order.MedicationId, medicationName = order.Name,
                    profileVersion = order.Version, reason = "Verification time is missing; verify the original order before relying on expected slots." });
                continue;
            }
            if (!MedicationDoseTimes.TryNormalize(order.Times, out var times) || times.Length == 0)
            {
                unstructured.Add(new { medicationId = order.MedicationId, medicationName = order.Name,
                    profileVersion = order.Version, reason = "Fixed daily dose times have not been approved for this medication." });
                continue;
            }
            for (var day = from; day <= to; day = day.AddDays(1))
            foreach (var time in times)
            {
                var local = day.ToDateTime(TimeOnly.ParseExact(time, "HH:mm", CultureInfo.InvariantCulture), DateTimeKind.Unspecified);
                DateTimeOffset[] candidates;
                if (zone.IsInvalidTime(local)) candidates = [];
                else if (zone.IsAmbiguousTime(local)) candidates = zone.GetAmbiguousTimeOffsets(local)
                    .Select(offset => new DateTimeOffset(local, offset).ToUniversalTime()).Order().ToArray();
                else candidates = [new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime()];
                bool Active(DateTimeOffset candidate) => candidate >= order.VerifiedAt &&
                    (order.Start is null || candidate >= order.Start) && (order.End is null || candidate <= order.End) &&
                    (order.PendingChangeAt is null || candidate < order.PendingChangeAt);
                if (candidates.Length > 0 && !candidates.Any(Active)) continue;
                if (candidates.Length > 0) candidates = candidates.Where(Active).ToArray();
                if (candidates.Length == 0)
                {
                    // A skipped hour has no UTC instant. Keep the clock exception only on an active order day.
                    var dayStart = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), zone));
                    var dayEnd = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(day.AddDays(1).ToDateTime(TimeOnly.MinValue), zone));
                    if (dayEnd <= order.VerifiedAt || order.Start >= dayEnd || order.End < dayStart || order.PendingChangeAt <= dayStart) continue;
                    if (local < TimeZoneInfo.ConvertTime(order.VerifiedAt.Value, zone).DateTime ||
                        order.Start is { } start && local < TimeZoneInfo.ConvertTime(start, zone).DateTime ||
                        order.End is { } end && local > TimeZoneInfo.ConvertTime(end, zone).DateTime ||
                        order.PendingChangeAt is { } pending && local >= TimeZoneInfo.ConvertTime(pending, zone).DateTime) continue;
                }
                var matching = mar.Where(record => record.MedicationId == order.MedicationId &&
                    candidates.Contains(record.ScheduledAt.ToUniversalTime())).ToArray();
                var current = matching.Where(record => record.Version == order.Version).ToArray();
                var state = candidates.Length != 1 && current.Length == 0 ? "ClockReviewRequired" :
                    current.Length > 1 ? "Duplicate" : current.Length == 1 ?
                        current[0].Outcome == "Scheduled" ? "Scheduled" : "Recorded" :
                    candidates[0].AddMinutes(order.Window) < now ? "Missing" : "Unscheduled";
                slots.Add(new { key = $"{order.MedicationId}:{order.Version}:{day:yyyy-MM-dd}:{time}",
                    medicationId = order.MedicationId, medicationName = order.Name, profileVersion = order.Version,
                    ukDate = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), localTime = time,
                    candidateInstants = candidates, status = state,
                    marIds = current.Select(record => record.Id).ToArray(),
                    staleMarIds = matching.Where(record => record.Version != order.Version).Select(record => record.Id).ToArray() });
            }
        }
        return Ok(new { personId, from, to, basis = "CurrentApprovedOrder", generatedAt = now,
            slots, unstructuredMedications = unstructured });
    }

    private void Bind(System.Data.Common.DbCommand command, Guid personId, Guid? branchId)
    {
        command.Parameters.Add(new NpgsqlParameter("person", NpgsqlDbType.Uuid) { Value = personId });
        command.Parameters.Add(new NpgsqlParameter("organization", NpgsqlDbType.Uuid) { Value = tenant.OrganizationId });
        command.Parameters.Add(new NpgsqlParameter("branch", NpgsqlDbType.Uuid)
            { Value = branchId.HasValue ? branchId.Value : DBNull.Value });
    }
}
