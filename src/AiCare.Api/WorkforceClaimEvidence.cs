using System.Security.Cryptography;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

public sealed partial class WorkforceFinanceController
{
    [HttpPost("evidence")]
    [RequestSizeLimit(11 * 1024 * 1024)]
    public Task<IActionResult> UploadEvidence([FromForm] Guid careWorkerId, IFormFile file,
        [FromServices] IDocumentMalwareScanner scanner, CancellationToken t) => Tx(async () =>
    {
        if (user.UserId is null) return Unauthorized();
        if (!Review && (!user.IsCareWorker || careWorkerId != user.CareWorkerId)) return Forbid();
        var worker = await db.CareWorkers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == careWorkerId &&
            x.OrganizationId == tenant.OrganizationId && (tenant.IsOrganizationWide || x.BranchId == tenant.BranchId), t);
        if (worker?.BranchId is not Guid branch) return NotFound();
        if (file is null) return BadRequest(new { message = "Choose a receipt document." });
        var validation = await DocumentUploadValidator.ValidateAsync(file, 10 * 1024 * 1024, scanner, t);
        if (!validation.Accepted) return StatusCode(validation.StatusCode, new { message = validation.Message });
        using var memory = new MemoryStream();
        await file.CopyToAsync(memory, t);
        var bytes = memory.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var existing = await Rows("select id from finance_claim_evidence where organization_id=@org and care_worker_id=@worker and sha256=@hash",
            c => { Add(c, "worker", careWorkerId); Add(c, "hash", hash); }, t);
        if (existing.Count == 1) return Ok(new { id = existing[0]["id"], sha256 = hash, replayed = true });
        var id = Guid.NewGuid();
        await Exec("""
            insert into finance_claim_evidence(id,organization_id,branch_id,care_worker_id,file_name,content_type,content,sha256,uploaded_by)
            values(@id,@org,@target,@worker,@name,@type,@bytes,@hash,@actorId)
            """, c => { Add(c, "id", id); Add(c, "target", branch); Add(c, "worker", careWorkerId);
                Add(c, "name", file.FileName.Trim()); Add(c, "type", file.ContentType.Split(';')[0].Trim());
                Add(c, "bytes", bytes); Add(c, "hash", hash); }, t);
        Audit("CLAIM_EVIDENCE_UPLOADED", id, branch);
        await db.SaveChangesAsync(t);
        return Created($"/api/phase1/finance/workforce-claims/evidence/{id}", new { id, sha256 = hash });
    }, t);

    [HttpGet("evidence/{id:guid}")]
    public async Task<IActionResult> DownloadEvidence(Guid id, CancellationToken t)
    {
        if (!Review && !user.IsCareWorker) return Forbid();
        await using var c = await Cmd("""
            select file_name,content_type,content,branch_id from finance_claim_evidence
            where id=@id and organization_id=@org and (@wide or branch_id=@branch) and (@review or care_worker_id=@worker)
            """, t);
        Add(c, "id", id); Add(c, "review", Review); Add(c, "worker", user.CareWorkerId ?? Guid.Empty);
        await using var reader = await c.ExecuteReaderAsync(t);
        if (!await reader.ReadAsync(t)) return NotFound();
        var name = reader.GetString(0); var type = reader.GetString(1); var bytes = (byte[])reader[2]; var branch = reader.GetGuid(3);
        await reader.DisposeAsync();
        Audit("CLAIM_EVIDENCE_DOWNLOADED", id, branch); await db.SaveChangesAsync(t);
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(bytes, type, name);
    }

    [HttpGet("evidence-findings")]
    public async Task<IActionResult> EvidenceFindings(CancellationToken t)
    {
        if (!Review) return Forbid();
        return Ok(await Rows("select * from finance_claim_evidence_findings where organization_id=@org and (@wide or branch_id=@branch)", _ => { }, t));
    }
}
