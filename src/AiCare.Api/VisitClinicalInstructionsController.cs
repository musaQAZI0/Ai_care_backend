using System.Data;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[ApiController]
[Authorize(Roles = "CareWorker,CareCoordinator,CareManager,Administrator")]
[Route("api/phase1/visits/{visitId:guid}/clinical-instructions")]
public sealed class VisitClinicalInstructionsController(CareDbContext db, ITenantContext tenant,
    ICurrentUserContext user, IContextualAuthorization authorization) : ControllerBase
{
    [HttpGet]
    [ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
    public async Task<IActionResult> Get(Guid visitId, CancellationToken token)
    {
        if (!await authorization.CanReadVisitAsync(visitId, token)) return NotFound();
        var visit = await db.Visits.AsNoTracking().SingleAsync(item => item.Id == visitId, token);
        var person = await db.ServiceUsers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == visit.ServiceUserId
            && item.OrganizationId == tenant.OrganizationId && item.BranchId == visit.BranchId, token);
        if (person is null) return NotFound();

        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select 'Assessment' kind,id,assessment_type title,version,summary,recommended_actions instructions,
                   '' contingency,review_due_at
            from governed_assessments
            where service_user_id=@person and organization_id=@organization and branch_id=@branch
              and status='Current' and approved_at is not null and signed_at is not null
            union all
            select 'Risk',id,category,version,hazard,controls,contingency,review_due_at
            from governed_risk_assessments
            where service_user_id=@person and organization_id=@organization and branch_id=@branch
              and status='Current' and approved_at is not null and signed_at is not null
            order by kind,title
            """;
        foreach (var pair in new[] { ("person", person.Id), ("organization", tenant.OrganizationId), ("branch", visit.BranchId!.Value) })
        {
            var parameter = command.CreateParameter(); parameter.ParameterName = pair.Item1; parameter.Value = pair.Item2;
            command.Parameters.Add(parameter);
        }
        var instructions = new List<VisitClinicalInstruction>();
        var generatedAt = DateTimeOffset.UtcNow;
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                var due = reader.GetFieldValue<DateTimeOffset>(7);
                instructions.Add(new(reader.GetString(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt32(3),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6), due, due <= generatedAt));
            }
        db.AuditEvents.Add(new(Guid.NewGuid(), "visit.clinical_instructions_viewed", user.UserName,
            nameof(Visit), visit.Id, generatedAt, visit.OrganizationId, visit.BranchId));
        await db.SaveChangesAsync(token);
        return Ok(new { visitId, serviceUserId = person.Id, generatedAt, instructions });
    }
}

public sealed record VisitClinicalInstruction(string Kind, Guid Id, string Title, int Version, string Summary,
    string Instructions, string Contingency, DateTimeOffset ReviewDueAt, bool ReviewOverdue);
