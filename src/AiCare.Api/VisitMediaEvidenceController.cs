using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
namespace AiCare.Api;
[ApiController,Authorize(Policy="Phase1User",Roles="CareWorker,CareCoordinator,CareManager,Administrator")]
[Route("api/phase1/visits/{visitId:guid}/media-evidence")]
public sealed class VisitMediaEvidenceController(CareDbContext db,ITenantContext tenant,ICurrentUserContext user,IContextualAuthorization authorization):ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(Guid visitId,[FromQuery]int page=1,CancellationToken t=default)
    {
        if(page is <1 or >1000000)return BadRequest();var visit=await Visit(visitId,t);if(visit is null)return NotFound();
        var rows=await Rows("""
            select e.id,e.kind,e.caption,e.signer_name,e.declaration,e.capture_source,e.file_name,e.content_type,e.sha256,e.recorded_by,e.recorded_at,
              w.reason withdrawal_reason,w.withdrawn_at
            from visit_media_evidence e left join visit_media_withdrawals w on w.evidence_id=e.id
            where e.organization_id=@org and e.branch_id=@target and e.visit_id=@visit order by e.recorded_at desc,e.id limit 51 offset @skip
            """,c=>{Add(c,"target",visit.BranchId);Add(c,"visit",visitId);Add(c,"skip",(page-1)*50);},t);
        return Ok(new{items=rows.Take(50),page,hasMore=rows.Count>50});
    }
    [HttpPost,RequestSizeLimit(3*1024*1024)]
    public async Task<IActionResult> Upload(Guid visitId,[FromForm]VisitMediaUpload r,IFormFile file,[FromServices]IDocumentMalwareScanner scanner,CancellationToken t)
    {
        if(user.UserId is null)return Unauthorized();if(await Visit(visitId,t) is null)return NotFound();
        if(r.RequestId==Guid.Empty||r.Kind is not ("Photo" or "Signature")||string.IsNullOrWhiteSpace(r.Caption)||r.Caption.Trim().Length is <5 or >1000||
           r.CaptureSource is not ("Camera" or "File" or "Drawn")||!r.ConsentConfirmed||file is null||
           (r.Kind=="Photo"&&(r.CaptureSource=="Drawn"||!string.IsNullOrWhiteSpace(r.SignerName)||!string.IsNullOrWhiteSpace(r.Declaration)))||
           (r.Kind=="Signature"&&r.CaptureSource=="Camera")||
           (r.SignerName?.Length??0)>200||(r.Declaration?.Length??0)>2000||
           (r.Kind=="Signature"&&(string.IsNullOrWhiteSpace(r.SignerName)||(r.Declaration?.Trim().Length??0)<10)))
            return BadRequest(new{message="Choose photo/signature evidence with a caption, capture source and consent confirmation. Signatures require a signer name and declaration."});
        var mime=file.ContentType.Split(';')[0].Trim();
        if(mime is not ("image/jpeg" or "image/png")||(r.Kind=="Signature"&&mime!="image/png"))return BadRequest(new{message="Photo evidence must be JPEG or PNG; signature evidence must be PNG."});
        var validation=await DocumentUploadValidator.ValidateAsync(file,2*1024*1024,scanner,t);
        if(!validation.Accepted)return StatusCode(validation.StatusCode,new{message=validation.Message});
        using var buffer=new MemoryStream();await file.CopyToAsync(buffer,t);var bytes=buffer.ToArray();var hash=Convert.ToHexString(SHA256.HashData(bytes));
        var name=Path.GetFileName(file.FileName.Replace('\\','/'));
        if(name.Length is <1 or >200||name.Any(char.IsControl))return BadRequest(new{message="Use a bounded file name without control characters."});
        var snapshot=JsonSerializer.Serialize(new{r.Kind,Caption=r.Caption.Trim(),SignerName=r.SignerName?.Trim()??"",Declaration=r.Declaration?.Trim()??"",r.CaptureSource,r.ConsentConfirmed,Name=name,Mime=mime,Hash=hash});
        return await Tx(async()=>{
            var visit=await Visit(visitId,t);if(visit?.BranchId is not Guid branch)return NotFound();
            var prior=await Rows("select id,request_snapshot=cast(@snapshot as jsonb) and recorded_by=@actor matches from visit_media_evidence where organization_id=@org and branch_id=@target and visit_id=@visit and (id=@id or (kind=@kind and sha256=@hash))",c=>{Add(c,"snapshot",snapshot);Add(c,"target",branch);Add(c,"visit",visitId);Add(c,"id",r.RequestId);Add(c,"kind",r.Kind);Add(c,"hash",hash);},t);
            if(prior.Count>0)return prior.Count==1&&prior[0].GetProperty("matches").GetBoolean()?Ok(new{id=prior[0].GetProperty("id").GetGuid(),sha256=hash,replayed=true}):Conflict(new{message="Evidence ID or image already has different immutable metadata. Reload the evidence history."});
            await Exec("""
                insert into visit_media_evidence(id,organization_id,branch_id,visit_id,kind,caption,signer_name,declaration,capture_source,consent_confirmed,file_name,content_type,content,sha256,request_snapshot,recorded_by)
                values(@id,@org,@target,@visit,@kind,@caption,@signer,@declaration,@source,true,@name,@mime,@bytes,@hash,cast(@snapshot as jsonb),@actor)
                """,c=>{Add(c,"id",r.RequestId);Add(c,"target",branch);Add(c,"visit",visitId);Add(c,"kind",r.Kind);Add(c,"caption",r.Caption.Trim());Add(c,"signer",r.SignerName?.Trim()??"");Add(c,"declaration",r.Declaration?.Trim()??"");Add(c,"source",r.CaptureSource);Add(c,"name",name);Add(c,"mime",mime);Add(c,"bytes",bytes);Add(c,"hash",hash);Add(c,"snapshot",snapshot);},t);
            await Audit("visit.media_uploaded",r.RequestId,branch,t);return Created("",new{id=r.RequestId,sha256=hash});
        },visitId,t);
    }
    [HttpGet("{id:guid}/content")]
    public async Task<IActionResult> Content(Guid visitId,Guid id,CancellationToken t)
    {
        var visit=await Visit(visitId,t);if(visit is null)return NotFound();
        await using var c=await Command("select file_name,content_type,content from visit_media_evidence e where e.id=@id and e.organization_id=@org and e.branch_id=@target and e.visit_id=@visit and not exists(select 1 from visit_media_withdrawals w where w.evidence_id=e.id)",t);Add(c,"id",id);Add(c,"target",visit.BranchId);Add(c,"visit",visitId);
        await using var reader=await c.ExecuteReaderAsync(t);if(!await reader.ReadAsync(t))return NotFound();var name=reader.GetString(0);var mime=reader.GetString(1);var bytes=(byte[])reader[2];await reader.DisposeAsync();
        await Audit("visit.media_downloaded",id,visit.BranchId!.Value,t);Response.Headers.CacheControl="no-store";Response.Headers["X-Content-Type-Options"]="nosniff";return File(bytes,mime,name);
    }
    [HttpPost("{id:guid}/withdraw"),Authorize(Roles="CareManager,Administrator")]
    public Task<IActionResult> Withdraw(Guid visitId,Guid id,VisitMediaWithdrawal r,CancellationToken t)=>Tx(async()=>{
        if(string.IsNullOrWhiteSpace(r.Reason)||r.Reason.Trim().Length is <10 or >2000)return BadRequest(new{message="A withdrawal reason of 10–2,000 characters is required."});
        var visit=await Visit(visitId,t);if(visit?.BranchId is not Guid branch)return NotFound();
        var prior=await Rows("select e.id,w.reason from visit_media_evidence e left join visit_media_withdrawals w on w.evidence_id=e.id where e.id=@id and e.organization_id=@org and e.branch_id=@target and e.visit_id=@visit",c=>{Add(c,"id",id);Add(c,"target",branch);Add(c,"visit",visitId);},t);
        if(prior.Count!=1)return NotFound();if(prior[0].GetProperty("reason").ValueKind!=JsonValueKind.Null)return prior[0].GetProperty("reason").GetString()==r.Reason.Trim()?Ok(new{id,replayed=true}):Conflict(new{message="Evidence already has an immutable withdrawal reason."});
        await Exec("insert into visit_media_withdrawals(evidence_id,organization_id,branch_id,reason,withdrawn_by) values(@id,@org,@target,@reason,@actor)",c=>{Add(c,"id",id);Add(c,"target",branch);Add(c,"reason",r.Reason.Trim());},t);await Audit("visit.media_withdrawn",id,branch,t);return Ok(new{id});
    },visitId,t);
    async Task<Visit?> Visit(Guid id,CancellationToken t){var visit=await db.Visits.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id&&x.OrganizationId==tenant.OrganizationId,t);return visit is not null&&visit.BranchId.HasValue&&tenant.CanAccess(visit.OrganizationId,visit.BranchId)&&await authorization.CanOperateVisitAsync(id,t)?visit:null;}
    async Task<IActionResult> Tx(Func<Task<IActionResult>> work,Guid visitId,CancellationToken t){if(user.UserId is null)return Unauthorized();try{return await db.Database.CreateExecutionStrategy().ExecuteAsync(async()=>{await using var tx=await db.Database.BeginTransactionAsync(t);await Exec("select pg_advisory_xact_lock(hashtextextended(@key,0))",c=>Add(c,"key","visit-media:"+tenant.OrganizationId+":"+visitId),t);var result=await work();if(result is ObjectResult o&&o.StatusCode>=400||result is StatusCodeResult s&&s.StatusCode>=400)await tx.RollbackAsync(t);else await tx.CommitAsync(t);return result;});}catch(Npgsql.PostgresException e)when(e.SqlState is "23505" or "23514" or "23503"){return Conflict(new{message="Evidence scope, duplicate image or immutable history conflict. Reload before retrying."});}}
    async Task<List<JsonElement>> Rows(string sql,Action<DbCommand> bind,CancellationToken t){await using var c=await Command("select row_to_json(q)::text from ("+sql+") q",t);bind(c);await using var reader=await c.ExecuteReaderAsync(t);var rows=new List<JsonElement>();while(await reader.ReadAsync(t))rows.Add(JsonSerializer.Deserialize<JsonElement>(reader.GetString(0)));return rows;}
    async Task Exec(string sql,Action<DbCommand> bind,CancellationToken t){await using var c=await Command(sql,t);bind(c);await c.ExecuteNonQueryAsync(t);}
    async Task<DbCommand> Command(string sql,CancellationToken t){var connection=db.Database.GetDbConnection();if(connection.State!=ConnectionState.Open)await connection.OpenAsync(t);var c=connection.CreateCommand();c.Transaction=db.Database.CurrentTransaction?.GetDbTransaction();c.CommandText=sql;Add(c,"org",tenant.OrganizationId);Add(c,"actor",user.UserId);return c;}
    static void Add(DbCommand c,string name,object? value){var p=c.CreateParameter();p.ParameterName=name;p.Value=value??DBNull.Value;c.Parameters.Add(p);}
    async Task Audit(string action,Guid id,Guid branch,CancellationToken t){db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),action,user.UserName,"VisitMediaEvidence",id,DateTimeOffset.UtcNow,tenant.OrganizationId,branch));await db.SaveChangesAsync(t);}
}
public sealed class VisitMediaUpload
{
    public Guid RequestId{get;set;}public string Kind{get;set;}="";public string Caption{get;set;}="";public string? SignerName{get;set;}public string? Declaration{get;set;}public string CaptureSource{get;set;}="File";public bool ConsentConfirmed{get;set;}
}
public sealed record VisitMediaWithdrawal(string Reason);
