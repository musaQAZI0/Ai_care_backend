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
[ApiController,Authorize(Policy="Phase1User",Roles="CareCoordinator,CareManager,Administrator,CareWorker")]
[RequestSizeLimit(1024*1024)]
[Route("api/phase1/service-users/{personId:guid}/custom-forms")]
public sealed class CustomFormsController(CareDbContext db,ITenantContext tenant,ICurrentUserContext user,IContextualAuthorization authorization):ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Workspace(Guid personId,[FromQuery]int page=1,CancellationToken t=default)
    {
        if(page is <1 or >1000000)return BadRequest(new{message="Form history page is out of range."});
        var person=await Person(personId,t);if(person is null)return NotFound();
        var templates=await Templates(person.BranchId,t);
        var submissions=await Rows("""
            select s.id,s.template_version_id,s.supersedes_id,s.reason,s.answers,s.created_by,s.created_at,
              v.title,v.version,v.template_key,v.branch_id template_branch_id,
              exists(select 1 from custom_form_submissions successor where successor.supersedes_id=s.id) superseded,
              (select jsonb_object_agg(f->>'Id',f->>'Label') from jsonb_array_elements(v.definition_json) f) field_labels
            from custom_form_submissions s join assessment_template_versions v on v.id=s.template_version_id
            where s.organization_id=@org and s.branch_id=@target and s.service_user_id=@person
            order by s.created_at desc,s.id limit 101 offset @skip
            """,c=>{Add(c,"target",person.BranchId);Add(c,"person",personId);Add(c,"skip",(page-1)*100);},t);
        return Ok(new{templates,submissions=submissions.Take(100),page,hasMore=submissions.Count>100});
    }
    [HttpPost]
    public async Task<IActionResult> Submit(Guid personId,CustomFormRequest r,CancellationToken t)
    {
        if(user.UserId is null)return Unauthorized();
        if(r.RequestId==Guid.Empty||r.TemplateVersionId==Guid.Empty||string.IsNullOrWhiteSpace(r.AnswersJson)||r.AnswersJson.Length>500000||
           r.Reason is null||r.Reason.Length>2000||(r.SupersedesId is not null&&r.Reason.Trim().Length<10))
            return BadRequest(new{message="Provide immutable request/template IDs, bounded answers and a correction reason when superseding a record."});
        try{return await db.Database.CreateExecutionStrategy().ExecuteAsync(async()=>{
            await using var tx=await db.Database.BeginTransactionAsync(t);
            await Exec("select pg_advisory_xact_lock(hashtextextended(@key,0))",c=>Add(c,"key","custom-form:"+tenant.OrganizationId+":"+personId),t);
            var person=await Person(personId,t);if(person is null)return (IActionResult)NotFound();
            var snapshot=JsonSerializer.Serialize(r);
            var replay=await Rows("select request_snapshot=cast(@snapshot as jsonb) and created_by=@actor matches from custom_form_submissions where id=@id and organization_id=@org and branch_id=@target and service_user_id=@person",c=>{Add(c,"snapshot",snapshot);Add(c,"id",r.RequestId);Add(c,"target",person.BranchId);Add(c,"person",personId);},t);
            if(replay.Count>0)return replay[0].GetProperty("matches").GetBoolean()?(IActionResult)Ok(new{id=r.RequestId,replayed=true}):Conflict(new{message="This immutable request ID has different answers or an author mismatch."});
            var templates=await Templates(person.BranchId,t,r.TemplateVersionId);
            if(templates.Count!=1)return (IActionResult)Conflict(new{message="Select an accessible published Custom form version."});
            var error=AssessmentTemplateValidation.Answers(templates[0].Fields,r.AnswersJson,true);
            if(error is not null)return (IActionResult)BadRequest(new{message=error});
            await Exec("""
                insert into custom_form_submissions(id,organization_id,branch_id,service_user_id,template_version_id,supersedes_id,reason,answers,request_snapshot,created_by)
                values(@id,@org,@target,@person,@template,@prior,@reason,cast(@answers as jsonb),cast(@snapshot as jsonb),@actor)
                """,c=>{Add(c,"id",r.RequestId);Add(c,"target",person.BranchId);Add(c,"person",personId);Add(c,"template",r.TemplateVersionId);
                    var p=c.CreateParameter();p.ParameterName="prior";p.DbType=DbType.Guid;p.Value=(object?)r.SupersedesId??DBNull.Value;c.Parameters.Add(p);
                    Add(c,"reason",r.Reason.Trim());Add(c,"answers",r.AnswersJson);Add(c,"snapshot",snapshot);},t);
            db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),r.SupersedesId.HasValue?"custom_form.corrected":"custom_form.submitted",user.UserName,"CustomFormSubmission",r.RequestId,DateTimeOffset.UtcNow,tenant.OrganizationId,person.BranchId));
            await db.SaveChangesAsync(t);await tx.CommitAsync(t);return (IActionResult)Created("",new{id=r.RequestId});
        });}catch(Npgsql.PostgresException e)when(e.SqlState is "23505" or "23514" or "23503"){return Conflict(new{message="Form scope, publication, correction lineage or duplicate request conflict. Reload before retrying."});}
    }
    async Task<ServiceUser?> Person(Guid id,CancellationToken t)
    {
        var person=await db.ServiceUsers.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id&&x.OrganizationId==tenant.OrganizationId,t);
        return person is not null&&person.BranchId is not null&&tenant.CanAccess(person.OrganizationId,person.BranchId)&&await authorization.CanReadServiceUserAsync(id,t)?person:null;
    }
    async Task<List<CustomFormTemplate>> Templates(Guid? branch,CancellationToken t,Guid? id=null)
    {
        await using var c=await Command("select id,template_key,title,version,definition_json::text,branch_id from assessment_template_versions where organization_id=@org and (branch_id is null or branch_id=@target) and assessment_type='Custom form' and status='Published'"+(id.HasValue?" and id=@id":"")+" order by template_key,version desc",t);
        Add(c,"target",branch);if(id.HasValue)Add(c,"id",id.Value);await using var reader=await c.ExecuteReaderAsync(t);var rows=new List<CustomFormTemplate>();
        while(await reader.ReadAsync(t))rows.Add(new(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetInt32(3),JsonSerializer.Deserialize<AssessmentField[]>(reader.GetString(4))!,reader.IsDBNull(5)?null:reader.GetGuid(5)));return rows;
    }
    async Task<List<JsonElement>> Rows(string sql,Action<DbCommand> bind,CancellationToken t){await using var c=await Command("select row_to_json(q)::text from ("+sql+") q",t);bind(c);await using var reader=await c.ExecuteReaderAsync(t);var rows=new List<JsonElement>();while(await reader.ReadAsync(t))rows.Add(JsonSerializer.Deserialize<JsonElement>(reader.GetString(0)));return rows;}
    async Task Exec(string sql,Action<DbCommand> bind,CancellationToken t){await using var c=await Command(sql,t);bind(c);await c.ExecuteNonQueryAsync(t);}
    async Task<DbCommand> Command(string sql,CancellationToken t){var connection=db.Database.GetDbConnection();if(connection.State!=ConnectionState.Open)await connection.OpenAsync(t);var c=connection.CreateCommand();c.Transaction=db.Database.CurrentTransaction?.GetDbTransaction();c.CommandText=sql;Add(c,"org",tenant.OrganizationId);Add(c,"actor",user.UserId);return c;}
    static void Add(DbCommand c,string name,object? value){var p=c.CreateParameter();p.ParameterName=name;p.Value=value??DBNull.Value;c.Parameters.Add(p);}
}
public sealed record CustomFormTemplate(Guid Id,string Key,string Title,int Version,AssessmentField[] Fields,Guid? BranchId);
public sealed record CustomFormRequest(Guid RequestId,Guid TemplateVersionId,string AnswersJson,Guid? SupersedesId,string Reason);
