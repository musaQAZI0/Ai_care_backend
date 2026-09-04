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
[Authorize(Roles="CareCoordinator,CareManager,Administrator")]
[Route("api/phase1/person-lifecycle/service-users/{personId:guid}")]
public sealed class PersonJourneyController(CareDbContext db,ITenantContext tenant,ICurrentUserContext user):ControllerBase
{
    [HttpGet("journey")]
    public async Task<IActionResult> Journey(Guid personId,CancellationToken token)
    {
        if(await Person(personId,token) is null)return NotFound();
        return Ok(new{
            admissions=await Query("select id,referral_id,admission_type,planned_at,admitted_at,status,funding_confirmed,initial_plan_confirmed,medication_reconciled,notes,admitted_by from person_admissions where service_user_id=@person and organization_id=@organization order by admitted_at desc",personId,r=>new AdmissionResponse(r.GetGuid(0),r.IsDBNull(1)?null:r.GetGuid(1),r.GetString(2),r.IsDBNull(3)?null:r.GetFieldValue<DateTimeOffset>(3),r.GetFieldValue<DateTimeOffset>(4),r.GetString(5),r.GetBoolean(6),r.GetBoolean(7),r.GetBoolean(8),r.GetString(9),r.GetString(10)),token),
            transfers=await Query("select id,source_branch_id,destination_branch_id,effective_at,reason,handover_summary,receiving_manager,transferred_by from person_transfers where service_user_id=@person and organization_id=@organization order by effective_at desc",personId,r=>new TransferResponse(r.GetGuid(0),r.GetGuid(1),r.GetGuid(2),r.GetFieldValue<DateTimeOffset>(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7)),token),
            discharges=await Query("select id,admission_id,discharged_at,reason,destination,summary,medication_handover,property_returned,follow_up_required,follow_up_details,authorized_by from person_discharges where service_user_id=@person and organization_id=@organization order by discharged_at desc",personId,r=>new DischargeResponse(r.GetGuid(0),r.GetGuid(1),r.GetFieldValue<DateTimeOffset>(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetBoolean(7),r.GetBoolean(8),r.GetString(9),r.GetString(10)),token)});
    }

    [HttpPost("admissions")]
    public async Task<IActionResult> Admit(Guid personId,CreateAdmissionRequest request,CancellationToken token)
    {
        var person=await Person(personId,token);if(person is null)return NotFound();
        if(person.Status is not("Assessment" or "Onboarding"))return Conflict(new{message="Only a person in Assessment or Onboarding can be admitted."});
        if(Missing(request.AdmissionType)||!request.FundingConfirmed||!request.InitialPlanConfirmed||!request.MedicationReconciled)return BadRequest(new{message="Admission type, funding, initial plan, and medication reconciliation are required."});
        if(request.ReferralId is not null&&await Scalar("select count(*) from person_referrals where id=@id and service_user_id=@person and organization_id=@organization and branch_id=@branch and status='Accepted'",personId,request.ReferralId,token)==0)return BadRequest(new{message="Admission referral must be accepted for this person and branch."});
        return await Transactional(async transaction=>{var id=Guid.NewGuid();var at=request.AdmittedAt??DateTimeOffset.UtcNow;
        try{await Exec("insert into person_admissions(id,service_user_id,organization_id,branch_id,referral_id,admission_type,planned_at,admitted_at,funding_confirmed,initial_plan_confirmed,medication_reconciled,notes,admitted_by) values(@id,@person,@organization,@branch,@referral,@type,@planned,@at,true,true,true,@notes,@actor)",personId,id,c=>{Add(c,"referral",request.ReferralId);Add(c,"type",request.AdmissionType.Trim());Add(c,"planned",request.PlannedAt);Add(c,"at",at);Add(c,"notes",request.Notes?.Trim()??"");},token);}catch(DbException){return Conflict(new{message="The person already has an active admission."});}
        await Exec("update \"PersonRecords\" set \"AdmittedAt\"=@at,\"DischargedAt\"=null,\"LastReviewedAt\"=now() where \"ServiceUserId\"=@person and \"OrganizationId\"=@organization and \"BranchId\"=@branch",personId,id,c=>Add(c,"at",at),token);
        db.Entry(person).State=EntityState.Detached;db.ServiceUsers.Update(person with{Status="Active"});Lifecycle(person,"Admitted","Active",request.Notes??request.AdmissionType);Audit("person_admission.created","PersonAdmission",id);await db.SaveChangesAsync(token);return CreatedAtAction(nameof(Journey),new{personId},new{id});},token);
    }

    [HttpPost("transfers")]
    [Authorize(Roles="CareManager,Administrator")]
    public async Task<IActionResult> Transfer(Guid personId,CreateTransferRequest request,CancellationToken token)
    {
        var person=await Person(personId,token);if(person is null)return NotFound();if(person.Status!="Active")return Conflict(new{message="Only an active person can be transferred."});
        if(request.DestinationBranchId==person.BranchId||Missing(request.Reason)||Missing(request.HandoverSummary)||Missing(request.ReceivingManager))return BadRequest(new{message="A different destination branch, reason, handover summary, and receiving manager are required."});
        if(!await db.Branches.AsNoTracking().AnyAsync(x=>x.Id==request.DestinationBranchId&&x.OrganizationId==tenant.OrganizationId,token))return NotFound(new{message="Destination branch was not found in this organization."});
        return await Transactional(async transaction=>{var id=Guid.NewGuid();var at=request.EffectiveAt??DateTimeOffset.UtcNow;
        await Exec("insert into person_transfers(id,service_user_id,organization_id,source_branch_id,destination_branch_id,effective_at,reason,handover_summary,receiving_manager,transferred_by) values(@id,@person,@organization,@source,@destination,@at,@reason,@handover,@manager,@actor)",personId,id,c=>{Add(c,"source",person.BranchId);Add(c,"destination",request.DestinationBranchId);Add(c,"at",at);Add(c,"reason",request.Reason.Trim());Add(c,"handover",request.HandoverSummary.Trim());Add(c,"manager",request.ReceivingManager.Trim());},token);
        await Exec("update \"PersonRecords\" set \"BranchId\"=@destination,\"LastReviewedAt\"=now() where \"ServiceUserId\"=@person and \"OrganizationId\"=@organization and \"BranchId\"=@source",personId,id,c=>{Add(c,"source",person.BranchId);Add(c,"destination",request.DestinationBranchId);},token);
        db.Entry(person).State=EntityState.Detached;db.ServiceUsers.Update(person with{BranchId=request.DestinationBranchId});Lifecycle(person,"Transferred",person.Status,request.Reason);Audit("person_transfer.completed","PersonTransfer",id);await db.SaveChangesAsync(token);return CreatedAtAction(nameof(Journey),new{personId},new{id});},token);
    }

    [HttpPost("discharges")]
    public async Task<IActionResult> Discharge(Guid personId,CreateDischargeRequest request,CancellationToken token)
    {
        var person=await Person(personId,token);if(person is null)return NotFound();if(person.Status is not("Active" or "Suspended"))return Conflict(new{message="Only an active or suspended person can be discharged."});
        if(Missing(request.Reason)||Missing(request.Destination)||Missing(request.Summary)||Missing(request.MedicationHandover)||!request.PropertyReturned)return BadRequest(new{message="Reason, destination, summary, medication handover, and property return confirmation are required."});if(request.FollowUpRequired&&Missing(request.FollowUpDetails))return BadRequest(new{message="Follow-up details are required."});
        var admission=await GuidScalar("select id from person_admissions where service_user_id=@person and organization_id=@organization and branch_id=@branch and status='Admitted' order by admitted_at desc limit 1",personId,token);if(admission is null)return Conflict(new{message="No active admission exists for this person."});
        return await Transactional(async transaction=>{var id=Guid.NewGuid();var at=request.DischargedAt??DateTimeOffset.UtcNow;
        await Exec("insert into person_discharges(id,service_user_id,organization_id,branch_id,admission_id,discharged_at,reason,destination,summary,medication_handover,property_returned,follow_up_required,follow_up_details,authorized_by) values(@id,@person,@organization,@branch,@admission,@at,@reason,@destination,@summary,@medication,true,@followup,@details,@actor);update person_admissions set status='Discharged' where id=@admission and organization_id=@organization",personId,id,c=>{Add(c,"admission",admission);Add(c,"at",at);Add(c,"reason",request.Reason.Trim());Add(c,"destination",request.Destination.Trim());Add(c,"summary",request.Summary.Trim());Add(c,"medication",request.MedicationHandover.Trim());Add(c,"followup",request.FollowUpRequired);Add(c,"details",request.FollowUpDetails?.Trim()??"");},token);
        await Exec("update \"PersonRecords\" set \"DischargedAt\"=@at,\"LastReviewedAt\"=now() where \"ServiceUserId\"=@person and \"OrganizationId\"=@organization and \"BranchId\"=@branch",personId,id,c=>Add(c,"at",at),token);
        db.Entry(person).State=EntityState.Detached;db.ServiceUsers.Update(person with{Status="Discharged"});Lifecycle(person,"Discharged","Discharged",request.Reason);Audit("person_discharge.created","PersonDischarge",id);await db.SaveChangesAsync(token);return CreatedAtAction(nameof(Journey),new{personId},new{id});},token);
    }

    private async Task<IActionResult> Transactional(Func<IDbContextTransaction,Task<IActionResult>> work,CancellationToken token)
    {
        var strategy=db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async()=>
        {
            await using var transaction=await db.Database.BeginTransactionAsync(token);
            var result=await work(transaction);
            await transaction.CommitAsync(token);
            return result;
        });
    }

