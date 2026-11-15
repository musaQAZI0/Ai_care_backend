using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

public sealed partial class PayrollReadyController
{
    private const string ManifestSelect="""
        select m.id,m.batch_id,m.template_id,m.revision,m.supersedes_id,m.reason,m.file_name,m.sha256,m.source_sha256,
          m.row_count,m.currency,m.total,m.worker_totals,m.line_snapshot,m.created_at,m.branch_id,
          case when exists(select 1 from finance_payroll_csv_exports n where n.supersedes_id=m.id) then 'Superseded' else coalesce(r.outcome,'Created') end status,
          r.reference import_reference,r.imported_rows,r.imported_total
        from finance_payroll_csv_exports m left join finance_payroll_csv_receipts r on r.export_id=m.id
        where m.organization_id=@org and (@wide or m.branch_id=@branch)
        """;

    [HttpGet("csv/configuration")]
    public IActionResult CsvConfiguration([FromServices] IConfiguration configuration)=>Ok(new{encryptionConfigured=PayrollCsvDocument.Configured(configuration),format="Ordered row-based CSV",sageCompatibility="Customer template and payroll test-company reconciliation required"});

    [HttpGet("csv/templates")]
    public async Task<IActionResult> CsvTemplates(CancellationToken t)=>Ok(await Rows("select id,branch_id,reference,evidence_reference,definition,created_by,approved_by,approved_at from finance_payroll_csv_templates where organization_id=@org and (@wide or branch_id=@branch) order by created_at desc",_=>{},t));

    [HttpPost("csv/templates")]
    public Task<IActionResult> CreateCsvTemplate(PayrollCsvTemplateRequest r,CancellationToken t)=>Tx(async()=>
    {
        if(r.RequestId==Guid.Empty||!Scope(r.BranchId)||string.IsNullOrWhiteSpace(r.Reference)||r.Reference.Length>120||(r.EvidenceReference?.Length??0)>200)return BadRequest(new{message="Provide scoped template ID, branch, reference and bounded evidence reference."});
        var error=PayrollCsvDocument.Validate(r.Definition);if(error is not null)return UnprocessableEntity(new{message=error});
        if(!await db.Branches.AnyAsync(x=>x.Id==r.BranchId&&x.OrganizationId==tenant.OrganizationId,t))return NotFound();
        var snapshot=JsonSerializer.Serialize(r,PayrollCsvDocument.JsonOptions);var replay=await Replay("finance_payroll_csv_templates",r.RequestId,snapshot,t);if(replay is not null)return replay;
        await Exec("insert into finance_payroll_csv_templates(id,organization_id,branch_id,reference,evidence_reference,definition,request_snapshot,created_by) values(@id,@org,@target,@reference,@evidence,cast(@definition as jsonb),cast(@snapshot as jsonb),@actor)",c=>{
            Add(c,"id",r.RequestId);Add(c,"target",r.BranchId);Add(c,"reference",r.Reference.Trim());Add(c,"evidence",r.EvidenceReference?.Trim()??"");Add(c,"definition",JsonSerializer.Serialize(r.Definition,PayrollCsvDocument.JsonOptions));Add(c,"snapshot",snapshot);
        },t);await Audit("PAYROLL_CSV_TEMPLATE_CREATED",r.RequestId,r.BranchId,t);return Created("",new{id=r.RequestId});
    },t);

    [HttpPost("csv/templates/{id:guid}/approve")]
    public Task<IActionResult> ApproveCsvTemplate(Guid id,CancellationToken t)=>Tx(async()=>
    {
        var templates=await Rows("select * from finance_payroll_csv_templates where id=@id and organization_id=@org and (@wide or branch_id=@branch) for update",c=>Add(c,"id",id),t);
        if(templates.Count!=1)return NotFound();var template=templates[0];
        if(template.GetProperty("approved_by").ValueKind!=JsonValueKind.Null)return Ok(new{id,replayed=true});
        if(template.GetProperty("created_by").GetGuid()==user.UserId||string.IsNullOrWhiteSpace(template.GetProperty("evidence_reference").GetString()))return Conflict(new{message="An independent approver and template/specification evidence reference are required."});
        await Exec("update finance_payroll_csv_templates set approved_by=@actor,approved_at=now() where id=@id and organization_id=@org",c=>Add(c,"id",id),t);
        await Audit("PAYROLL_CSV_TEMPLATE_APPROVED",id,template.GetProperty("branch_id").GetGuid(),t);return Ok(new{id});
    },t);

