using System.Data;
using System.Data.Common;
using System.Text.Json;
using AiCare.Application;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[ApiController]
[Authorize(Roles="CareCoordinator,CareManager,Administrator")]
[Route("api/phase1/service-users/{personId:guid}/clinical-governance")]
public sealed class AssessmentRiskGovernanceController(CareDbContext db,ITenantContext tenant,ICurrentUserContext user):ControllerBase
{
 [HttpGet]
 public async Task<IActionResult> Get(Guid personId,CancellationToken token)
 {
  if(!await CanAccess(personId,token))return NotFound();
  return Ok(new{assessments=await Assessments(personId,token),risks=await Risks(personId,token),alerts=await Alerts(personId,token)});
 }

 [HttpPost("assessments")]
 public async Task<IActionResult> CreateAssessment(Guid personId,AssessmentDraftRequest request,CancellationToken token)
 {
  if(!await CanAccess(personId,token))return NotFound();if(!Valid(request, out var message))return BadRequest(new{message});
  try{JsonDocument.Parse(request.AnswersJson);}catch(JsonException){return BadRequest(new{message="Assessment answers must be valid JSON."});}
  var current=await Current("governed_assessments","assessment_type",personId,request.AssessmentType,token);if(current is not null)return Conflict(new{message="Create a correction from the current assessment instead."});
  var id=Guid.NewGuid();await InsertAssessment(id,personId,request,1,null,"",token);Audit("assessment.draft_created","GovernedAssessment",id);await db.SaveChangesAsync(token);return CreatedAtAction(nameof(Get),new{personId},new{id,version=1,status="Draft"});
 }

 [HttpPost("assessments/{id:guid}/submit")]
 public Task<IActionResult> SubmitAssessment(Guid personId,Guid id,CancellationToken token)=>Transition(personId,id,"governed_assessments","Draft","InReview","assessment.submitted",token);

 [HttpPost("assessments/{id:guid}/approve")]
 [Authorize(Roles="CareManager,Administrator")]
 public Task<IActionResult> ApproveAssessment(Guid personId,Guid id,ApprovalRequest request,CancellationToken token)=>Approve(personId,id,"governed_assessments","assessment_type","assessment.approved",request,token);

 [HttpPost("assessments/{id:guid}/corrections")]
 public async Task<IActionResult> CorrectAssessment(Guid personId,Guid id,AssessmentDraftRequest request,CancellationToken token)
 {
  if(!await CanAccess(personId,token))return NotFound();if(string.IsNullOrWhiteSpace(request.ChangeReason))return BadRequest(new{message="A correction reason is required."});if(!Valid(request,out var message))return BadRequest(new{message});try{JsonDocument.Parse(request.AnswersJson);}catch(JsonException){return BadRequest(new{message="Assessment answers must be valid JSON."});}
  var source=await Version("governed_assessments",personId,id,"assessment_type",token);if(source is null||source.Value.Status!="Current")return Conflict(new{message="Only the current assessment can be corrected."});if(!source.Value.Key.Equals(request.AssessmentType,StringComparison.OrdinalIgnoreCase))return BadRequest(new{message="A correction cannot change assessment type."});
  var next=Guid.NewGuid();await InsertAssessment(next,personId,request,source.Value.Version+1,id,request.ChangeReason.Trim(),token);Audit("assessment.correction_created","GovernedAssessment",next);await db.SaveChangesAsync(token);return CreatedAtAction(nameof(Get),new{personId},new{id=next,version=source.Value.Version+1,status="Draft"});
 }

 [HttpPost("risks")]
 public async Task<IActionResult> CreateRisk(Guid personId,RiskDraftRequest request,CancellationToken token)
 {
  if(!await CanAccess(personId,token))return NotFound();if(!Valid(request,out var message))return BadRequest(new{message});var current=await Current("governed_risk_assessments","category",personId,request.Category,token);if(current is not null)return Conflict(new{message="Create a correction from the current risk assessment instead."});
  var id=Guid.NewGuid();await InsertRisk(id,personId,request,1,null,"",token);Audit("risk_assessment.draft_created","GovernedRiskAssessment",id);await db.SaveChangesAsync(token);return CreatedAtAction(nameof(Get),new{personId},new{id,version=1,status="Draft",score=request.Likelihood*request.Severity});
 }

 [HttpPost("risks/{id:guid}/submit")]
 public Task<IActionResult> SubmitRisk(Guid personId,Guid id,CancellationToken token)=>Transition(personId,id,"governed_risk_assessments","Draft","InReview","risk_assessment.submitted",token);

