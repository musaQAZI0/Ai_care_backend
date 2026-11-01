using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

public sealed partial class WorkforceFinanceController
{
    bool RechargeManager => user.IsAdministrator || user.IsBackOffice;

    [HttpGet("recharge-policies")]
    public async Task<IActionResult> RechargePolicies(CancellationToken t)
    {
        if (!RechargeManager) return Forbid();
        return Ok(await Rows("select * from finance_recharge_policies where organization_id=@org and (@wide or branch_id=@branch) order by created_at desc", _ => { }, t));
    }

    [HttpPost("recharge-policies")]
    public Task<IActionResult> CreateRechargePolicy(ExpenseRechargePolicyRequest r, CancellationToken t) => Tx(async () =>
    {
        if (!RechargeManager) return Forbid();
        if (user.UserId is null) return Unauthorized();
        if (r.RequestId == Guid.Empty || r.ValidFrom == default || r.ValidTo < r.ValidFrom ||
            string.IsNullOrWhiteSpace(r.Reference) || string.IsNullOrWhiteSpace(r.ServiceType) ||
            r.ChargeBasis != "ClaimAmountAsNet" || r.Multiplier is <= 0 or > 10 || decimal.Round(r.Multiplier, 4) != r.Multiplier ||
            !RechargeMoney(r.MaximumNet) || !RechargeMoney(r.AuthorisedNetLimit))
            return BadRequest(new { message = "Explicit recharge basis, multiplier, per-claim and total authorisation limits, service type, reference and dates are required." });
        var contracts = await Rows("select branch_id from finance_contract_versions where id=@id and organization_id=@org and (@wide or branch_id=@branch) and status='Approved'",
            c => Add(c, "id", r.ContractVersionId), t);
        if (contracts.Count != 1) return NotFound();
        var branch = (Guid)contracts[0]["branch_id"]!;
        var snapshot = JsonSerializer.Serialize(r);
        var replay = await RechargeReplay("finance_recharge_policies", r.RequestId, snapshot, t);
        if (replay is not null) return replay;
        await Exec("""
            insert into finance_recharge_policies(id,organization_id,branch_id,contract_version_id,claim_policy_id,service_type,reference,
              valid_from,valid_to,charge_basis,multiplier,maximum_net,authorised_net_limit,created_by,request_snapshot)
            values(@id,@org,@target,@contract,@claimPolicy,@service,@reference,@start,@end,@basis,@multiplier,@maximum,@limit,@actorId,cast(@snapshot as jsonb))
            """, c => { Add(c, "id", r.RequestId); Add(c, "target", branch); Add(c, "contract", r.ContractVersionId);
                Add(c, "claimPolicy", r.ClaimPolicyId); Add(c, "service", r.ServiceType.Trim()); Add(c, "reference", r.Reference.Trim());
                Add(c, "start", r.ValidFrom); Add(c, "end", r.ValidTo); Add(c, "basis", r.ChargeBasis); Add(c, "multiplier", r.Multiplier);
                Add(c, "maximum", r.MaximumNet); Add(c, "limit", r.AuthorisedNetLimit); Add(c, "snapshot", snapshot); }, t);
        Audit("EXPENSE_RECHARGE_POLICY_CREATED", r.RequestId, branch); await db.SaveChangesAsync(t);
        return Created("", new { id = r.RequestId });
    }, t);

    [HttpPost("recharge-policies/{id:guid}/approve")]
    public Task<IActionResult> ApproveRechargePolicy(Guid id, CancellationToken t) => Tx(async () =>
    {
        if (!RechargeManager) return Forbid();
        await using var c = await Cmd("""
            update finance_recharge_policies set approved_by=@actorId,approved_at=now()
            where id=@id and organization_id=@org and (@wide or branch_id=@branch) and approved_by is null and created_by<>@actorId returning branch_id
            """, t);
        Add(c, "id", id);
        if (await c.ExecuteScalarAsync(t) is not Guid branch) return Conflict(new { message = "A different finance reviewer must approve a current recharge policy draft." });
        Audit("EXPENSE_RECHARGE_POLICY_APPROVED", id, branch); await db.SaveChangesAsync(t);
        return Ok(new { id });
    }, t);

    [HttpGet("recharges")]
    public async Task<IActionResult> Recharges(CancellationToken t)
    {
        if (!RechargeManager) return Forbid();
        return Ok(await Rows("""
            select r.*,l.invoice_id from finance_expense_recharges r left join finance_invoice_lines l on l.recharge_id=r.id
            where r.organization_id=@org and (@wide or r.branch_id=@branch) order by r.created_at desc
            """, _ => { }, t));
    }