    private async Task<ServiceUser?> Person(Guid id,CancellationToken token){var person=await db.ServiceUsers.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,token);return person is not null&&tenant.CanAccess(person.OrganizationId,person.BranchId)?person:null;}
    private void Lifecycle(ServiceUser person,string type,string next,string reason)=>db.Database.ExecuteSqlRaw("insert into person_lifecycle_events(id,service_user_id,organization_id,branch_id,event_type,old_status,new_status,reason,effective_at,created_by) values({0},{1},{2},{3},{4},{5},{6},{7},{8},{9})",Guid.NewGuid(),person.Id,tenant.OrganizationId,person.BranchId??TenantDefaults.BranchId,type,person.Status,next,reason,DateTimeOffset.UtcNow,user.UserName??"system");
    private void Audit(string action,string entity,Guid id)=>db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),action,user.UserName,entity,id,DateTimeOffset.UtcNow,tenant.OrganizationId,tenant.BranchId??TenantDefaults.BranchId));
    private async Task<List<T>> Query<T>(string sql,Guid person,Func<DbDataReader,T> map,CancellationToken token){var items=new List<T>();await using var command=await Command(sql,person,null,token);await using var reader=await command.ExecuteReaderAsync(token);while(await reader.ReadAsync(token))items.Add(map(reader));return items;}
    private async Task<long> Scalar(string sql,Guid person,Guid? id,CancellationToken token){await using var command=await Command(sql,person,id,token);return Convert.ToInt64(await command.ExecuteScalarAsync(token));}
    private async Task<Guid?> GuidScalar(string sql,Guid person,CancellationToken token){await using var command=await Command(sql,person,null,token);var value=await command.ExecuteScalarAsync(token);return value is Guid id?id:null;}
    private async Task Exec(string sql,Guid person,Guid id,Action<DbCommand> bind,CancellationToken token){await using var command=await Command(sql,person,id,token);bind(command);await command.ExecuteNonQueryAsync(token);}
    private async Task<DbCommand> Command(string sql,Guid person,Guid? id,CancellationToken token){var connection=db.Database.GetDbConnection();if(connection.State!=System.Data.ConnectionState.Open)await connection.OpenAsync(token);var command=connection.CreateCommand();command.CommandText=sql;command.Transaction=db.Database.CurrentTransaction?.GetDbTransaction();Add(command,"person",person);Add(command,"organization",tenant.OrganizationId);Add(command,"branch",tenant.BranchId??TenantDefaults.BranchId);Add(command,"actor",user.UserName);if(id is not null)Add(command,"id",id);return command;}
    private static void Add(DbCommand command,string name,object? value){if(command.Parameters.Contains(name))return;var parameter=command.CreateParameter();parameter.ParameterName=name;parameter.Value=value??DBNull.Value;command.Parameters.Add(parameter);}
    private static bool Missing(string? value)=>string.IsNullOrWhiteSpace(value);
}