    private async Task<bool> CsvGrant(Guid branch,CancellationToken t)=>
        (await Rows("select id from finance_export_permissions where organization_id=@org and branch_id=@target and user_id=@actor and export_kind='Payroll' and approved_by is not null and allow_identifiers and allow_rates and (now() at time zone 'Europe/London')::date between valid_from and valid_to",c=>Add(c,"target",branch),t)).Count>0;

    [HttpGet("batches/{id:guid}/csv-exports")]
    public async Task<IActionResult> CsvExports(Guid id,CancellationToken t)
    {
        var batches=await Rows("select branch_id from finance_payroll_batches where id=@id and organization_id=@org and (@wide or branch_id=@branch)",c=>Add(c,"id",id),t);
        if(batches.Count!=1)return NotFound();if(!await CsvGrant(batches[0].GetProperty("branch_id").GetGuid(),t))return Forbid();
        return Ok(await Rows(ManifestSelect+" and m.batch_id=@id order by m.revision desc",c=>Add(c,"id",id),t));
    }

    [HttpPost("batches/{id:guid}/csv-exports")]
    public Task<IActionResult> CreateCsvExport(Guid id,PayrollCsvExportRequest r,[FromServices] IConfiguration configuration,CancellationToken t)=>Tx(async()=>
    {
        if(r.RequestId==Guid.Empty||r.TemplateId==Guid.Empty||(r.Reason?.Length??0)>500)return BadRequest();
        var batches=await Batch(id,t);if(batches.Count!=1)return NotFound();var batch=batches[0];var branch=batch.GetProperty("branch_id").GetGuid();
        if(!await CsvGrant(branch,t))return Forbid();
        if(batch.GetProperty("status").GetString() is not("Approved" or "Paid"))return Conflict(new{message="CSV export requires an independently approved batch."});
        if(!PayrollCsvDocument.Configured(configuration))return Conflict(new{message="Configure the payroll export encryption key in environment secrets before generating files."});
        var snapshot=JsonSerializer.Serialize(new{r.TemplateId,r.SupersedesId,Reason=r.Reason?.Trim()??"",r.NotImportedConfirmed},PayrollCsvDocument.JsonOptions);
        var existing=await Rows("select id,batch_id,request_snapshot=cast(@snapshot as jsonb) matches from finance_payroll_csv_exports where id=@id and organization_id=@org and (@wide or branch_id=@branch)",c=>{Add(c,"id",r.RequestId);Add(c,"snapshot",snapshot);},t);
        if(existing.Count>0)return existing[0].GetProperty("batch_id").GetGuid()==id&&existing[0].GetProperty("matches").GetBoolean()?Ok(new{id=r.RequestId,replayed=true}):Conflict(new{message="Export request ID has different contents."});
        var latest=await Rows("select m.*,r.outcome,m.request_snapshot=cast(@snapshot as jsonb) snapshot_matches from finance_payroll_csv_exports m left join finance_payroll_csv_receipts r on r.export_id=m.id where m.batch_id=@id and m.organization_id=@org order by m.revision desc limit 1",c=>{Add(c,"id",id);Add(c,"snapshot",snapshot);},t);
        var revision=1;
        if(latest.Count>0)
        {
            var last=latest[0];
            if(last.GetProperty("snapshot_matches").GetBoolean())return Ok(new{id=last.GetProperty("id").GetGuid(),replayed=true});
            // A fresh first-export request recovers the same latest artifact for its template.
            if(r.SupersedesId is null&&last.GetProperty("template_id").GetGuid()==r.TemplateId)return Ok(new{id=last.GetProperty("id").GetGuid(),replayed=true});
            if(r.SupersedesId!=last.GetProperty("id").GetGuid()||!r.NotImportedConfirmed||string.IsNullOrWhiteSpace(r.Reason))return Conflict(new{message="A new file must explicitly supersede the latest export, with a reason and confirmation that it has not been imported."});
            if(last.GetProperty("outcome").ValueKind!=JsonValueKind.Null&&last.GetProperty("outcome").GetString()!="Rejected")return Conflict(new{message="Imported or partially imported files cannot be superseded. Reconcile the payroll system and use approved corrections; never import the whole file again."});
            revision=last.GetProperty("revision").GetInt32()+1;
        }
        else if(r.SupersedesId is not null)return Conflict(new{message="There is no previous export to supersede."});
        var templates=await Rows("select definition from finance_payroll_csv_templates where id=@id and organization_id=@org and branch_id=@target and approved_by is not null",c=>{Add(c,"id",r.TemplateId);Add(c,"target",branch);},t);
        if(templates.Count!=1)return Conflict(new{message="Select an independently approved template for this branch."});
        var contracts=await Rows("select * from finance_payroll_contracts where id=@id and organization_id=@org and branch_id=@target and approved_by is not null",c=>{Add(c,"id",batch.GetProperty("contract_id").GetGuid());Add(c,"target",branch);},t);
        if(contracts.Count!=1)return Conflict(new{message="Approved source contract is unavailable."});var contract=contracts[0];var currency=contract.GetProperty("currency").GetString()!;
        var records=await Rows("select * from finance_payroll_batch_lines where batch_id=@id and organization_id=@org and branch_id=@target and not released",c=>{Add(c,"id",id);Add(c,"target",branch);},t);
        var lines=new List<PayrollCsvLine>();
        foreach(var x in records)
        {
            var worker=x.GetProperty("care_worker_id").GetGuid();var kind=x.GetProperty("source_type").GetString()!;
            if(!contract.GetProperty("worker_mappings").TryGetProperty(worker.ToString(),out var workerRef)||workerRef.GetString()!=x.GetProperty("worker_reference").GetString()||
               !contract.GetProperty("pay_codes").TryGetProperty(kind,out var payCode)||payCode.GetString()!=x.GetProperty("pay_code").GetString())return Conflict(new{message="Worker/pay-code mapping does not match the approved source contract."});
            lines.Add(new(x.GetProperty("id").GetGuid(),workerRef.GetString()!,payCode.GetString()!,kind,DateOnly.Parse(x.GetProperty("service_date").GetString()!,CultureInfo.InvariantCulture),x.GetProperty("currency").GetString()!,x.GetProperty("quantity").GetDecimal(),x.GetProperty("unit_rate").GetDecimal(),x.GetProperty("amount").GetDecimal()));
        }
        PayrollCsvArtifact artifact;(string KeyId,byte[] Payload) protectedFile;
        try
        {
            artifact=PayrollCsvDocument.Render(JsonSerializer.Deserialize<PayrollCsvDefinition>(templates[0].GetProperty("definition"),PayrollCsvDocument.JsonOptions)!,lines,currency,DateOnly.Parse(batch.GetProperty("period_from").GetString()!),DateOnly.Parse(batch.GetProperty("period_to").GetString()!));
            if(artifact.Content.Length>10*1024*1024)return UnprocessableEntity(new{message="CSV exceeds the 10 MB export limit."});
            protectedFile=PayrollCsvDocument.Protect(artifact.Content,configuration,$"{tenant.OrganizationId:N}:{r.RequestId:N}");
        }
        catch(ArgumentException e){return UnprocessableEntity(new{message=e.Message});}
        var fileName=$"payroll-{id:N}-r{revision}.csv";
        await Exec("""
            insert into finance_payroll_csv_exports(id,organization_id,branch_id,batch_id,template_id,revision,supersedes_id,reason,request_snapshot,file_name,sha256,source_sha256,key_id,protected_content,row_count,currency,total,worker_totals,line_snapshot,created_by)
            values(@id,@org,@target,@batch,@template,@revision,@supersedes,@reason,cast(@snapshot as jsonb),@file,@sha,@source,@key,@payload,@count,@currency,@total,cast(@workers as jsonb),cast(@lines as jsonb),@actor)
            """,c=>{
            Add(c,"id",r.RequestId);Add(c,"target",branch);Add(c,"batch",id);Add(c,"template",r.TemplateId);Add(c,"revision",revision);NullableGuid(c,"supersedes",r.SupersedesId);Add(c,"reason",r.Reason?.Trim()??"");Add(c,"snapshot",snapshot);
            Add(c,"file",fileName);Add(c,"sha",artifact.Sha256);Add(c,"source",artifact.SourceSha256);Add(c,"key",protectedFile.KeyId);Add(c,"payload",protectedFile.Payload);Add(c,"count",artifact.RowCount);Add(c,"currency",currency);Add(c,"total",artifact.Total);Add(c,"workers",JsonSerializer.Serialize(artifact.WorkerTotals,PayrollCsvDocument.JsonOptions));Add(c,"lines",JsonSerializer.Serialize(lines,PayrollCsvDocument.JsonOptions));
        },t);await Audit("PAYROLL_CSV_CREATED",r.RequestId,branch,t);return Created("",new{id=r.RequestId,revision,sha256=artifact.Sha256,rowCount=artifact.RowCount,total=artifact.Total});
    },t);