    [HttpPost("recharges")]
    public Task<IActionResult> CreateRecharge(ExpenseRechargeRequest r, CancellationToken t) => Tx(async () =>
    {
        if (!RechargeManager) return Forbid();
        if (user.UserId is null) return Unauthorized();
        if (r.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(r.Reason)) return BadRequest(new { message = "Request ID and reason are required." });
        var snapshot = JsonSerializer.Serialize(r);
        var replay = await RechargeReplay("finance_expense_recharges", r.RequestId, snapshot, t);
        if (replay is not null) return replay;
        // The database revalidates all related records and reserves the policy's monetary limit under a policy-row lock.
        await using var c = await Cmd("""
            insert into finance_expense_recharges(id,organization_id,branch_id,claim_id,policy_id,service_user_id,funder_id,service_date,currency,net_amount,reason,request_snapshot,created_by)
            select @id,@org,p.branch_id,claim.id,p.id,contract.person_id,contract.funder_id,claim.claim_date,claim.currency,
              finance_policy_round(claim.amount*p.multiplier,2,contract.rounding_mode),@reason,cast(@snapshot as jsonb),@actorId
            from finance_recharge_policies p join finance_contract_versions contract on contract.id=p.contract_version_id
              join finance_workforce_claims claim on claim.id=@claim and claim.organization_id=p.organization_id and claim.branch_id=p.branch_id
            where p.id=@policy and p.organization_id=@org and (@wide or p.branch_id=@branch) returning branch_id,net_amount,currency
            """, t);
        Add(c, "id", r.RequestId); Add(c, "claim", r.ClaimId); Add(c, "policy", r.PolicyId);
        Add(c, "reason", r.Reason.Trim()); Add(c, "snapshot", snapshot);
        await using var reader = await c.ExecuteReaderAsync(t);
        if (!await reader.ReadAsync(t)) return NotFound();
        var branch = reader.GetGuid(0); var amount = reader.GetDecimal(1); var currency = reader.GetString(2).Trim();
        await reader.DisposeAsync(); Audit("EXPENSE_RECHARGE_SUBMITTED", r.RequestId, branch); await db.SaveChangesAsync(t);
        return Created("", new { id = r.RequestId, netAmount = amount, currency, status = "Submitted" });
    }, t);

    [HttpPost("recharges/{id:guid}/review")]
    public Task<IActionResult> ReviewRecharge(Guid id, ReviewClaimRequest r, CancellationToken t) => Tx(async () =>
    {
        if (!RechargeManager) return Forbid();
        if (r.Decision is not ("Approve" or "Reject") || string.IsNullOrWhiteSpace(r.Reason)) return BadRequest();
        var status = r.Decision == "Approve" ? "Approved" : "Rejected";
        var rows = await Rows("select * from finance_expense_recharges where id=@id and organization_id=@org and (@wide or branch_id=@branch) for update", c => Add(c, "id", id), t);
        if (rows.Count != 1) return NotFound();
        var prior = rows[0];
        if ((string)prior["status"]! != "Submitted") return Equals(prior["approved_by"], user.UserId) &&
            Equals(prior["status"], status) && Equals(prior["review_reason"], r.Reason.Trim())
            ? Ok(new { id, replayed = true }) : Conflict(new { message = "Recharge was already reviewed." });
        if (Equals(prior["created_by"], user.UserId)) return Conflict(new { message = "A different finance reviewer must review the recharge." });
        await Exec("""
            update finance_expense_recharges set status=@status,approved_by=@actorId,reviewed_at=now(),review_reason=@reason
            where id=@id and organization_id=@org
            """, c => { Add(c, "id", id); Add(c, "status", status); Add(c, "reason", r.Reason.Trim()); }, t);
        Audit("EXPENSE_RECHARGE_" + status.ToUpperInvariant(), id, (Guid)prior["branch_id"]!); await db.SaveChangesAsync(t);
        return Ok(new { id, status });
    }, t);

    async Task<IActionResult?> RechargeReplay(string table, Guid id, string snapshot, CancellationToken t)
    {
        // Table names are private constants selected by the endpoints, never request values.
        var rows = await Rows($"select request_snapshot=cast(@snapshot as jsonb) and created_by=@actorId matches from {table} where id=@id and organization_id=@org and (@wide or branch_id=@branch)",
            c => { Add(c, "id", id); Add(c, "snapshot", snapshot); }, t);
        return rows.Count == 0 ? null : (bool)rows[0]["matches"]! ? Ok(new { id, replayed = true }) : Conflict(new { message = "Request ID already has different details or creator." });
    }

    static bool RechargeMoney(decimal value) => value > 0 && value <= 9999999999.99m && decimal.Round(value, 2) == value;
}

public sealed record ExpenseRechargePolicyRequest(Guid RequestId, Guid ContractVersionId, Guid ClaimPolicyId, string ServiceType,
    string Reference, DateOnly ValidFrom, DateOnly ValidTo, string ChargeBasis, decimal Multiplier, decimal MaximumNet, decimal AuthorisedNetLimit);
public sealed record ExpenseRechargeRequest(Guid RequestId, Guid ClaimId, Guid PolicyId, string Reason);
