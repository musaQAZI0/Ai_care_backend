using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed class InvoiceBranchSnapshotRegressionTests(PostgresRegressionFactory factory) : IClassFixture<PostgresRegressionFactory>
{
    [Fact]
    public async Task MissingBranchSettingsRollBackTheWholeRunAndAllowRecovery()
    {
        await factory.EnsureClinicalSeedAsync();
        var org=Guid.NewGuid(); var branch=Guid.NewGuid(); var funder=Guid.NewGuid(); var name=$"missing.profile.{Guid.NewGuid():N}";
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Organizations.Add(new Organization(org,"Missing profile","Trial","Active"));
            db.Branches.Add(new Branch(branch,org,"Missing settings","Test","Active"));
            db.AppUsers.Add(new AppUser(Guid.NewGuid(),name,$"{name}@aicare.local",PasswordHasher.HashPassword("Admin123!"),UserRole.BackOffice,true,org,branch,null,null));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("insert into finance_funders(id,organization_id,name,normalized_name,funder_type,currency,created_by,updated_by) values({0},{1},'Council','COUNCIL','LocalAuthority','GBP','test','test')",funder,org);
            await Seed(db,org,branch,funder,"Missing",0m,30,new byte[]{1,2,3});
            await db.Database.ExecuteSqlRawAsync("delete from finance_invoice_profiles where organization_id={0}",org);
        }
        var client=factory.CreateClient(); var login=await client.PostAsJsonAsync("/api/auth/login",new{userName=name,password="Admin123!",mfaCode=(string?)null});login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString());
        var request=new{periodStart="2048-01-01",periodEnd="2048-01-31",funderId=funder,idempotencyKey=$"RECOVER-{Guid.NewGuid()}"};
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync("/api/phase1/finance/invoice-runs",request)).StatusCode);
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            Assert.False(await db.Invoices.AnyAsync(x=>x.OrganizationId==org));
            Assert.Equal(0,await db.Database.SqlQueryRaw<int>("select count(*)::int as \"Value\" from finance_invoice_runs where organization_id={0}",org).SingleAsync());
            await db.Database.ExecuteSqlRawAsync("insert into finance_invoice_profiles(organization_id,branch_id,provider_name,default_vat_rate,vat_exemption_reason) values({0},{1},'Configured provider',0,'Explicit test setting')",org,branch);
        }
        Assert.Equal(HttpStatusCode.Created,(await client.PostAsJsonAsync("/api/phase1/finance/invoice-runs",request)).StatusCode);
    }

    [Fact]
    public async Task OrganizationRunUsesEachBranchSettingsAndSnapshotsPayerSeparately()
    {
        await factory.EnsureClinicalSeedAsync();
        var organization=Guid.NewGuid(); var a=Guid.NewGuid(); var b=Guid.NewGuid(); var funder=Guid.NewGuid(); var name=$"branch.finance.{Guid.NewGuid():N}";
        var logoA=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jhXQAAAAASUVORK5CYII=");
        var logoB=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        using(var scope=factory.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Organizations.Add(new Organization(organization,"Branch provider","Trial","Active"));
            db.Branches.AddRange(new Branch(a,organization,"A","Test","Active"),new Branch(b,organization,"B","Test","Active"));
            // This test requests an organisation-wide run; branch-assigned finance
            // operators must not gain access to the other branch implicitly.
            db.AppUsers.Add(new AppUser(Guid.NewGuid(),name,$"{name}@aicare.local",PasswordHasher.HashPassword("Admin123!"),UserRole.Administrator,true,organization,null,null,null));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("insert into finance_funders(id,organization_id,name,normalized_name,funder_type,billing_address,currency,created_by,updated_by) values({0},{1},'Liable Council','LIABLE COUNCIL','LocalAuthority','Council billing address','GBP','test','test')",funder,organization);
            await Seed(db,organization,a,funder,"A",0m,7,logoA);
            await Seed(db,organization,b,funder,"B",20m,45,logoB);
        }
        var client=factory.CreateClient(); var login=await client.PostAsJsonAsync("/api/auth/login",new{userName=name,password="Admin123!",mfaCode=(string?)null}); login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString());
        var request=new{periodStart="2048-01-01",periodEnd="2048-01-31",funderId=funder,idempotencyKey=$"BRANCH-{Guid.NewGuid()}"};
        var response=await client.PostAsJsonAsync("/api/phase1/finance/invoice-runs",request); Assert.Equal(HttpStatusCode.Created,response.StatusCode);
        using var verify=factory.Services.CreateScope(); var verifyDb=verify.ServiceProvider.GetRequiredService<CareDbContext>();
        var snapshots=await verifyDb.Database.SqlQueryRaw<Snapshot>("""
            select invoice_id as "InvoiceId",branch_id as "BranchId",provider_name as "Provider",customer_name as "Payer",customer_address as "PayerAddress",
              care_recipient_name as "Recipient",remittance_details as "Remittance",due_date-invoice_date as "Terms",vat_rate as "VatRate",gross_amount as "Gross",logo_bytes as "Logo"
            from finance_invoice_details where organization_id={0}
            """,organization).ToListAsync();
        Assert.Equal(2,snapshots.Count);
        foreach(var item in snapshots)
        {
            var isA=item.BranchId==a;
            Assert.Equal(isA?"Provider A":"Provider B",item.Provider); Assert.Equal(isA?7:45,item.Terms);
            Assert.Equal(isA?0m:20m,item.VatRate); Assert.Equal(isA?100m:120m,item.Gross);
            Assert.Equal(isA?logoA:logoB,item.Logo); Assert.Equal(isA?"Remit A":"Remit B",item.Remittance);
            Assert.Equal("Liable Council",item.Payer); Assert.Equal("Council billing address",item.PayerAddress);
            Assert.Equal(isA?"Recipient A":"Recipient B",item.Recipient);
        }
        // Profile changes cannot rewrite already captured financial history.
        await verifyDb.Database.ExecuteSqlRawAsync("update finance_invoice_profiles set provider_name='Changed',default_vat_rate=5 where organization_id={0}",organization);
        Assert.Equal("Provider B",await verifyDb.Database.SqlQueryRaw<string>("select provider_name as \"Value\" from finance_invoice_details where organization_id={0} and branch_id={1}",organization,b).SingleAsync());
        var replay=await client.PostAsJsonAsync("/api/phase1/finance/invoice-runs",request); Assert.Equal(HttpStatusCode.OK,replay.StatusCode);
    }

    private static async Task Seed(CareDbContext db,Guid org,Guid branch,Guid funder,string label,decimal vat,int terms,byte[] logo)
    {
        var person=Guid.NewGuid(); var visit=Guid.NewGuid(); var worker=Guid.NewGuid(); var card=Guid.NewGuid(); var version=Guid.NewGuid(); var rule=Guid.NewGuid(); var arrangement=Guid.NewGuid(); var billable=Guid.NewGuid();
        db.ServiceUsers.Add(new ServiceUser(person,$"Recipient {label}",new DateOnly(1950,1,1),"1","Care","Contact","",RiskLevel.Low,"Active","Care home address","None","None","Private","","","Independent","Independent","Verbal","Routine","Standard",org,branch));
        db.CareWorkers.Add(new CareWorker(worker,$"Worker {label}","Care","Flexible",0,0,"Valid","Compliant","10",org,branch));
        db.Visits.Add(new Visit(visit,person,worker,new DateTimeOffset(2048,1,2,9,0,0,TimeSpan.Zero),"Care",60,"Care",VisitStatus.Completed,null,null,null,null,null,null,org,branch));
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("""
            insert into finance_invoice_profiles(organization_id,branch_id,provider_name,default_vat_rate,default_payment_terms_days,remittance_details,vat_exemption_reason,logo_content_type,logo_bytes,logo_sha256)
            values({0},{1},{2},{3},{4},{5},'Explicit test treatment','image/png',{6},{7});
            insert into finance_rate_cards(id,organization_id,branch_id,name,service_type,currency,created_by,updated_by) values({8},{0},{1},{2},'Care','GBP','test','test');
            insert into finance_rate_versions(id,rate_card_id,organization_id,branch_id,version,effective_from,status,created_by) values({9},{8},{0},{1},1,'2048-01-01','Active','test');
            insert into finance_rate_rules(id,rate_version_id,organization_id,rule_type,day_type,unit,unit_rate,created_by) values({10},{9},{0},'Standard','Any','Hour',100,'test');
            insert into funding_arrangements(id,service_user_id,organization_id,branch_id,funding_source,funder_name,authorized_hours_per_week,hourly_rate,valid_from,status,funder_id,rate_card_id,created_by)
              values({11},{12},{0},{1},'LocalAuthority','Liable Council',168,100,'2048-01-01','Active',{13},{8},'test');
            insert into finance_billable_events(id,organization_id,branch_id,service_user_id,visit_id,funder_id,funding_arrangement_id,rate_version_id,rate_rule_id,service_date,quantity,unit,unit_rate,gross_amount,final_amount,currency,calculation_explanation,status,created_by,approved_at,approved_by)
              values({14},{0},{1},{12},{15},{13},{11},{9},{10},'2048-01-02',1,'Hour',100,100,100,'GBP','Explicit test source','Validated','test',now(),'reviewer')
            """,org,branch,$"Provider {label}",vat,terms,$"Remit {label}",logo,Convert.ToHexString(SHA256.HashData(logo)).ToLowerInvariant(),card,version,rule,arrangement,person,funder,billable,visit);
        await ApprovedBillingTestData.Bind(db,billable);
    }

    private sealed record Snapshot(Guid InvoiceId,Guid BranchId,string Provider,string Payer,string PayerAddress,string Recipient,string Remittance,int Terms,decimal VatRate,decimal Gross,byte[] Logo);
}