    [HttpGet("csv/exports/{id:guid}/download")]
    public Task<IActionResult> DownloadCsv(Guid id,[FromServices] IConfiguration configuration,CancellationToken t)=>Tx(async()=>
    {
        var exports=await Rows(ManifestSelect+" and m.id=@id",c=>Add(c,"id",id),t);if(exports.Count!=1)return NotFound();var export=exports[0];var branch=export.GetProperty("branch_id").GetGuid();
        if(!await CsvGrant(branch,t))return Forbid();if(export.GetProperty("status").GetString()=="Superseded")return Conflict(new{message="This CSV has been superseded. Download the latest revision."});
        byte[] payload;string key;
        await using(var c=await Command("select protected_content,key_id from finance_payroll_csv_exports where id=@id and organization_id=@org",t))
        {Add(c,"id",id);await using var reader=await c.ExecuteReaderAsync(t);if(!await reader.ReadAsync(t))return NotFound();payload=reader.GetFieldValue<byte[]>(0);key=reader.GetString(1);}
        byte[] content;try{content=PayrollCsvDocument.Unprotect(payload,key,configuration,$"{tenant.OrganizationId:N}:{id:N}");}
        catch(Exception e)when(e is ArgumentException or CryptographicException){return Conflict(new{message="Export encryption key is unavailable or the stored artifact failed verification. Restore the correct key; do not generate a replacement for an uncertain import."});}
        if(PayrollCsvDocument.Hash(content)!=export.GetProperty("sha256").GetString())return Conflict(new{message="Stored CSV checksum failed verification."});
        await Audit("PAYROLL_CSV_DOWNLOADED",id,branch,t);Response.Headers["X-Payroll-SHA256"]=export.GetProperty("sha256").GetString();Response.Headers.CacheControl="no-store";
        return File(content,"text/csv; charset=utf-8",export.GetProperty("file_name").GetString());
    },t);

