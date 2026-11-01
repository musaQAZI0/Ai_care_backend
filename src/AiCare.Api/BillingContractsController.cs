using System.Data;
using System.Data.Common;
using System.Text.Json;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AiCare.Api;

[ApiController, Authorize(Roles="Administrator,BackOffice,CareManager")]
[Route("api/phase1/finance/billing-contracts")]
public sealed partial class BillingContractsController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken token)
    {
        var rows = new List<JsonElement>();
        await using var command = await Command("""
            select row_to_json(q)::text from (select c.*,s."FullName" recipient,f.name payer,
              (select jsonb_agg(jsonb_build_object('day',r.day_type,'unit',r.unit,'rate',r.unit_rate,'minimum',r.minimum_quantity,'priority',r.priority,'type',r.rule_type,'from',r.time_from,'to',r.time_to) order by r.priority desc,r.id) from finance_rate_rules r where r.rate_version_id=c.rate_version_id and r.organization_id=c.organization_id) rate_rules
              from finance_contract_versions c join "ServiceUsers" s on s."Id"=c.person_id and s."OrganizationId"=c.organization_id
              join finance_funders f on f.id=c.funder_id and f.organization_id=c.organization_id
              where c.organization_id=@org and (@wide or c.branch_id=@branch) order by c.created_at desc) q
            """, token);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) rows.Add(JsonSerializer.Deserialize<JsonElement>(reader.GetString(0)));
        return Ok(rows);
    }

    [HttpPost]
    public async Task<IActionResult> Create(BillingContractRequest r, CancellationToken token)
    {
        if (!user.IsAdministrator && !user.IsBackOffice) return Forbid();
        if (user.UserId is null) return Unauthorized();
        if (r.RequestId==Guid.Empty || r.AuthorisedFrom==default || r.AuthorisedTo<r.AuthorisedFrom ||
            string.IsNullOrWhiteSpace(r.ServiceType) || string.IsNullOrWhiteSpace(r.AuthorisationReference) || string.IsNullOrWhiteSpace(r.PolicyReference) ||
            r.BillingBasis is not ("Planned" or "Actual") || r.CancellationPolicy is not ("NonChargeable" or "ManualReview") ||
            r.RoundingMode is not ("AwayFromZero" or "ToEven") || r.CapMode is not ("Uncapped" or "LifetimeAmount") ||
            (r.CapMode=="Uncapped" ? r.CapAmount is not null : r.CapAmount is null or <=0) ||
            (r.CapAmount is decimal cap && (decimal.Round(cap,2)!=cap || cap>9999999999.99m)))
            return BadRequest(new { message="Explicit effective dates, billing/cancellation/rounding/cap policies and authorisation/policy references are required." });
        return await Transaction(async () =>
        {
            Guid person, funder, branch; string currency;
            await using(var command=await Command("""
                select a.service_user_id,a.funder_id,a.branch_id,f.currency from funding_arrangements a
                  join finance_funders f on f.id=a.funder_id and f.organization_id=a.organization_id
                where a.id=@funding and a.organization_id=@org and (@wide or a.branch_id=@branch) for update of a
                """,token))
            {
                Add(command,"funding",r.FundingArrangementId);
                await using var reader=await command.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token)) return NotFound();
                person=reader.GetGuid(0); funder=reader.GetGuid(1); branch=reader.GetGuid(2); currency=reader.GetString(3).Trim();
            }
            var snapshot=JsonSerializer.Serialize(r);
            await using(var replay=await Command("select request_snapshot::text from finance_contract_versions where id=@id and organization_id=@org and branch_id=@target",token))
            {
                Add(replay,"id",r.RequestId); Add(replay,"target",branch);
                if (await replay.ExecuteScalarAsync(token) is string existing)
                {
                    using var compare=await Command("select cast(@existing as jsonb)=cast(@request as jsonb)",token);
                    Add(compare,"existing",existing); Add(compare,"request",snapshot);
                    return (bool)(await compare.ExecuteScalarAsync(token))! ? Ok(new{id=r.RequestId,replayed=true}) : Conflict(new{message="Request ID was already used with different contract details."});
                }
            }
            await using(var command=await Command("""
                insert into finance_contract_versions(id,organization_id,branch_id,funding_arrangement_id,person_id,funder_id,rate_version_id,version,
                  service_type,currency,authorised_from,authorised_to,authorisation_reference,billing_basis,cancellation_policy,rounding_mode,policy_reference,cap_mode,cap_amount,created_by,request_snapshot)
                values(@id,@org,@target,@funding,@person,@funder,@rate,(select coalesce(max(version),0)+1 from finance_contract_versions where funding_arrangement_id=@funding),
                  @service,@currency,@start,@end,@reference,@basis,@cancellation,@rounding,@policy,@mode,@cap,@actor,cast(@request as jsonb))
                """,token))
            {
                Add(command,"id",r.RequestId); Add(command,"target",branch); Add(command,"funding",r.FundingArrangementId);
                Add(command,"person",person); Add(command,"funder",funder); Add(command,"rate",r.RateVersionId); Add(command,"service",r.ServiceType.Trim());
                Add(command,"currency",currency); Add(command,"start",r.AuthorisedFrom); Add(command,"end",r.AuthorisedTo);
                Add(command,"reference",r.AuthorisationReference.Trim()); Add(command,"basis",r.BillingBasis); Add(command,"cancellation",r.CancellationPolicy);
                Add(command,"rounding",r.RoundingMode); Add(command,"policy",r.PolicyReference.Trim()); Add(command,"mode",r.CapMode);
                var cap=command.CreateParameter(); cap.ParameterName="cap"; cap.DbType=DbType.Decimal; cap.Value=(object?)r.CapAmount??DBNull.Value; command.Parameters.Add(cap);
                Add(command,"request",snapshot); await command.ExecuteNonQueryAsync(token);
            }
            Audit("FINANCE_CONTRACT_CREATED",r.RequestId,branch); await db.SaveChangesAsync(token);
            return Created($"/api/phase1/finance/billing-contracts/{r.RequestId}",new{id=r.RequestId,status="Draft"});
        },token);
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, BillingContractDecision r, CancellationToken token)
    {
        if (!user.IsAdministrator && !user.IsBackOffice) return Forbid();
        if (user.UserId is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(r.Reason)) return BadRequest(new{message="An approval reason is required."});
        return await Transaction(async () =>
        {
            await using var command=await Command("""
                update finance_contract_versions set status='Approved',approved_by=@actor,approved_at=now(),approval_reason=@reason
                  where id=@id and organization_id=@org and (@wide or branch_id=@branch) and status='Draft' and created_by<>@actor returning branch_id
                """,token);
            Add(command,"id",id); Add(command,"reason",r.Reason.Trim());
            if (await command.ExecuteScalarAsync(token) is not Guid branch)
            {
                await using var replay=await Command("select count(*) from finance_contract_versions where id=@id and organization_id=@org and (@wide or branch_id=@branch) and status='Approved' and approved_by=@actor and approval_reason=@reason",token);
                Add(replay,"id",id);Add(replay,"reason",r.Reason.Trim());
                if(Convert.ToInt32(await replay.ExecuteScalarAsync(token))==1)return Ok(new{id,status="Approved",replayed=true});
                return Conflict(new{message="Only an accessible draft created by another person can be approved."});
            }
            Audit("FINANCE_CONTRACT_APPROVED",id,branch); await db.SaveChangesAsync(token); return Ok(new{id,status="Approved"});
        },token);
    }

    private async Task<IActionResult> Transaction(Func<Task<IActionResult>> action,CancellationToken token)
    {
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction=await db.Database.BeginTransactionAsync(token);
                var result=await action(); await transaction.CommitAsync(token); return result;
            });
        }
        catch(Npgsql.PostgresException e) when(e.SqlState is "23514" or "23505" or "23P01")
        { return Conflict(new{message="Contract references, dates or version conflict. Reload the current configuration before retrying."}); }
    }

    private async Task<DbCommand> Command(string sql,CancellationToken token)
    {
        var connection=db.Database.GetDbConnection(); if(connection.State!=ConnectionState.Open) await connection.OpenAsync(token);
        var command=connection.CreateCommand(); command.Transaction=db.Database.CurrentTransaction?.GetDbTransaction(); command.CommandText=sql;
        Add(command,"org",tenant.OrganizationId); Add(command,"branch",tenant.BranchId??Guid.Empty); Add(command,"wide",tenant.IsOrganizationWide); Add(command,"actor",user.UserId); return command;
    }
    private static void Add(DbCommand command,string name,object? value) { var p=command.CreateParameter(); p.ParameterName=name; p.Value=value??DBNull.Value; command.Parameters.Add(p); }
    private void Audit(string action,Guid id,Guid branch)=>db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),action,user.UserName,"BillingContract",id,DateTimeOffset.UtcNow,tenant.OrganizationId,branch));
}

public sealed record BillingContractRequest(Guid RequestId,Guid FundingArrangementId,Guid RateVersionId,string ServiceType,
    DateOnly AuthorisedFrom,DateOnly AuthorisedTo,string AuthorisationReference,string BillingBasis,string CancellationPolicy,
    string RoundingMode,string PolicyReference,string CapMode,decimal? CapAmount);
public sealed record BillingContractDecision(string Reason);
