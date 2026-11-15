using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiCare.Api;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed class PayrollCsvWorkflowRegressionTests(PostgresRegressionFactory factory):IClassFixture<PostgresRegressionFactory>
{
    const string Root="/api/phase1/finance/payroll-ready";
    [Theory]
    [InlineData("Imported")]
    [InlineData("Rejected")]
    [InlineData("PartiallyImported")]
    public async Task FrozenFilesEnforceScopeApprovalRetrySupersessionAndReconciledImportWithoutPayment(string outcome)
    {
        await factory.EnsureClinicalSeedAsync();
        var configuration=factory.Services.GetRequiredService<IConfiguration>();
        var oldId=configuration["PayrollExports:ActiveKeyId"];var oldKey=configuration["PayrollExports:EncryptionKeys:regression"];
        configuration["PayrollExports:ActiveKeyId"]="regression";
        configuration["PayrollExports:EncryptionKeys:regression"]=Convert.ToBase64String(Enumerable.Repeat((byte)19,32).ToArray());
        try { await Exercise(outcome); }
        finally { configuration["PayrollExports:ActiveKeyId"]=oldId;configuration["PayrollExports:EncryptionKeys:regression"]=oldKey; }
    }
    async Task Exercise(string outcome)
    {
        var org=Guid.NewGuid();var branch=Guid.NewGuid();var other=Guid.NewGuid();var worker=Guid.NewGuid();var authorId=Guid.NewGuid();var reviewerId=Guid.NewGuid();var prefix=$"csv.{Guid.NewGuid():N}";
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Organizations.Add(new Organization(org,"CSV regression","Trial","Active"));
            db.Branches.AddRange(new Branch(branch,org,"Main","Test","Active"),new Branch(other,org,"Other","Test","Active"));
            db.CareWorkers.Add(new CareWorker(worker,"Synthetic CSV worker","Care","Flexible",0,0,"Valid","Compliant","10",org,branch));
            db.AppUsers.AddRange(new AppUser(authorId,prefix+"author",prefix+"author@test.invalid",PasswordHasher.HashPassword("Admin123!"),UserRole.BackOffice,true,org,branch),new AppUser(reviewerId,prefix+"reviewer",prefix+"reviewer@test.invalid",PasswordHasher.HashPassword("Admin123!"),UserRole.BackOffice,true,org,branch),new AppUser(Guid.NewGuid(),prefix+"other",prefix+"other@test.invalid",PasswordHasher.HashPassword("Admin123!"),UserRole.BackOffice,true,org,other));
            await db.SaveChangesAsync();
        }
        using var author=await Login(prefix+"author");using var reviewer=await Login(prefix+"reviewer");using var outsider=await Login(prefix+"other");
        var contractId=Guid.NewGuid();
        await Success(await author.PostAsJsonAsync(Root+"/contracts",new PayrollExportContractRequest(contractId,branch,"INTERNAL-CSV-TEST","GBP",new(2052,1,1),new(2052,1,31),new(){{worker.ToString(),"WORKER-001"}},new(){["Worked"]="WORK",["Travel"]="TRAVEL",["TimesheetMileage"]="TM",["ClaimMileage"]="CM",["ClaimExpense"]="EXP",["Leave"]="LEAVE",["Adjustment"]="ADJ"})));
        await Success(await reviewer.PostAsync($"{Root}/contracts/{contractId}/approve",null));
        var absence=Guid.NewGuid();
        using(var scope=factory.Services.CreateScope())await scope.ServiceProvider.GetRequiredService<CareDbContext>().Database.ExecuteSqlRawAsync("insert into worker_absences(id,care_worker_id,organization_id,branch_id,absence_type,starts_at,ends_at,status) values({0},{1},{2},{3},'Annual leave','2052-01-11T00:00:00Z','2052-01-14T00:00:00Z','Approved')",absence,worker,org,branch);
        var extras=new List<Guid>();
        for(var i=0;i<2;i++)
        {
            var extra=new PayrollExtraRequest(Guid.NewGuid(),branch,worker,"Leave",new(2052,1,11+i),"GBP",i+1,10,(i+1)*10,absence,null,"INTERNAL-LEAVE-"+i,"Synthetic approved hours","ToEven");
            await Success(await author.PostAsJsonAsync(Root+"/extras",extra));await Success(await reviewer.PostAsync($"{Root}/extras/{extra.RequestId}/approve",null));extras.Add(extra.RequestId);
        }
        var batch=Guid.NewGuid();await Success(await author.PostAsJsonAsync(Root+"/batches",new PayrollReadyBatchRequest(batch,contractId,new(2052,1,1),new(2052,1,31),"INTERNAL-CSV-BATCH",[],[],extras.ToArray(),null)));
        var template=new PayrollCsvTemplateRequest(Guid.NewGuid(),branch,"GENERIC-INTERNAL-CSV","INTERNAL-SPEC-001; NOT SAGE VERIFIED",PayrollCsvDocumentTests.Definition);
        await Success(await author.PostAsJsonAsync(Root+"/csv/templates",template));
        Assert.Equal(HttpStatusCode.Conflict,(await author.PostAsync($"{Root}/csv/templates/{template.RequestId}/approve",null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await outsider.PostAsync($"{Root}/csv/templates/{template.RequestId}/approve",null)).StatusCode);
        var request=new PayrollCsvExportRequest(Guid.NewGuid(),template.RequestId);
        Assert.Equal(HttpStatusCode.Forbidden,(await author.PostAsJsonAsync($"{Root}/batches/{batch}/csv-exports",request)).StatusCode);
        // Set up explicit, independently approved synthetic export authority; never change production grants.
        using(var scope=factory.Services.CreateScope())await scope.ServiceProvider.GetRequiredService<CareDbContext>().Database.ExecuteSqlRawAsync("insert into finance_export_permissions(id,organization_id,branch_id,user_id,export_kind,allow_identifiers,allow_rates,valid_from,valid_to,reference,created_by,approved_by,approved_at) values({0},{1},{2},{3},'Payroll',true,true,'2026-01-01','2099-12-31','SYNTHETIC-CSV-GRANT',{4},{5},now())",Guid.NewGuid(),org,branch,authorId,Guid.NewGuid(),reviewerId);
        Assert.Equal(HttpStatusCode.Conflict,(await author.PostAsJsonAsync($"{Root}/batches/{batch}/csv-exports",request)).StatusCode);
        await Success(await reviewer.PostAsJsonAsync($"{Root}/batches/{batch}/review",new{decision="Approve",reason="Independent source reconciliation"}));
        Assert.Equal(HttpStatusCode.Conflict,(await author.PostAsJsonAsync($"{Root}/batches/{batch}/csv-exports",request)).StatusCode);
        await Success(await reviewer.PostAsync($"{Root}/csv/templates/{template.RequestId}/approve",null));
        var attempts=await Task.WhenAll(author.PostAsJsonAsync($"{Root}/batches/{batch}/csv-exports",request),author.PostAsJsonAsync($"{Root}/batches/{batch}/csv-exports",request with{RequestId=Guid.NewGuid()}));
        foreach(var attempt in attempts)await Success(attempt);Assert.Single(attempts,x=>x.StatusCode==HttpStatusCode.Created);
        var history=(await author.GetFromJsonAsync<JsonElement[]>($"{Root}/batches/{batch}/csv-exports"))!;Assert.Single(history);var first=history[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound,(await outsider.GetAsync($"{Root}/csv/exports/{first}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await reviewer.GetAsync($"{Root}/csv/exports/{first}/download")).StatusCode);
        var bytes=await author.GetByteArrayAsync($"{Root}/csv/exports/{first}/download");Assert.Equal(bytes,await author.GetByteArrayAsync($"{Root}/csv/exports/{first}/download"));Assert.Equal(history[0].GetProperty("sha256").GetString(),PayrollCsvDocument.Hash(bytes));
        var replacement=new PayrollCsvExportRequest(Guid.NewGuid(),template.RequestId,first,"Internal formatting reissue",true);
        Assert.Equal(HttpStatusCode.Conflict,(await author.PostAsJsonAsync($"{Root}/batches/{batch}/csv-exports",replacement with{NotImportedConfirmed=false})).StatusCode);
        await Success(await author.PostAsJsonAsync($"{Root}/batches/{batch}/csv-exports",replacement));
        await Success(await author.PostAsJsonAsync($"{Root}/batches/{batch}/csv-exports",replacement with{RequestId=Guid.NewGuid()}));
        history=(await author.GetFromJsonAsync<JsonElement[]>($"{Root}/batches/{batch}/csv-exports"))!;Assert.Equal(2,history.Length);var current=history[0];var exportId=current.GetProperty("id").GetGuid();Assert.Equal(replacement.RequestId,exportId);
        Assert.Equal(HttpStatusCode.Conflict,(await author.GetAsync($"{Root}/csv/exports/{first}/download")).StatusCode);
        var lines=JsonSerializer.Deserialize<PayrollCsvLine[]>(current.GetProperty("line_snapshot"),PayrollCsvDocument.JsonOptions)!;
        var evidence=new PayrollCsvImportRequest(Guid.NewGuid(),"Imported","TEST-COMPANY-IMPORT-1",lines.Select(x=>x.Id).ToArray(),2,30,new(){{"WORKER-001",30}});
        Assert.Equal(HttpStatusCode.UnprocessableEntity,(await author.PostAsJsonAsync($"{Root}/csv/exports/{exportId}/acknowledge-import",evidence with{ImportedTotal=31})).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,(await author.PostAsJsonAsync($"{Root}/csv/exports/{exportId}/acknowledge-import",evidence with{WorkerTotals=new(){{"WORKER-001",29}}})).StatusCode);
        var actual=outcome switch {
            "Rejected"=>evidence with{Outcome=outcome,ImportedLineIds=[],ImportedRows=0,ImportedTotal=0,WorkerTotals=new()},
            "PartiallyImported"=>evidence with{Outcome=outcome,ImportedLineIds=[lines[0].Id],ImportedRows=1,ImportedTotal=lines[0].Amount,WorkerTotals=new(){{"WORKER-001",lines[0].Amount}}},
            _=>evidence
        };
        await Success(await author.PostAsJsonAsync($"{Root}/csv/exports/{exportId}/acknowledge-import",actual));
        await Success(await author.PostAsJsonAsync($"{Root}/csv/exports/{exportId}/acknowledge-import",actual with{RequestId=Guid.NewGuid()}));
        Assert.Equal(HttpStatusCode.Conflict,(await author.PostAsJsonAsync($"{Root}/csv/exports/{exportId}/acknowledge-import",actual with{Reference="DIFFERENT-EVIDENCE"})).StatusCode);
        var reissue=await author.PostAsJsonAsync($"{Root}/batches/{batch}/csv-exports",replacement with{RequestId=Guid.NewGuid(),SupersedesId=exportId});
        if(outcome=="Rejected")await Success(reissue);
        else Assert.Equal(HttpStatusCode.Conflict,reissue.StatusCode);
        using var verify=factory.Services.CreateScope();var verifyDb=verify.ServiceProvider.GetRequiredService<CareDbContext>();
        Assert.Equal("Approved",await verifyDb.Database.SqlQueryRaw<string>("select status as \"Value\" from finance_payroll_batches where id={0}",batch).SingleAsync());
        Assert.Equal(1,await verifyDb.Database.SqlQueryRaw<int>("select count(*)::int as \"Value\" from finance_payroll_csv_receipts where export_id={0}",exportId).SingleAsync());
        var action="PAYROLL_CSV_IMPORT_"+outcome.ToUpperInvariant();Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.OrganizationId==org&&x.Action==action));
        await Assert.ThrowsAnyAsync<Exception>(()=>verifyDb.Database.ExecuteSqlRawAsync("update finance_payroll_csv_exports set total=31 where id={0}",exportId));
        await Assert.ThrowsAnyAsync<Exception>(()=>verifyDb.Database.ExecuteSqlRawAsync("delete from finance_payroll_csv_receipts where export_id={0}",exportId));
        // Applying an already applied migration to populated evidence is a no-op.
        await verifyDb.Database.MigrateAsync();
        Assert.Empty(await verifyDb.Database.GetPendingMigrationsAsync());
    }
    async Task Success(HttpResponseMessage response)=>Assert.True(response.IsSuccessStatusCode,$"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}; {factory.LastServerException}");
    async Task<HttpClient> Login(string name){var c=factory.CreateClient();var response=await c.PostAsJsonAsync("/api/auth/login",new{userName=name,password="Admin123!",mfaCode=(string?)null});await Success(response);var value=await response.Content.ReadFromJsonAsync<JsonElement>();c.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",value.GetProperty("token").GetString());return c;}
}