 [HttpPost("risks/{id:guid}/approve")]
 [Authorize(Roles="CareManager,Administrator")]
 public Task<IActionResult> ApproveRisk(Guid personId,Guid id,ApprovalRequest request,CancellationToken token)=>Approve(personId,id,"governed_risk_assessments","category","risk_assessment.approved",request,token);

 [HttpPost("risks/{id:guid}/corrections")]
 public async Task<IActionResult> CorrectRisk(Guid personId,Guid id,RiskDraftRequest request,CancellationToken token)
 {
  if(!await CanAccess(personId,token))return NotFound();if(string.IsNullOrWhiteSpace(request.ChangeReason))return BadRequest(new{message="A correction reason is required."});if(!Valid(request,out var message))return BadRequest(new{message});var source=await Version("governed_risk_assessments",personId,id,"category",token);if(source is null||source.Value.Status!="Current")return Conflict(new{message="Only the current risk assessment can be corrected."});if(!source.Value.Key.Equals(request.Category,StringComparison.OrdinalIgnoreCase))return BadRequest(new{message="A correction cannot change risk category."});
  var next=Guid.NewGuid();await InsertRisk(next,personId,request,source.Value.Version+1,id,request.ChangeReason.Trim(),token);Audit("risk_assessment.correction_created","GovernedRiskAssessment",next);await db.SaveChangesAsync(token);return CreatedAtAction(nameof(Get),new{personId},new{id=next,version=source.Value.Version+1,status="Draft",score=request.Likelihood*request.Severity});
 }

