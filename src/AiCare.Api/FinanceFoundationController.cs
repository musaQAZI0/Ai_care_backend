using System.Data;
using System.Data.Common;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AiCare.Api;

[ApiController]
[Authorize(Roles = "Administrator,BackOffice,CareManager")]
[Route("api/phase1/finance")]
public sealed class FinanceFoundationController(CareDbContext db, ITenantContext tenant, ICurrentUserContext user) : ControllerBase
{
    private bool CanManage => user.IsAdministrator || user.IsBackOffice;
    private Guid Branch => tenant.BranchId ?? TenantDefaults.BranchId;

    [HttpGet("funders")]
    public async Task<IActionResult> Funders([FromQuery] bool includeInactive, CancellationToken token)
    {
        var rows = await Query("""
            select id,name,funder_type,billing_address,billing_email,billing_phone,payment_terms_days,
                   default_invoice_frequency,currency,external_reference,notes,active,branch_id,created_at,updated_at
              from finance_funders
             where organization_id=@organization and (@wide or branch_id is null or branch_id=@branch)
               and (@includeInactive or active)
             order by active desc,name
            """, c => Add(c, "includeInactive", includeInactive), r => new
            {
                id=r.GetGuid(0),name=r.GetString(1),funderType=r.GetString(2),billingAddress=r.GetString(3),
                billingEmail=r.GetString(4),billingPhone=r.GetString(5),paymentTermsDays=r.GetInt32(6),
                defaultInvoiceFrequency=r.GetString(7),currency=r.GetString(8).Trim(),externalReference=r.GetString(9),
                notes=r.GetString(10),active=r.GetBoolean(11),branchId=r.IsDBNull(12)?null:(Guid?)r.GetGuid(12),
                createdAt=r.GetFieldValue<DateTimeOffset>(13),updatedAt=r.GetFieldValue<DateTimeOffset>(14)
            }, token);
        return Ok(rows);
    }

