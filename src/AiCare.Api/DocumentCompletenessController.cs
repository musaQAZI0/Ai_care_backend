using System.Data;
using System.Text.Json;
using AiCare.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AiCare.Api;
public sealed partial class PersonOnboardingController
{
    private async Task<PersonOnboardingCheck[]> DocumentCompletenessChecks(Guid person, Guid branch, CancellationToken token)
    {
        var rows = await RemediationRows("""
            select r.id,r.title,r.category,r.expiry_required,
              exists(select 1 from "Documents" d join person_document_verifications v on v.document_id=d."Id"
                where d."ServiceUserId"=@person and d."OrganizationId"=@org and d."BranchId"=@branch
                  and v.service_user_id=@person and v.organization_id=@org and v.branch_id=@branch
                  and lower(trim(d."Category"))<>'withdrawn' and not exists(select 1 from document_lifecycle_events e where e."DocumentId"=d."Id") and lower(trim(d."Category"))=lower(trim(r.category)) and v.status='Verified' and v.document_snapshot=to_jsonb(d)
                  and (v.valid_until is null or v.valid_until>now()) and (not r.expiry_required or v.valid_until is not null)
                  and not exists(select 1 from person_document_verifications newer where newer.document_id=v.document_id and newer.revision>v.revision)) complete
            from person_document_requirements r where r.service_user_id=@person and r.organization_id=@org and r.branch_id=@branch and r.enabled
              and not exists(select 1 from person_document_requirements newer where newer.root_id=r.root_id and newer.version>r.version)
            order by r.title,r.id
            """, person, branch, _ => { }, token);
        return rows.Select(r => new PersonOnboardingCheck("document:" + r.GetProperty("id").GetGuid(), r.GetProperty("title").GetString()!,
            r.GetProperty("complete").GetBoolean(), "Verified document in category " + r.GetProperty("category").GetString()
                + (r.GetProperty("expiry_required").GetBoolean() ? " with a current expiry is required." : " is required."))).ToArray();
    }

    [HttpGet("document-requirements")]
    public async Task<IActionResult> DocumentRequirements(Guid serviceUserId, CancellationToken token)
    {
        var p = await FindAccessiblePerson(serviceUserId, token); if (p?.BranchId is not Guid branch) return NotFound();
        var requirements = await RemediationRows("select * from person_document_requirements where service_user_id=@person and organization_id=@org and branch_id=@branch order by root_id,version desc", serviceUserId, branch, _ => { }, token);
        var documents = await RemediationRows("""
            select d."Id" id,d."FileName" file_name,d."Category" category,
              (select coalesce(max(revision),0) from person_document_verifications where document_id=d."Id") revision,v.status,v.valid_until,
              coalesce(v.document_snapshot=to_jsonb(d),false) metadata_matches,v.evidence
            from "Documents" d left join lateral (select * from person_document_verifications where document_id=d."Id"
              and service_user_id=@person and organization_id=@org and branch_id=@branch order by revision desc limit 1) v on true
            where d."ServiceUserId"=@person and d."OrganizationId"=@org and d."BranchId"=@branch and lower(trim(d."Category"))<>'withdrawn' and not exists(select 1 from document_lifecycle_events e where e."DocumentId"=d."Id") order by d."FileName",d."Id"
            """, serviceUserId, branch, _ => { }, token);
        var verificationHistory=await RemediationRows("""
            select v.id,v.document_id,v.revision,v.status,v.valid_until,v.evidence,v.created_at,u."UserName" author,
              v.document_snapshot->>'FileName' file_name from person_document_verifications v join "AppUsers" u on u."Id"=v.created_by
            where v.service_user_id=@person and v.organization_id=@org and v.branch_id=@branch order by v.created_at desc,v.revision desc
            """,serviceUserId,branch,_=>{},token);
        return Ok(new { requirements, documents, verificationHistory, checks = await DocumentCompletenessChecks(serviceUserId, branch, token), asOf = DateTimeOffset.UtcNow });
    }

