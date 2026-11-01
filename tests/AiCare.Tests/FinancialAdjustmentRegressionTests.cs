using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AiCare.Api;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace AiCare.Tests;
[Collection("Postgres regression")]
public sealed class FinancialAdjustmentRegressionTests(PostgresRegressionFactory factory):IClassFixture<PostgresRegressionFactory>
{
 [Fact]public async Task LegacyAttributionAppendsReviewedReportingWithoutRewritingTheCreditOrBalance()
 {
  await factory.EnsureClinicalSeedAsync();var org=Guid.NewGuid();var branch=Guid.NewGuid();var person=Guid.NewGuid();var invoice=Guid.NewGuid();var credit=Guid.NewGuid();var authorId=Guid.NewGuid();var reviewerId=Guid.NewGuid();var grantorId=Guid.NewGuid();var prefix=$"legacy.{Guid.NewGuid():N}";
  using(var scope=factory.Services.CreateScope()){
   var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();db.Organizations.Add(new Organization(org,"Legacy attribution test","Trial","Active"));db.Branches.Add(new Branch(branch,org,"Main","Test","Active"));db.ServiceUsers.Add(new ServiceUser(person,"Recipient",new(1950,1,1),"1","Care","Contact","",RiskLevel.Low,"Active","Address","None","None","Private","","","Independent","Independent","Verbal","Routine","Standard",org,branch));foreach(var x in new[]{(authorId,"author"),(reviewerId,"reviewer"),(grantorId,"grantor")})db.AppUsers.Add(new AppUser(x.Item1,prefix+x.Item2,prefix+x.Item2+"@test.invalid",PasswordHasher.HashPassword("Admin123!"),UserRole.BackOffice,true,org,branch));db.Invoices.Add(new Invoice(invoice,person,"Payer",100,"Issued",DateTimeOffset.UtcNow,org,branch));await db.SaveChangesAsync();
   await db.Database.ExecuteSqlRawAsync("insert into finance_invoice_details(invoice_id,organization_id,branch_id,invoice_number,invoice_date,due_date,payment_terms,provider_name,customer_name,net_amount,vat_rate,vat_amount,gross_amount) values({0},{1},{2},{3},current_date,current_date+30,'30 days','Provider','Payer',80,25,20,100)",invoice,org,branch,$"LEGACY-{invoice}");
   // Reproduce pre-migration history ONLY in the factory-owned throwaway regression database.
   Assert.StartsWith("aicare_regression_",db.Database.GetDbConnection().Database);await using var tx=await db.Database.BeginTransactionAsync();await db.Database.ExecuteSqlRawAsync("alter table finance_credit_notes disable trigger finance_controlled_credit");await db.Database.ExecuteSqlRawAsync("insert into finance_credit_notes(id,invoice_id,organization_id,branch_id,credit_number,amount,reason,issued_by) values({0},{1},{2},{3},{4},20,'Historical test credit','Historical actor')",credit,invoice,org,branch,$"LEGACY-CR-{credit}");await db.Database.ExecuteSqlRawAsync("alter table finance_credit_notes enable trigger finance_controlled_credit");await tx.CommitAsync();
  }
  var author=await Login(prefix+"author");var reviewer=await Login(prefix+"reviewer");var grantor=await Login(prefix+"grantor");var authority=new AdjustmentAuthorityRequest(Guid.NewGuid(),branch,reviewerId,"GBP",100,new(2026,1,1),new(2099,12,31),"SYNTHETIC-LEGACY-AUTHORITY");(await author.PostAsJsonAsync(Root+"/authorities",authority)).EnsureSuccessStatusCode();(await grantor.PostAsync($"{Root}/authorities/{authority.RequestId}/approve",null)).EnsureSuccessStatusCode();
  var request=new LegacyCreditAttributionRequest(Guid.NewGuid(),credit,16,4,new(2054,1,10),"HISTORICAL-EVIDENCE","Reviewed historical service and tax split");(await author.PostAsJsonAsync(Root+"/legacy-credit-attributions",request)).EnsureSuccessStatusCode();Assert.Equal(HttpStatusCode.OK,(await author.PostAsJsonAsync(Root+"/legacy-credit-attributions",request)).StatusCode);Assert.Equal(HttpStatusCode.Conflict,(await author.PostAsJsonAsync($"{Root}/legacy-credit-attributions/{request.RequestId}/approve",new{})).StatusCode);var approvals=await Task.WhenAll(reviewer.PostAsync($"{Root}/legacy-credit-attributions/{request.RequestId}/approve",null),reviewer.PostAsync($"{Root}/legacy-credit-attributions/{request.RequestId}/approve",null));Assert.All(approvals,r=>r.EnsureSuccessStatusCode());
  using var verify=factory.Services.CreateScope();var verifyDb=verify.ServiceProvider.GetRequiredService<CareDbContext>();Assert.Equal(80,await verifyDb.Database.SqlQueryRaw<decimal>("select outstanding as \"Value\" from finance_invoice_ledger where invoice_id={0}",invoice).SingleAsync());Assert.Equal(16,await verifyDb.Database.SqlQueryRaw<decimal>("select net_amount as \"Value\" from finance_credit_reporting where id={0}",credit).SingleAsync());Assert.True(await verifyDb.Database.SqlQueryRaw<bool>("select net_amount is null as \"Value\" from finance_credit_notes where id={0}",credit).SingleAsync());await Assert.ThrowsAnyAsync<Exception>(()=>verifyDb.Database.ExecuteSqlRawAsync("update finance_legacy_credit_attributions set net_amount=17 where id={0}",request.RequestId));
 }
 [Fact]
 public async Task IndependentAuthorityControlsPostingAndCreditsWriteoffsDisputesRemainDistinct()
 {
  await factory.EnsureClinicalSeedAsync();var org=Guid.NewGuid();var branch=Guid.NewGuid();var person=Guid.NewGuid();var invoice=Guid.NewGuid();var prefix=$"adjust.{Guid.NewGuid():N}";var authorId=Guid.NewGuid();var reviewerId=Guid.NewGuid();var grantorId=Guid.NewGuid();
  using(var scope=factory.Services.CreateScope())
  {
   var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();db.Organizations.Add(new Organization(org,"Adjustment test","Trial","Active"));db.Branches.Add(new Branch(branch,org,"Main","Test","Active"));db.ServiceUsers.Add(new ServiceUser(person,"Recipient",new DateOnly(1950,1,1),"1","Care","Contact","",RiskLevel.Low,"Active","Address","None","None","Private","","","Independent","Independent","Verbal","Routine","Standard",org,branch));
   foreach(var actor in new[]{(authorId,"author"),(reviewerId,"reviewer"),(grantorId,"grantor")})db.AppUsers.Add(new AppUser(actor.Item1,prefix+actor.Item2,prefix+actor.Item2+"@test.invalid",PasswordHasher.HashPassword("Admin123!"),UserRole.BackOffice,true,org,branch));db.Invoices.Add(new Invoice(invoice,person,"Payer",100,"Issued",DateTimeOffset.UtcNow,org,branch));await db.SaveChangesAsync();
   await db.Database.ExecuteSqlRawAsync("insert into finance_invoice_details(invoice_id,organization_id,branch_id,invoice_number,invoice_date,due_date,payment_terms,provider_name,customer_name,net_amount,vat_rate,vat_amount,gross_amount) values({0},{1},{2},{3},current_date,current_date+30,'30 days','Provider','Payer',80,25,20,100)",invoice,org,branch,$"ADJ-{invoice}");
  }
  var author=await Login(prefix+"author");var reviewer=await Login(prefix+"reviewer");var grantor=await Login(prefix+"grantor");
  var authority=new AdjustmentAuthorityRequest(Guid.NewGuid(),branch,reviewerId,"GBP",25,new(2026,1,1),new(2099,12,31),"APPROVED-TEST-AUTHORITY");
  (await author.PostAsJsonAsync(Root+"/authorities",authority)).EnsureSuccessStatusCode();
  Assert.Equal(HttpStatusCode.Conflict,(await author.PostAsync($"{Root}/authorities/{authority.RequestId}/approve",null)).StatusCode);
  Assert.Equal(HttpStatusCode.Conflict,(await reviewer.PostAsync($"{Root}/authorities/{authority.RequestId}/approve",null)).StatusCode);
  (await grantor.PostAsync($"{Root}/authorities/{authority.RequestId}/approve",null)).EnsureSuccessStatusCode();
  var request=new FinancialAdjustmentRequest(Guid.NewGuid(),invoice,"Credit",20,"Correct service charge","SERVICE-CORRECTION",16,4,new(2054,1,10),"CORRECTION-CASE-1");
  (await author.PostAsJsonAsync(Root,request)).EnsureSuccessStatusCode();Assert.Equal(HttpStatusCode.OK,(await author.PostAsJsonAsync(Root,request)).StatusCode);
  Assert.Equal(HttpStatusCode.Conflict,(await author.PostAsJsonAsync(Root,request with{Reason="Changed"})).StatusCode);
  Assert.Equal(HttpStatusCode.Conflict,(await author.PostAsJsonAsync($"{Root}/{request.RequestId}/review",new{decision="Approve",reason="Self"})).StatusCode);
  Assert.Equal(HttpStatusCode.Conflict,(await grantor.PostAsJsonAsync($"{Root}/{request.RequestId}/review",new{decision="Approve",reason="No posting authority"})).StatusCode);
  var review=new{decision="Approve",reason="Reviewed source net and tax"};var attempts=await Task.WhenAll(reviewer.PostAsJsonAsync($"{Root}/{request.RequestId}/review",review),reviewer.PostAsJsonAsync($"{Root}/{request.RequestId}/review",review));Assert.All(attempts,r=>r.EnsureSuccessStatusCode());
  var excessive=request with{RequestId=Guid.NewGuid(),Amount=30,NetAmount=24,TaxAmount=6,Reference="EXCEEDS-AUTHORITY"};(await author.PostAsJsonAsync(Root,excessive)).EnsureSuccessStatusCode();Assert.Equal(HttpStatusCode.Conflict,(await reviewer.PostAsJsonAsync($"{Root}/{excessive.RequestId}/review",review)).StatusCode);
  (await author.PostAsJsonAsync($"/api/phase1/finance/invoices/{invoice}/payments",new{amount=50m,reference="BANK"})).EnsureSuccessStatusCode();
  var dispute=new InvoiceDisputeRequest(Guid.NewGuid(),invoice,10,"Questioned charge","EVIDENCE-CASE");(await author.PostAsJsonAsync(Root+"/disputes",dispute)).EnsureSuccessStatusCode();(await reviewer.PostAsJsonAsync($"{Root}/disputes/{dispute.RequestId}/resolve",new{reason="Agreed financial correction"})).EnsureSuccessStatusCode();
  var larger=authority with{RequestId=Guid.NewGuid(),MaximumAmount=100,Reference="LARGER-APPROVED-TEST-LIMIT"};(await author.PostAsJsonAsync(Root+"/authorities",larger)).EnsureSuccessStatusCode();(await grantor.PostAsync($"{Root}/authorities/{larger.RequestId}/approve",null)).EnsureSuccessStatusCode();
  var writeoff=new FinancialAdjustmentRequest(Guid.NewGuid(),invoice,"WriteOff",30,"Approved uncollectable debt","WRITE-OFF-1");(await author.PostAsJsonAsync(Root,writeoff)).EnsureSuccessStatusCode();(await reviewer.PostAsJsonAsync($"{Root}/{writeoff.RequestId}/review",review)).EnsureSuccessStatusCode();
  Assert.Equal(HttpStatusCode.Gone,(await author.PostAsJsonAsync($"/api/phase1/finance/invoices/{invoice}/credit-notes",new{amount=1,reason="Bypass"})).StatusCode);
  using var verify=factory.Services.CreateScope();var verifyDb=verify.ServiceProvider.GetRequiredService<CareDbContext>();var ledger=await verifyDb.Database.SqlQueryRaw<Ledger>("select credits as \"Credits\",write_offs as \"Written\",net_cash as \"Cash\",outstanding as \"Outstanding\",status as \"Status\" from finance_invoice_ledger where invoice_id={0}",invoice).SingleAsync();Assert.Equal(20,ledger.Credits);Assert.Equal(30,ledger.Written);Assert.Equal(50,ledger.Cash);Assert.Equal(0,ledger.Outstanding);Assert.Equal("Written off",ledger.Status);
  Assert.Equal(1,await verifyDb.Database.SqlQueryRaw<int>("select count(*)::int as \"Value\" from finance_credit_notes where invoice_id={0}",invoice).SingleAsync());
  await Assert.ThrowsAnyAsync<Exception>(()=>verifyDb.Database.ExecuteSqlRawAsync("update finance_adjustment_requests set amount=1 where id={0}",request.RequestId));
  await Assert.ThrowsAnyAsync<Exception>(()=>verifyDb.Database.ExecuteSqlRawAsync("delete from finance_write_offs where invoice_id={0}",invoice));
 }
 const string Root="/api/phase1/finance/adjustments";
 async Task<HttpClient> Login(string name){var c=factory.CreateClient();var r=await c.PostAsJsonAsync("/api/auth/login",new{userName=name,password="Admin123!",mfaCode=(string?)null});r.EnsureSuccessStatusCode();c.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await r.Content.ReadFromJsonAsync<Token>())!.Value);return c;}
 sealed record Token([property:System.Text.Json.Serialization.JsonPropertyName("token")]string Value);
 sealed record Ledger(decimal Credits,decimal Written,decimal Cash,decimal Outstanding,string Status);
}