public sealed record CreateAdmissionRequest(Guid? ReferralId,string AdmissionType,DateTimeOffset? PlannedAt,DateTimeOffset? AdmittedAt,bool FundingConfirmed,bool InitialPlanConfirmed,bool MedicationReconciled,string? Notes);
public sealed record CreateTransferRequest(Guid DestinationBranchId,DateTimeOffset? EffectiveAt,string Reason,string HandoverSummary,string ReceivingManager);
public sealed record CreateDischargeRequest(DateTimeOffset? DischargedAt,string Reason,string Destination,string Summary,string MedicationHandover,bool PropertyReturned,bool FollowUpRequired,string? FollowUpDetails);
public sealed record AdmissionResponse(Guid Id,Guid? ReferralId,string AdmissionType,DateTimeOffset? PlannedAt,DateTimeOffset AdmittedAt,string Status,bool FundingConfirmed,bool InitialPlanConfirmed,bool MedicationReconciled,string Notes,string AdmittedBy);
public sealed record TransferResponse(Guid Id,Guid SourceBranchId,Guid DestinationBranchId,DateTimeOffset EffectiveAt,string Reason,string HandoverSummary,string ReceivingManager,string TransferredBy);
public sealed record DischargeResponse(Guid Id,Guid AdmissionId,DateTimeOffset DischargedAt,string Reason,string Destination,string Summary,string MedicationHandover,bool PropertyReturned,bool FollowUpRequired,string FollowUpDetails,string AuthorizedBy);
