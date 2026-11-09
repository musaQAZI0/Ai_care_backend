using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

public sealed partial class ProductionEmarController
{
    [HttpGet("people/{personId:guid}/timeline.json")]
    [Authorize(Roles="Administrator,CareManager")]
    public Task<IActionResult> ExportClinicalTimeline(Guid personId,DateTimeOffset from,DateTimeOffset to,CancellationToken token)
        => db.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async () =>
        {
            from=from.ToUniversalTime();to=to.ToUniversalTime();
            if(from==default || to==default || from>=to || to-from>TimeSpan.FromDays(31))
                return BadRequest(new { message="Choose an explicit start and exclusive end, covering at most 31 days." });
            var branch=tenant.BranchId??AiCare.Domain.TenantDefaults.BranchId;
            var person=await db.ServiceUsers.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==personId&&x.OrganizationId==tenant.OrganizationId&&x.BranchId==branch,token);
            if(person is null) return NotFound();
            await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,token);
            void Bind(System.Data.Common.DbCommand c) { Add(c,"person",personId); Add(c,"from",from); Add(c,"to",to); }
            var entries=await Query("""
                select to_jsonb(l)::text from emar_ledger l where l.organization_id=@organization and l.branch_id=@branch
                  and l.service_user_id=@person and l.created_at>=@from and l.created_at<@to order by l.created_at,l.id
                """,Bind,r=>JsonSerializer.Deserialize<JsonElement>(r.GetString(0)),token);
            var stock=await Query("""
                select to_jsonb(s)::text from medication_stock_transactions s join "Medications" m on m."Id"=s.medication_id
                  and m."OrganizationId"=s.organization_id and m."BranchId"=s.branch_id
                where s.organization_id=@organization and s.branch_id=@branch and m."ServiceUserId"=@person
                  and s.created_at>=@from and s.created_at<@to order by s.created_at,s.id
                """,Bind,r=>JsonSerializer.Deserialize<JsonElement>(r.GetString(0)),token);
            var escalations=await Query("""
                select to_jsonb(e)::text from emar_escalations e join "MedicationAdministrationRecords" r on r."Id"=e.mar_record_id
                  and r."OrganizationId"=e.organization_id and r."BranchId"=e.branch_id
                join "Medications" m on m."Id"=r."MedicationId" and m."OrganizationId"=r."OrganizationId" and m."BranchId"=r."BranchId"
                where e.organization_id=@organization and e.branch_id=@branch and m."ServiceUserId"=@person
                  and e.created_at>=@from and e.created_at<@to order by e.created_at,e.id
                """,Bind,r=>JsonSerializer.Deserialize<JsonElement>(r.GetString(0)),token);
            var medicationAlerts=await Query("""
                select to_jsonb(a)::text from medication_monitoring_alerts a join "Medications" m on m."Id"=a.medication_id
                  and m."OrganizationId"=a.organization_id and m."BranchId"=a.branch_id
                where a.organization_id=@organization and a.branch_id=@branch and m."ServiceUserId"=@person
                  and a.created_at>=@from and a.created_at<@to order by a.created_at,a.id
                """,Bind,r=>JsonSerializer.Deserialize<JsonElement>(r.GetString(0)),token);
            var orders=await Query("""
                select r."Id" as id,r."MedicationOrderSnapshotJson"::text from "MedicationAdministrationRecords" r
                join "Medications" m on m."Id"=r."MedicationId" and m."OrganizationId"=r."OrganizationId" and m."BranchId"=r."BranchId"
                where r."OrganizationId"=@organization and r."BranchId"=@branch and m."ServiceUserId"=@person
                  and exists(select 1 from emar_ledger l where l.mar_record_id=r."Id" and l.organization_id=@organization
                    and l.branch_id=@branch and l.created_at>=@from and l.created_at<@to)
                order by r."Id"
                """,Bind,r=>new { marId=r.GetGuid(0), snapshot=r.IsDBNull(1)?(JsonElement?)null:JsonSerializer.Deserialize<JsonElement>(r.GetString(1)) },token);
            db.AuditEvents.Add(new AiCare.Domain.AuditEvent(Guid.NewGuid(),"emar.clinical_timeline_exported",user.UserName,"ServiceUser",personId,DateTimeOffset.UtcNow,tenant.OrganizationId,branch));
            await db.SaveChangesAsync(token);
            var bytes=JsonSerializer.SerializeToUtf8Bytes(new { personId, organizationId=tenant.OrganizationId, branchId=branch,
                from,to, interval="created_at >= from and created_at < to; timestamps in UTC", generatedAt=DateTimeOffset.UtcNow,
                entries,stock,escalations,medicationAlerts,orders },new JsonSerializerOptions { WriteIndented=true });
            await tx.CommitAsync(token);
            return File(bytes,"application/json",$"emar-timeline-{personId:N}-{from:yyyyMMdd}.json");
        });
}