    [HttpPost("document-requirements"), Authorize(Roles = "CareManager,Administrator")]
    public Task<IActionResult> ConfigureDocumentRequirement(Guid serviceUserId, DocumentRequirementRequest r, CancellationToken token)
    {
        if (r.RequestId == Guid.Empty || r.ExpectedVersion < 0 || string.IsNullOrWhiteSpace(r.Title) || r.Title.Length>200
            || string.IsNullOrWhiteSpace(r.Category) || r.Category.Length>200 || r.Category.Trim().Equals("Withdrawn",StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Length>2000
            || (r.RootId is null && r.ExpectedVersion!=0)) return Task.FromResult<IActionResult>(BadRequest(new { message = "Provide a title, category, reason and current requirement version." }));
        return DocumentWrite(serviceUserId, "person_document_requirements", r.RequestId, JsonSerializer.Serialize(r), async branch => {
            var root = r.RootId ?? r.RequestId;
            var versions = await RemediationRows("select coalesce(max(version),0) version from person_document_requirements where root_id=@root and service_user_id=@person and organization_id=@org and branch_id=@branch",
                serviceUserId, branch, c => RemediationAdd(c, "root", root), token);
            var version = versions[0].GetProperty("version").GetInt32();
            if (version != r.ExpectedVersion || r.RootId is not null && version==0) return Conflict(new { message = "Requirement changed or is outside this person scope." });
            await using var c = await RemediationCommand("""
                insert into person_document_requirements(id,root_id,version,service_user_id,organization_id,branch_id,title,category,expiry_required,enabled,reason,created_by,request_snapshot)
                values(@id,@root,@version,@person,@org,@branch,@title,@category,@expiry,@enabled,@reason,@actor,cast(@snapshot as jsonb))
                """, serviceUserId, branch, token);
            RemediationAdd(c,"id",r.RequestId); RemediationAdd(c,"root",root); RemediationAdd(c,"version",version+1); RemediationAdd(c,"title",r.Title.Trim());
            RemediationAdd(c,"category",r.Category.Trim()); RemediationAdd(c,"expiry",r.ExpiryRequired); RemediationAdd(c,"enabled",r.Enabled);
            RemediationAdd(c,"reason",r.Reason.Trim()); RemediationAdd(c,"snapshot",JsonSerializer.Serialize(r)); await c.ExecuteNonQueryAsync(token);
            return Created($"/api/phase1/service-users/{serviceUserId}/onboarding/document-requirements/{r.RequestId}",new{id=r.RequestId,version=version+1});
        }, token);
    }

    [HttpPost("document-requirements/documents/{documentId:guid}/verification")]
    public Task<IActionResult> VerifyDocument(Guid serviceUserId, Guid documentId, DocumentVerificationRequest r, CancellationToken token)
    {
        if (r.RequestId==Guid.Empty || r.ExpectedRevision<0 || r.Status is not ("Verified" or "Rejected") || string.IsNullOrWhiteSpace(r.Evidence)
            || r.Evidence.Length>2000 || (r.Status=="Rejected" && r.ValidUntil.HasValue))
            return Task.FromResult<IActionResult>(BadRequest(new { message="Provide the current revision, verification decision and evidence. Rejected documents cannot have a validity expiry." }));
        var snapshot=JsonSerializer.Serialize(new{documentId,request=r});
        return DocumentWrite(serviceUserId,"person_document_verifications",r.RequestId,snapshot,async branch=> {
            if(r.ValidUntil is {} expiry && expiry<=DateTimeOffset.UtcNow) return BadRequest(new{message="Verification expiry must be in the future."});
            var docs=await RemediationRows("select d.\"Id\" id from \"Documents\" d where d.\"Id\"=@doc and d.\"ServiceUserId\"=@person and d.\"OrganizationId\"=@org and d.\"BranchId\"=@branch and lower(trim(d.\"Category\"))<>'withdrawn' and not exists(select 1 from document_lifecycle_events e where e.\"DocumentId\"=d.\"Id\")",serviceUserId,branch,c=>RemediationAdd(c,"doc",documentId),token);
            if(docs.Count!=1)return NotFound();
            var versions=await RemediationRows("select coalesce(max(revision),0) revision from person_document_verifications where document_id=@doc",serviceUserId,branch,c=>RemediationAdd(c,"doc",documentId),token);
            if(versions[0].GetProperty("revision").GetInt32()!=r.ExpectedRevision)return Conflict(new{message="Document verification changed. Refresh before saving."});
            await using var c=await RemediationCommand("""
                insert into person_document_verifications(id,document_id,revision,service_user_id,organization_id,branch_id,status,valid_until,evidence,document_snapshot,created_by,request_snapshot)
                values(@id,@doc,@revision,@person,@org,@branch,@status,@until,@evidence,'{}'::jsonb,@actor,cast(@snapshot as jsonb))
                """,serviceUserId,branch,token);
            RemediationAdd(c,"id",r.RequestId);RemediationAdd(c,"doc",documentId);RemediationAdd(c,"revision",r.ExpectedRevision+1);RemediationAdd(c,"status",r.Status);
            RemediationAdd(c,"until",r.ValidUntil?.ToUniversalTime(),DbType.DateTimeOffset);RemediationAdd(c,"evidence",r.Evidence.Trim());RemediationAdd(c,"snapshot",snapshot);
            await c.ExecuteNonQueryAsync(token);return Created($"/api/phase1/service-users/{serviceUserId}/onboarding/document-requirements/documents/{documentId}/verification/{r.RequestId}",new{id=r.RequestId,revision=r.ExpectedRevision+1});
        },token);
    }
    private async Task<IActionResult> DocumentWrite(Guid person,string table,Guid id,string snapshot,Func<Guid,Task<IActionResult>> action,CancellationToken token)
    {
        if(_currentUser.UserId is null)return Unauthorized();
        try{return await _context.Database.CreateExecutionStrategy().ExecuteAsync<IActionResult>(async()=>{
            await using var tx=await _context.Database.BeginTransactionAsync(token);
            var p=await FindAccessiblePerson(person,token);if(p?.BranchId is not Guid branch)return NotFound();
            await using(var gate=await RemediationCommand("select pg_advisory_xact_lock(hashtextextended(cast(@person as text),29))",person,branch,token))await gate.ExecuteNonQueryAsync(token);
            var replay=await RemediationRows($"select request_snapshot=cast(@snapshot as jsonb) and created_by=@actor same from {table} where id=@id and service_user_id=@person and organization_id=@org and branch_id=@branch",person,branch,c=>{RemediationAdd(c,"id",id);RemediationAdd(c,"snapshot",snapshot);},token);
            if(replay.Count>0)return replay[0].GetProperty("same").GetBoolean()?Ok(new{id,replayed=true}):Conflict(new{message="Request ID has different content or author."});
            var result=await action(branch);if(result is CreatedResult){_context.AuditEvents.Add(new AuditEvent(Guid.NewGuid(),table=="person_document_requirements"?"person.document_requirement_configured":"person.document_verified",_currentUser.UserName,"DocumentCompleteness",id,DateTimeOffset.UtcNow,_tenant.OrganizationId,branch));await _context.SaveChangesAsync(token);await tx.CommitAsync(token);}return result;
        });}catch(Npgsql.PostgresException e)when(e.SqlState is "23505" or "23514" or "23503"){return Conflict(new{message="Document, requirement or review state changed. Refresh before saving."});}
    }
}
public sealed record DocumentRequirementRequest(Guid RequestId,int ExpectedVersion,string Title,string Category,bool ExpiryRequired,bool Enabled,string Reason,Guid? RootId=null);
public sealed record DocumentVerificationRequest(Guid RequestId,int ExpectedRevision,string Status,string Evidence,DateTimeOffset? ValidUntil=null);