    [HttpPost("funders")]
    public async Task<IActionResult> CreateFunder(FunderRequest request, CancellationToken token)
    {
        if (!CanManage) return Forbid();
        var error = ValidateFunder(request); if (error is not null) return BadRequest(new { message=error });
        var id=Guid.NewGuid();
        try
        {
            await Exec("""
                insert into finance_funders(id,organization_id,branch_id,name,normalized_name,funder_type,billing_address,billing_email,billing_phone,payment_terms_days,default_invoice_frequency,currency,external_reference,notes,created_by,updated_by)
                values(@id,@organization,@recordBranch,@name,@normalized,@type,@address,@email,@phone,@terms,@frequency,@currency,@external,@notes,@actor,@actor)
                """, c => BindFunder(c,id,request), token);
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
        { return Conflict(new { message="An active funder with this name already exists." }); }
        Audit("FUNDER_CREATED","Funder",id); await db.SaveChangesAsync(token);
        return Created($"/api/phase1/finance/funders/{id}",new{id});
    }

    [HttpPut("funders/{id:guid}")]
    public async Task<IActionResult> UpdateFunder(Guid id,FunderRequest request,CancellationToken token)
    {
        if (!CanManage) return Forbid();
        var error=ValidateFunder(request); if(error is not null)return BadRequest(new{message=error});
        try
        {
            var changed=await Exec("""
                update finance_funders set name=@name,normalized_name=@normalized,funder_type=@type,billing_address=@address,
                  billing_email=@email,billing_phone=@phone,payment_terms_days=@terms,default_invoice_frequency=@frequency,
                  currency=@currency,external_reference=@external,notes=@notes,updated_at=now(),updated_by=@actor
                where id=@id and organization_id=@organization and (@wide or branch_id is null or branch_id=@branch)
                """,c=>BindFunder(c,id,request),token);
            if(changed==0)return NotFound();
        }
        catch(Npgsql.PostgresException e) when(e.SqlState==Npgsql.PostgresErrorCodes.UniqueViolation)
        {return Conflict(new{message="An active funder with this name already exists."});}
        Audit("FUNDER_UPDATED","Funder",id);await db.SaveChangesAsync(token);return NoContent();
    }

    [HttpPost("funders/{id:guid}/archive")]
    public async Task<IActionResult> ArchiveFunder(Guid id,CancellationToken token)
    {
        if(!CanManage)return Forbid();
        var changed=await Exec("update finance_funders set active=false,updated_at=now(),updated_by=@actor where id=@id and organization_id=@organization and (@wide or branch_id is null or branch_id=@branch) and active",c=>Add(c,"id",id),token);
        if(changed==0)return NotFound();Audit("FUNDER_ARCHIVED","Funder",id);await db.SaveChangesAsync(token);return NoContent();
    }

    [HttpGet("funding-arrangements")]
    public async Task<IActionResult> FundingArrangements([FromQuery] Guid? serviceUserId,CancellationToken token)
    {
        var rows=await Query("""
          select a.id,a.service_user_id,a.funder_id,coalesce(f.name,a.funder_name),a.rate_card_id,coalesce(rc.name,''),
                 a.valid_from,a.valid_to,a.invoice_frequency,a.allocation_rule,a.contract_reference,a.purchase_order_reference,
                 a.authorized_hours_per_week,a.status,a.notes,a.branch_id
            from funding_arrangements a
            left join finance_funders f on f.id=a.funder_id and f.organization_id=a.organization_id
            left join finance_rate_cards rc on rc.id=a.rate_card_id and rc.organization_id=a.organization_id
           where a.organization_id=@organization and (@wide or a.branch_id=@branch)
             and (@person is null or a.service_user_id=@person)
           order by a.valid_from desc
        """,c=>Add(c,"person",serviceUserId),r=>new{id=r.GetGuid(0),serviceUserId=r.GetGuid(1),funderId=r.IsDBNull(2)?null:(Guid?)r.GetGuid(2),funderName=r.GetString(3),rateCardId=r.IsDBNull(4)?null:(Guid?)r.GetGuid(4),rateCardName=r.GetString(5),startDate=r.GetFieldValue<DateTime>(6),endDate=r.IsDBNull(7)?null:(DateTime?)r.GetFieldValue<DateTime>(7),invoiceFrequency=r.GetString(8),allocationRule=r.GetString(9),billingReference=r.GetString(10),purchaseOrderReference=r.GetString(11),authorizedHoursPerWeek=r.GetDecimal(12),status=r.GetString(13),notes=r.GetString(14),branchId=r.GetGuid(15)},token);
        return Ok(rows);
    }

    [HttpPost("funding-arrangements")]
    public async Task<IActionResult> CreateFundingArrangement(FundingArrangementRequest request,CancellationToken token)
    {
        if(!CanManage)return Forbid();
        if(request.ServiceUserId==Guid.Empty||request.FunderId==Guid.Empty||request.StartDate==default||request.EndDate<request.StartDate||request.AuthorizedHoursPerWeek<0)return BadRequest(new{message="Service user, funder, valid effective dates, and non-negative authorized hours are required."});
        if(!new[]{"Weekly","FourWeekly","Monthly","AdHoc"}.Contains(request.InvoiceFrequency)||!new[]{"Primary","Percentage","FixedAmount","Remainder"}.Contains(request.AllocationRule))return BadRequest(new{message="Invoice frequency or allocation rule is invalid."});
        var person=await db.ServiceUsers.AsNoTracking().AnyAsync(x=>x.Id==request.ServiceUserId&&x.OrganizationId==tenant.OrganizationId&&(tenant.IsOrganizationWide||x.BranchId==tenant.BranchId),token);if(!person)return NotFound();
        var funder=await Scalar("select count(*) from finance_funders where id=@funder and organization_id=@organization and active and (@wide or branch_id is null or branch_id=@branch)",c=>Add(c,"funder",request.FunderId),token);if(funder==0)return BadRequest(new{message="Active funder was not found in this scope."});
        var overlap=await Scalar("""
          select count(*) from funding_arrangements where service_user_id=@person and organization_id=@organization and status='Active'
          and allocation_rule='Primary' and @allocation='Primary' and valid_from<=coalesce(@endDate,'infinity'::timestamptz) and coalesce(valid_to,'infinity'::timestamptz)>=@startDate
        """,c=>{Add(c,"person",request.ServiceUserId);Add(c,"allocation",request.AllocationRule);Add(c,"startDate",request.StartDate);Add(c,"endDate",request.EndDate);},token);if(overlap>0)return Conflict(new{message="This service user already has an overlapping primary funding arrangement."});
        var id=Guid.NewGuid();
        await Exec("""
          insert into funding_arrangements(id,service_user_id,organization_id,branch_id,funding_source,funder_name,contract_reference,care_package_type,authorized_hours_per_week,hourly_rate,valid_from,valid_to,status,notes,created_at,updated_at,funder_id,rate_card_id,invoice_frequency,allocation_rule,purchase_order_reference,created_by)
          select @id,@person,@organization,@branch,f.funder_type,f.name,@billingReference,@serviceType,@hours,0,@startDate,@endDate,'Active',@notes,now(),now(),f.id,@rateCard,@frequency,@allocation,@purchaseOrder,@actor from finance_funders f where f.id=@funder and f.organization_id=@organization
        """,c=>{Add(c,"id",id);Add(c,"person",request.ServiceUserId);Add(c,"funder",request.FunderId);Add(c,"rateCard",request.RateCardId);Add(c,"billingReference",request.BillingReference??"");Add(c,"serviceType",request.ServiceType??"");Add(c,"hours",request.AuthorizedHoursPerWeek);Add(c,"startDate",request.StartDate);Add(c,"endDate",request.EndDate);Add(c,"frequency",request.InvoiceFrequency);Add(c,"allocation",request.AllocationRule);Add(c,"purchaseOrder",request.PurchaseOrderReference??"");Add(c,"notes",request.Notes??"");},token);
        Audit("FUNDING_ARRANGEMENT_CREATED","FundingArrangement",id);await db.SaveChangesAsync(token);return Created($"/api/phase1/finance/funding-arrangements/{id}",new{id});
    }

    [HttpGet("rate-cards")]
    public async Task<IActionResult> RateCards(CancellationToken token)=>Ok(await Query("""
      select c.id,c.name,c.service_type,c.currency,c.active,c.branch_id,count(v.id) versions
      from finance_rate_cards c left join finance_rate_versions v on v.rate_card_id=c.id
      where c.organization_id=@organization and (@wide or c.branch_id is null or c.branch_id=@branch)
      group by c.id order by c.active desc,c.name
    """,_=>{},r=>new{id=r.GetGuid(0),name=r.GetString(1),serviceType=r.GetString(2),currency=r.GetString(3).Trim(),active=r.GetBoolean(4),branchId=r.IsDBNull(5)?null:(Guid?)r.GetGuid(5),versions=r.GetInt64(6)},token));

    [HttpPost("rate-cards")]
    public async Task<IActionResult> CreateRateCard(RateCardRequest request,CancellationToken token)
    {
        if(!CanManage)return Forbid();if(string.IsNullOrWhiteSpace(request.Name)||string.IsNullOrWhiteSpace(request.ServiceType)||!Currency(request.Currency))return BadRequest(new{message="Name, service type, and a three-letter currency are required."});var id=Guid.NewGuid();
        try{await Exec("insert into finance_rate_cards(id,organization_id,branch_id,name,service_type,currency,created_by,updated_by) values(@id,@organization,@recordBranch,@name,@serviceType,@currency,@actor,@actor)",c=>{Add(c,"id",id);Add(c,"recordBranch",request.OrganizationWide?null:Branch);Add(c,"name",request.Name.Trim());Add(c,"serviceType",request.ServiceType.Trim());Add(c,"currency",request.Currency.Trim().ToUpperInvariant());},token);}catch(Npgsql.PostgresException e)when(e.SqlState==Npgsql.PostgresErrorCodes.UniqueViolation){return Conflict(new{message="A rate card with this name already exists."});}
        Audit("RATE_CREATED","RateCard",id);await db.SaveChangesAsync(token);return Created($"/api/phase1/finance/rate-cards/{id}",new{id});
    }

    [HttpPost("rate-cards/{rateCardId:guid}/versions")]
    public async Task<IActionResult> CreateRateVersion(Guid rateCardId,RateVersionRequest request,CancellationToken token)
    {
        if(!CanManage)return Forbid();if(request.EffectiveFrom==default||request.EffectiveTo<request.EffectiveFrom||request.Rules.Count==0||request.Rules.Any(x=>x.UnitRate<0||!new[]{"Hour","Visit","Mile","Item"}.Contains(x.Unit)))return BadRequest(new{message="Valid effective dates and at least one valid rate rule are required."});
        var card=await Scalar("select count(*) from finance_rate_cards where id=@card and organization_id=@organization and active and (@wide or branch_id is null or branch_id=@branch)",c=>Add(c,"card",rateCardId),token);if(card==0)return NotFound();
        var overlap=await Scalar("select count(*) from finance_rate_versions where rate_card_id=@card and status='Active' and effective_from<=coalesce(@effectiveTo,'infinity'::date) and coalesce(effective_to,'infinity'::date)>=@effectiveFrom",c=>{Add(c,"card",rateCardId);Add(c,"effectiveFrom",request.EffectiveFrom);Add(c,"effectiveTo",request.EffectiveTo);},token);if(overlap>0)return Conflict(new{message="An active rate version overlaps these effective dates."});
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx=await db.Database.BeginTransactionAsync(token);var id=Guid.NewGuid();var version=await Scalar("select coalesce(max(version),0)+1 from finance_rate_versions where rate_card_id=@card",c=>Add(c,"card",rateCardId),token);
            await Exec("insert into finance_rate_versions(id,rate_card_id,organization_id,branch_id,version,effective_from,effective_to,status,created_by) values(@id,@card,@organization,@recordBranch,@version,@effectiveFrom,@effectiveTo,'Active',@actor)",c=>{Add(c,"id",id);Add(c,"card",rateCardId);Add(c,"recordBranch",tenant.IsOrganizationWide?null:Branch);Add(c,"version",version);Add(c,"effectiveFrom",request.EffectiveFrom);Add(c,"effectiveTo",request.EffectiveTo);},token);
            foreach(var rule in request.Rules)await Exec("insert into finance_rate_rules(id,rate_version_id,organization_id,rule_type,day_type,time_from,time_to,unit,unit_rate,minimum_quantity,priority,created_by) values(@id,@versionId,@organization,@ruleType,@dayType,@timeFrom,@timeTo,@unit,@rate,@minimum,@priority,@actor)",c=>{Add(c,"id",Guid.NewGuid());Add(c,"versionId",id);Add(c,"ruleType",rule.RuleType);Add(c,"dayType",rule.DayType);Add(c,"timeFrom",rule.TimeFrom);Add(c,"timeTo",rule.TimeTo);Add(c,"unit",rule.Unit);Add(c,"rate",rule.UnitRate);Add(c,"minimum",rule.MinimumQuantity);Add(c,"priority",rule.Priority);},token);
            Audit("RATE_CHANGED","RateVersion",id);await db.SaveChangesAsync(token);await tx.CommitAsync(token);return Created($"/api/phase1/finance/rate-cards/{rateCardId}/versions/{id}",new{id,version});
        });
    }

    [HttpGet("rate-cards/{rateCardId:guid}/versions")]
    public async Task<IActionResult> RateVersions(Guid rateCardId,CancellationToken token)=>Ok(await Query("""
      select v.id,v.version,v.effective_from,v.effective_to,v.status,r.id,r.rule_type,r.day_type,r.unit,r.unit_rate,r.minimum_quantity,r.priority
      from finance_rate_versions v join finance_rate_cards c on c.id=v.rate_card_id left join finance_rate_rules r on r.rate_version_id=v.id
      where v.rate_card_id=@card and v.organization_id=@organization and (@wide or c.branch_id is null or c.branch_id=@branch)
      order by v.version desc,r.priority desc
    """,c=>Add(c,"card",rateCardId),r=>new{versionId=r.GetGuid(0),version=r.GetInt32(1),effectiveFrom=r.GetFieldValue<DateOnly>(2),effectiveTo=r.IsDBNull(3)?null:(DateOnly?)r.GetFieldValue<DateOnly>(3),status=r.GetString(4),ruleId=r.IsDBNull(5)?null:(Guid?)r.GetGuid(5),ruleType=r.IsDBNull(6)?null:r.GetString(6),dayType=r.IsDBNull(7)?null:r.GetString(7),unit=r.IsDBNull(8)?null:r.GetString(8),unitRate=r.IsDBNull(9)?null:(decimal?)r.GetDecimal(9),minimumQuantity=r.IsDBNull(10)?null:(decimal?)r.GetDecimal(10),priority=r.IsDBNull(11)?null:(int?)r.GetInt32(11)},token));

    private static string? ValidateFunder(FunderRequest r)=>string.IsNullOrWhiteSpace(r.Name)?"Funder name is required.":!new[]{"LocalAuthority","Private","Family","NHS","Organisation","Other"}.Contains(r.FunderType)?"Funder type is invalid.":r.PaymentTermsDays is <0 or >365?"Payment terms must be between 0 and 365 days.":!new[]{"Weekly","FourWeekly","Monthly","AdHoc"}.Contains(r.DefaultInvoiceFrequency)?"Invoice frequency is invalid.":!Currency(r.Currency)?"Currency must be a three-letter code.":!string.IsNullOrWhiteSpace(r.BillingEmail)&&!System.Net.Mail.MailAddress.TryCreate(r.BillingEmail,out _)?"Billing email is invalid.":null;
    private static bool Currency(string value)=>!string.IsNullOrWhiteSpace(value)&&value.Trim().Length==3&&value.Trim().All(char.IsLetter);
    private void BindFunder(DbCommand c,Guid id,FunderRequest r){Add(c,"id",id);Add(c,"recordBranch",r.OrganizationWide?null:Branch);Add(c,"name",r.Name.Trim());Add(c,"normalized",r.Name.Trim().ToUpperInvariant());Add(c,"type",r.FunderType);Add(c,"address",r.BillingAddress??"");Add(c,"email",r.BillingEmail?.Trim()??"");Add(c,"phone",r.BillingPhone??"");Add(c,"terms",r.PaymentTermsDays);Add(c,"frequency",r.DefaultInvoiceFrequency);Add(c,"currency",r.Currency.Trim().ToUpperInvariant());Add(c,"external",r.ExternalReference??"");Add(c,"notes",r.Notes??"");}
    private async Task<int> Scalar(string sql,Action<DbCommand> bind,CancellationToken token){await using var c=await Command(sql,token);bind(c);return Convert.ToInt32(await c.ExecuteScalarAsync(token));}
    private async Task<int> Exec(string sql,Action<DbCommand> bind,CancellationToken token){await using var c=await Command(sql,token);bind(c);return await c.ExecuteNonQueryAsync(token);}
    private async Task<List<T>> Query<T>(string sql,Action<DbCommand> bind,Func<DbDataReader,T> map,CancellationToken token){var rows=new List<T>();await using var c=await Command(sql,token);bind(c);await using var r=await c.ExecuteReaderAsync(token);while(await r.ReadAsync(token))rows.Add(map(r));return rows;}
    private async Task<DbCommand> Command(string sql,CancellationToken token){var cn=db.Database.GetDbConnection();if(cn.State!=ConnectionState.Open)await cn.OpenAsync(token);var c=cn.CreateCommand();c.Transaction=db.Database.CurrentTransaction?.GetDbTransaction();c.CommandText=sql;Add(c,"organization",tenant.OrganizationId);Add(c,"branch",Branch);Add(c,"wide",tenant.IsOrganizationWide);Add(c,"actor",user.UserName);return c;}
    private static void Add(DbCommand c,string name,object? value){if(c.Parameters.Contains(name))return;var p=c.CreateParameter();p.ParameterName=name;p.Value=value??DBNull.Value;c.Parameters.Add(p);}
    private void Audit(string action,string entity,Guid? id)=>db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),action,user.UserName,entity,id,DateTimeOffset.UtcNow,tenant.OrganizationId,Branch));
}

public sealed record FunderRequest(string Name,string FunderType,string? BillingAddress,string? BillingEmail,string? BillingPhone,int PaymentTermsDays,string DefaultInvoiceFrequency,string Currency,string? ExternalReference,string? Notes,bool OrganizationWide=false);
public sealed record FundingArrangementRequest(Guid ServiceUserId,Guid FunderId,Guid? RateCardId,DateTimeOffset StartDate,DateTimeOffset? EndDate,string InvoiceFrequency,string AllocationRule,string? BillingReference,string? PurchaseOrderReference,string? ServiceType,decimal AuthorizedHoursPerWeek,string? Notes);
public sealed record RateCardRequest(string Name,string ServiceType,string Currency,bool OrganizationWide=false);
public sealed record RateVersionRequest(DateOnly EffectiveFrom,DateOnly? EffectiveTo,List<RateRuleRequest> Rules);
public sealed record RateRuleRequest(string RuleType,string DayType,string Unit,decimal UnitRate,decimal MinimumQuantity,int Priority,TimeOnly? TimeFrom=null,TimeOnly? TimeTo=null);
