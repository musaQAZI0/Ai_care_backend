using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

public sealed partial class BillingContractsController
{
    [HttpGet("multi-worker-policies")]
    public async Task<IActionResult> MultiWorkerPolicies(CancellationToken t)
    {
        var rows = new List<JsonElement>();
        await using var c = await Command("select row_to_json(p)::text from finance_multi_worker_policies p where organization_id=@org and (@wide or branch_id=@branch) order by created_at desc", t);
        await using var reader = await c.ExecuteReaderAsync(t);
        while (await reader.ReadAsync(t)) rows.Add(JsonSerializer.Deserialize<JsonElement>(reader.GetString(0)));
        return Ok(rows);
    }

    [HttpPost("multi-worker-policies")]
    public Task<IActionResult> CreateMultiWorkerPolicy(MultiWorkerBillingPolicyRequest r, CancellationToken t) => Transaction(async () =>
    {
        if (!user.IsAdministrator && !user.IsBackOffice) return Forbid();
        if (user.UserId is null) return Unauthorized();
        if (r.RequestId == Guid.Empty || r.BillingUnit != "PerVisit" || string.IsNullOrWhiteSpace(r.Reference))
            return BadRequest(new { message = "Only an explicitly authorised PerVisit crew policy is supported. Per-worker charging requires separate approved source allocation." });
        await using var replay = await Command("select contract_version_id=@contract and reference=@reference and billing_unit=@unit and created_by=@actor matches from finance_multi_worker_policies where id=@id and organization_id=@org and (@wide or branch_id=@branch)", t);
        Add(replay, "id", r.RequestId); Add(replay, "contract", r.ContractVersionId); Add(replay, "reference", r.Reference.Trim()); Add(replay, "unit", r.BillingUnit);
        if (await replay.ExecuteScalarAsync(t) is bool matches) return matches ? Ok(new { id = r.RequestId, replayed = true }) : Conflict(new { message = "Crew policy ID already has different details or creator." });
        await using var create = await Command("""
            insert into finance_multi_worker_policies(id,contract_version_id,organization_id,branch_id,billing_unit,reference,created_by)
            select @id,id,organization_id,branch_id,@unit,@reference,@actor from finance_contract_versions
            where id=@contract and organization_id=@org and (@wide or branch_id=@branch) and status='Approved' returning branch_id
            """, t);
        Add(create, "id", r.RequestId); Add(create, "contract", r.ContractVersionId); Add(create, "unit", r.BillingUnit); Add(create, "reference", r.Reference.Trim());
        if (await create.ExecuteScalarAsync(t) is not Guid branch) return NotFound();
        Audit("MULTI_WORKER_POLICY_CREATED", r.RequestId, branch); await db.SaveChangesAsync(t);
        return Created("", new { id = r.RequestId });
    }, t);

    [HttpPost("multi-worker-policies/{id:guid}/approve")]
    public Task<IActionResult> ApproveMultiWorkerPolicy(Guid id, CancellationToken t) => Transaction(async () =>
    {
        if (!user.IsAdministrator && !user.IsBackOffice) return Forbid();
        await using var c = await Command("update finance_multi_worker_policies set approved_by=@actor,approved_at=now() where id=@id and organization_id=@org and (@wide or branch_id=@branch) and approved_by is null and created_by<>@actor returning branch_id", t);
        Add(c, "id", id);
        if (await c.ExecuteScalarAsync(t) is not Guid branch) return Conflict(new { message = "A different finance reviewer must approve the crew policy." });
        Audit("MULTI_WORKER_POLICY_APPROVED", id, branch); await db.SaveChangesAsync(t);
        return Ok(new { id });
    }, t);
}

public sealed record MultiWorkerBillingPolicyRequest(Guid RequestId, Guid ContractVersionId, string BillingUnit, string Reference);