 private async Task<IActionResult> Transition(Guid person,Guid id,string table,string from,string to,string action,CancellationToken token){if(!await CanAccess(person,token))return NotFound();var changed=await Exec($"update {table} set status=@to,submitted_at=case when @to='InReview' then now() else submitted_at end where id=@id and service_user_id=@person and organization_id=@organization and branch_id=@branch and status=@from",person,c=>{Add(c,"id",id);Add(c,"from",from);Add(c,"to",to);},token);if(changed==0)return Conflict(new{message=$"Record must be {from} before this action."});Audit(action,table,id);await db.SaveChangesAsync(token);return NoContent();}
 private async Task<IActionResult> Approve(Guid person,Guid id,string table,string keyColumn,string action,ApprovalRequest request,CancellationToken token)
 {
  if(!await CanAccess(person,token))return NotFound();if(string.IsNullOrWhiteSpace(request.SignerName)||string.IsNullOrWhiteSpace(request.Declaration))return BadRequest(new{message="Approver name and signature declaration are required."});
  var source=await Version(table,person,id,keyColumn,token);if(source is null||source.Value.Status!="InReview")return Conflict(new{message="Record must be in review before approval."});
  var strategy=db.Database.CreateExecutionStrategy();await strategy.ExecuteAsync(async()=>{await using var transaction=await db.Database.BeginTransactionAsync(token);await Exec($"update {table} set status='Superseded' where service_user_id=@person and organization_id=@organization and lower({keyColumn})=lower(@key) and status='Current';update {table} set status='Current',approved_at=now(),approved_by=@actor,signed_at=now(),signed_by=@signer,signature_declaration=@declaration where id=@id and service_user_id=@person and organization_id=@organization and status='InReview'",person,c=>{Add(c,"id",id);Add(c,"key",source.Value.Key);Add(c,"signer",request.SignerName.Trim());Add(c,"declaration",request.Declaration.Trim());},token);Audit(action,table,id);await db.SaveChangesAsync(token);await transaction.CommitAsync(token);});return NoContent();
 }
 private async Task InsertAssessment(Guid id,Guid person,AssessmentDraftRequest r,int version,Guid? previous,string reason,CancellationToken token)=>await Exec("insert into governed_assessments(id,service_user_id,organization_id,branch_id,assessment_type,template_key,template_version,answers_json,score,risk_level,summary,recommended_actions,status,version,supersedes_id,change_reason,assessor_name,assessor_role,review_due_at,created_by) values(@id,@person,@organization,@branch,@type,@template,@templateVersion,cast(@answers as jsonb),@score,@risk,@summary,@actions,'Draft',@version,@previous,@reason,@assessor,@role,@review,@actor)",person,c=>{Add(c,"id",id);Add(c,"type",r.AssessmentType.Trim());Add(c,"template",r.TemplateKey.Trim());Add(c,"templateVersion",r.TemplateVersion.Trim());Add(c,"answers",r.AnswersJson);Add(c,"score",r.Score);Add(c,"risk",r.RiskLevel.Trim());Add(c,"summary",r.Summary.Trim());Add(c,"actions",r.RecommendedActions.Trim());Add(c,"version",version);Add(c,"previous",previous);Add(c,"reason",reason);Add(c,"assessor",r.AssessorName.Trim());Add(c,"role",r.AssessorRole.Trim());Add(c,"review",r.ReviewDueAt);},token);
 private async Task InsertRisk(Guid id,Guid person,RiskDraftRequest r,int version,Guid? previous,string reason,CancellationToken token)=>await Exec("insert into governed_risk_assessments(id,service_user_id,organization_id,branch_id,category,hazard,likelihood,severity,score,risk_level,controls,contingency,owner,status,version,supersedes_id,change_reason,review_due_at,created_by) values(@id,@person,@organization,@branch,@category,@hazard,@likelihood,@severity,@score,@risk,@controls,@contingency,@owner,'Draft',@version,@previous,@reason,@review,@actor)",person,c=>{Add(c,"id",id);Add(c,"category",r.Category.Trim());Add(c,"hazard",r.Hazard.Trim());Add(c,"likelihood",r.Likelihood);Add(c,"severity",r.Severity);Add(c,"score",r.Likelihood*r.Severity);Add(c,"risk",Level(r.Likelihood*r.Severity));Add(c,"controls",r.Controls.Trim());Add(c,"contingency",r.Contingency.Trim());Add(c,"owner",r.Owner.Trim());Add(c,"version",version);Add(c,"previous",previous);Add(c,"reason",reason);Add(c,"review",r.ReviewDueAt);},token);
 private Task<List<object>> Assessments(Guid p,CancellationToken t)=>Query("select id,assessment_type,template_key,template_version,score,risk_level,summary,recommended_actions,status,version,supersedes_id,change_reason,assessor_name,assessor_role,review_due_at,approved_by,signed_by,created_at from governed_assessments where service_user_id=@person and organization_id=@organization order by created_at desc",p,r=>(object)new{id=r.GetGuid(0),assessmentType=r.GetString(1),templateKey=r.GetString(2),templateVersion=r.GetString(3),score=r.GetInt32(4),riskLevel=r.GetString(5),summary=r.GetString(6),recommendedActions=r.GetString(7),status=r.GetString(8),version=r.GetInt32(9),supersedesId=r.IsDBNull(10)?null:(Guid?)r.GetGuid(10),changeReason=r.GetString(11),assessorName=r.GetString(12),assessorRole=r.GetString(13),reviewDueAt=r.GetFieldValue<DateTimeOffset>(14),approvedBy=r.GetString(15),signedBy=r.GetString(16),createdAt=r.GetFieldValue<DateTimeOffset>(17)},t);
 private Task<List<object>> Risks(Guid p,CancellationToken t)=>Query("select id,category,hazard,likelihood,severity,score,risk_level,controls,contingency,owner,status,version,supersedes_id,change_reason,review_due_at,approved_by,signed_by,created_at from governed_risk_assessments where service_user_id=@person and organization_id=@organization order by created_at desc",p,r=>(object)new{id=r.GetGuid(0),category=r.GetString(1),hazard=r.GetString(2),likelihood=r.GetInt32(3),severity=r.GetInt32(4),score=r.GetInt32(5),riskLevel=r.GetString(6),controls=r.GetString(7),contingency=r.GetString(8),owner=r.GetString(9),status=r.GetString(10),version=r.GetInt32(11),supersedesId=r.IsDBNull(12)?null:(Guid?)r.GetGuid(12),changeReason=r.GetString(13),reviewDueAt=r.GetFieldValue<DateTimeOffset>(14),approvedBy=r.GetString(15),signedBy=r.GetString(16),createdAt=r.GetFieldValue<DateTimeOffset>(17)},t);
 private static bool Valid(AssessmentDraftRequest r,out string message){message="Assessment type, template/version, summary, actions, assessor, role, risk, and future review are required.";return !Missing(r.AssessmentType,r.TemplateKey,r.TemplateVersion,r.Summary,r.RecommendedActions,r.AssessorName,r.AssessorRole,r.RiskLevel)&&r.ReviewDueAt>DateTimeOffset.UtcNow;}
 private static bool Valid(RiskDraftRequest r,out string message){message="Risk category, hazard, controls, contingency, owner, 1-5 scores, and future review are required.";return !Missing(r.Category,r.Hazard,r.Controls,r.Contingency,r.Owner)&&r.Likelihood is>=1 and<=5&&r.Severity is>=1 and<=5&&r.ReviewDueAt>DateTimeOffset.UtcNow;}
 private static string Level(int score)=>score>=15?"Critical":score>=10?"High":score>=5?"Medium":"Low";private static bool Missing(params string?[] values)=>values.Any(string.IsNullOrWhiteSpace);
 private async Task<(Guid Id,int Version)?> Current(string table,string key,Guid person,string value,CancellationToken token){await using var c=await Command($"select id,version from {table} where service_user_id=@person and organization_id=@organization and branch_id=@branch and lower({key})=lower(@key) and status='Current'",person,token);Add(c,"key",value.Trim());await using var r=await c.ExecuteReaderAsync(token);return await r.ReadAsync(token)?(r.GetGuid(0),r.GetInt32(1)):null;}
 private async Task<(string Key,int Version,string Status)?> Version(string table,Guid person,Guid id,string key,CancellationToken token){await using var c=await Command($"select {key},version,status from {table} where id=@id and service_user_id=@person and organization_id=@organization and branch_id=@branch",person,token);Add(c,"id",id);await using var r=await c.ExecuteReaderAsync(token);return await r.ReadAsync(token)?(r.GetString(0),r.GetInt32(1),r.GetString(2)):null;}
 private async Task<bool> CanAccess(Guid person,CancellationToken token)=>await db.ServiceUsers.AsNoTracking().AnyAsync(x=>x.Id==person&&x.OrganizationId==tenant.OrganizationId&&(tenant.BranchId==null||x.BranchId==tenant.BranchId),token);
 private void Audit(string action,string entity,Guid id)=>db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),action,user.UserName,entity,id,DateTimeOffset.UtcNow,tenant.OrganizationId,tenant.BranchId??TenantDefaults.BranchId));
 private async Task<int> Exec(string sql,Guid person,Action<DbCommand> configure,CancellationToken token){await using var c=await Command(sql,person,token);configure(c);return await c.ExecuteNonQueryAsync(token);}
 private async Task<DbCommand> Command(string sql,Guid person,CancellationToken token){var connection=db.Database.GetDbConnection();if(connection.State!=ConnectionState.Open)await connection.OpenAsync(token);var c=connection.CreateCommand();c.CommandText=sql;Add(c,"person",person);Add(c,"organization",tenant.OrganizationId);Add(c,"branch",tenant.BranchId??TenantDefaults.BranchId);Add(c,"actor",user.UserName);return c;}
 private async Task<List<T>> Query<T>(string sql,Guid person,Func<DbDataReader,T> map,CancellationToken token){await using var c=await Command(sql,person,token);await using var r=await c.ExecuteReaderAsync(token);var values=new List<T>();while(await r.ReadAsync(token))values.Add(map(r));return values;}
 private Task<List<object>> Alerts(Guid p,CancellationToken t)=>Query("select record_type,record_id,title,severity,due_at,message from (select 'Assessment' record_type,id record_id,assessment_type title,case when review_due_at<=now() then 'Overdue' else 'DueSoon' end severity,review_due_at due_at,case when review_due_at<=now() then 'Assessment review is overdue' else 'Assessment review is due within 14 days' end message from governed_assessments where service_user_id=@person and organization_id=@organization and branch_id=@branch and status='Current' and review_due_at<=now()+interval '14 days' union all select 'Risk',id,category,case when risk_level='Critical' then 'Critical' when review_due_at<=now() then 'Overdue' else 'DueSoon' end,review_due_at,case when risk_level='Critical' then 'Critical risk requires manager oversight' when review_due_at<=now() then 'Risk review is overdue' else 'Risk review is due within 14 days' end from governed_risk_assessments where service_user_id=@person and organization_id=@organization and branch_id=@branch and status='Current' and (risk_level='Critical' or review_due_at<=now()+interval '14 days')) alerts order by case severity when 'Critical' then 0 when 'Overdue' then 1 else 2 end,due_at",p,r=>(object)new{recordType=r.GetString(0),recordId=r.GetGuid(1),title=r.GetString(2),severity=r.GetString(3),dueAt=r.GetFieldValue<DateTimeOffset>(4),message=r.GetString(5)},t);
 private static void Add(DbCommand c,string name,object? value){var p=c.CreateParameter();p.ParameterName=name;p.Value=value??DBNull.Value;c.Parameters.Add(p);}
}

public sealed record AssessmentDraftRequest(string AssessmentType,string TemplateKey,string TemplateVersion,string AnswersJson,int Score,string RiskLevel,string Summary,string RecommendedActions,string AssessorName,string AssessorRole,DateTimeOffset ReviewDueAt,string? ChangeReason);
public sealed record RiskDraftRequest(string Category,string Hazard,int Likelihood,int Severity,string Controls,string Contingency,string Owner,DateTimeOffset ReviewDueAt,string? ChangeReason);
public sealed record ApprovalRequest(string SignerName,string Declaration);