    [HttpPost("csv/exports/{id:guid}/acknowledge-import")]
    public Task<IActionResult> AcknowledgeCsvImport(Guid id,PayrollCsvImportRequest r,CancellationToken t)=>Tx(async()=>
    {
        if(r.RequestId==Guid.Empty||string.IsNullOrWhiteSpace(r.Reference)||r.Reference.Length>200||r.Outcome is not("Imported" or "Rejected" or "PartiallyImported")||r.ImportedLineIds is null||r.WorkerTotals is null||r.ImportedLineIds.Length>100000)return BadRequest(new{message="Provide outcome, actual import reference, imported line IDs and reconciled worker totals."});
        var exports=await Rows(ManifestSelect+" and m.id=@id",c=>Add(c,"id",id),t);if(exports.Count!=1)return NotFound();var export=exports[0];var branch=export.GetProperty("branch_id").GetGuid();if(!await CsvGrant(branch,t))return Forbid();
        var snapshot=JsonSerializer.Serialize(new{r.Outcome,Reference=r.Reference.Trim(),ImportedLineIds=r.ImportedLineIds.Order().ToArray(),r.ImportedRows,r.ImportedTotal,WorkerTotals=r.WorkerTotals.OrderBy(x=>x.Key,StringComparer.Ordinal).ToDictionary(x=>x.Key,x=>x.Value)},PayrollCsvDocument.JsonOptions);
        var receipts=await Rows("select request_snapshot=cast(@snapshot as jsonb) matches from finance_payroll_csv_receipts where export_id=@id and organization_id=@org",c=>{Add(c,"id",id);Add(c,"snapshot",snapshot);},t);
        if(receipts.Count>0)return receipts[0].GetProperty("matches").GetBoolean()?Ok(new{id,replayed=true}):Conflict(new{message="Import acknowledgement is immutable and has different evidence."});
        if(export.GetProperty("status").GetString()=="Superseded")return Conflict(new{message="A superseded CSV cannot be acknowledged as imported."});
        var lines=JsonSerializer.Deserialize<PayrollCsvLine[]>(export.GetProperty("line_snapshot"),PayrollCsvDocument.JsonOptions)!;
        var ids=r.ImportedLineIds.ToHashSet();var selected=lines.Where(x=>ids.Contains(x.Id)).ToArray();
        if(ids.Count!=r.ImportedLineIds.Length||selected.Length!=ids.Count||r.ImportedRows!=selected.Length||r.ImportedTotal!=selected.Sum(x=>x.Amount)||
           r.Outcome=="Imported"&&selected.Length!=lines.Length||r.Outcome=="Rejected"&&selected.Length!=0||r.Outcome=="PartiallyImported"&&(selected.Length==0||selected.Length>=lines.Length))return UnprocessableEntity(new{message="Actual imported rows, line IDs and total must reconcile to the frozen CSV and selected outcome."});
        var workerTotals=selected.GroupBy(x=>x.WorkerReference).ToDictionary(x=>x.Key,x=>x.Sum(l=>l.Amount));
        if(workerTotals.Count!=r.WorkerTotals.Count||workerTotals.Any(x=>!r.WorkerTotals.TryGetValue(x.Key,out var amount)||amount!=x.Value))return UnprocessableEntity(new{message="Actual imported worker totals do not reconcile to the selected CSV lines."});
        await Exec("insert into finance_payroll_csv_receipts(id,export_id,organization_id,branch_id,outcome,reference,imported_rows,imported_total,worker_totals,request_snapshot,acknowledged_by) values(@receipt,@id,@org,@target,@outcome,@reference,@count,@total,cast(@workers as jsonb),cast(@snapshot as jsonb),@actor)",c=>{
            Add(c,"receipt",r.RequestId);Add(c,"id",id);Add(c,"target",branch);Add(c,"outcome",r.Outcome);Add(c,"reference",r.Reference.Trim());Add(c,"count",r.ImportedRows);Add(c,"total",r.ImportedTotal);Add(c,"workers",JsonSerializer.Serialize(r.WorkerTotals,PayrollCsvDocument.JsonOptions));Add(c,"snapshot",snapshot);
        },t);await Audit("PAYROLL_CSV_IMPORT_"+r.Outcome.ToUpperInvariant(),id,branch,t);return Ok(new{id,outcome=r.Outcome,paymentConfirmed=false});
    },t);
}
public sealed record PayrollCsvTemplateRequest(Guid RequestId,Guid BranchId,string Reference,string? EvidenceReference,PayrollCsvDefinition Definition);
public sealed record PayrollCsvExportRequest(Guid RequestId,Guid TemplateId,Guid? SupersedesId=null,string? Reason=null,bool NotImportedConfirmed=false);
public sealed record PayrollCsvImportRequest(Guid RequestId,string Outcome,string Reference,Guid[] ImportedLineIds,int ImportedRows,decimal ImportedTotal,Dictionary<string,decimal> WorkerTotals);
