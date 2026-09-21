using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiCare.Tests;

[Collection("Postgres regression")]
public sealed class InvoiceLifecycleRegressionTests(PostgresRegressionFactory factory):IClassFixture<PostgresRegressionFactory>
{
 [Fact]
 public async Task NumberingSnapshotsPdfCreditsRefundsAgeingDeliveryConcurrencyAndTenantScopeAreGoverned()
 {
  await factory.EnsureClinicalSeedAsync();var firstPerson=Guid.NewGuid();var secondPerson=Guid.NewGuid();var firstVisit=Guid.NewGuid();var secondVisit=Guid.NewGuid();var financeName=$"invoice.finance.{Guid.NewGuid():N}";var foreignName=$"invoice.foreign.{Guid.NewGuid():N}";var foreignOrganization=Guid.NewGuid();var foreignBranch=Guid.NewGuid();var starts=new DateTimeOffset(2038,4,1,9,0,0,TimeSpan.Zero);
  using(var scope=factory.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();db.ServiceUsers.AddRange(Person(firstPerson,"Invoice Customer One"),Person(secondPerson,"Invoice Customer Two"));db.Visits.AddRange(Visit(firstVisit,firstPerson,starts,60),Visit(secondVisit,secondPerson,starts.AddHours(2),30));db.Organizations.Add(new Organization(foreignOrganization,"Foreign Provider","Trial","Active"));db.Branches.Add(new Branch(foreignBranch,foreignOrganization,"Foreign Branch","Test","Active"));db.AppUsers.AddRange(User(financeName,TenantDefaults.OrganizationId,TenantDefaults.BranchId),User(foreignName,foreignOrganization,foreignBranch));await db.SaveChangesAsync();}
  var finance=await Login(financeName);var foreign=await Login(foreignName);
  Assert.Equal(HttpStatusCode.NoContent,(await finance.PutAsJsonAsync("/api/phase1/finance/invoice-profile",new{providerName="Governed Care Ltd",providerAddress="1 Care Street, London",providerEmail="billing@care.invalid",providerPhone="020 0000 0000",companyNumber="12345678",vatNumber="GB123456789",remittanceDetails="Quote the invoice number when paying.",paymentTermsDays=10,vatRate=20m,vatExemptionReason=""})).StatusCode);
  Assert.Contains("Governed Care Ltd",await finance.GetStringAsync("/api/phase1/finance/invoice-profile"));
  var generated=await finance.PostAsJsonAsync("/api/phase1/finance/invoice-batches",new{periodStart=starts.AddHours(-1),periodEnd=starts.AddDays(1),defaultHourlyRate=100m,mileageRate=0m});Assert.Equal(HttpStatusCode.Created,generated.StatusCode);var batch=(await generated.Content.ReadFromJsonAsync<InvoiceBatch>())!;Assert.Equal(2,batch.Count);Assert.Equal(2,batch.Invoices.Select(x=>x.InvoiceNumber).Distinct().Count());var first=batch.Invoices.Single(x=>x.ServiceUserId==firstPerson);var second=batch.Invoices.Single(x=>x.ServiceUserId==secondPerson);Assert.Equal(120m,first.Amount);
  Assert.Equal(HttpStatusCode.Conflict,(await finance.PostAsJsonAsync($"/api/phase1/finance/invoices/{first.Id}/payments",new{amount=1m,reference="EARLY",receivedAt=(DateTimeOffset?)null})).StatusCode);
  var approvals=await Task.WhenAll(finance.PostAsync($"/api/phase1/finance/invoices/{first.Id}/approve",null),finance.PostAsync($"/api/phase1/finance/invoices/{first.Id}/approve",null));Assert.Equal(1,approvals.Count(x=>x.StatusCode==HttpStatusCode.OK));Assert.Equal(1,approvals.Count(x=>x.StatusCode==HttpStatusCode.Conflict));
  Assert.Equal(HttpStatusCode.OK,(await finance.PostAsync($"/api/phase1/finance/invoices/{first.Id}/issue",null)).StatusCode);
  var detail=await finance.GetStringAsync($"/api/phase1/finance/invoices/{first.Id}");Assert.Contains("Governed Care Ltd",detail);Assert.Contains("Invoice Customer One",detail);Assert.Contains("Private",detail);Assert.Contains("GB123456789",detail);Assert.Contains("Quote the invoice number",detail);
  var pdf=await finance.GetAsync($"/api/phase1/finance/invoices/{first.Id}/pdf");Assert.Equal(HttpStatusCode.OK,pdf.StatusCode);Assert.Equal("application/pdf",pdf.Content.Headers.ContentType?.MediaType);Assert.StartsWith("%PDF",Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..4]));Assert.Equal(HttpStatusCode.NotFound,(await foreign.GetAsync($"/api/phase1/finance/invoices/{first.Id}/pdf")).StatusCode);Assert.Equal(HttpStatusCode.NotFound,(await foreign.GetAsync($"/api/phase1/finance/invoices/{first.Id}/lines")).StatusCode);
  var payments=await Task.WhenAll(finance.PostAsJsonAsync($"/api/phase1/finance/invoices/{first.Id}/payments",new{amount=80m,reference="RACE-A",receivedAt=(DateTimeOffset?)null}),finance.PostAsJsonAsync($"/api/phase1/finance/invoices/{first.Id}/payments",new{amount=80m,reference="RACE-B",receivedAt=(DateTimeOffset?)null}));Assert.Equal(1,payments.Count(x=>x.StatusCode==HttpStatusCode.Created));Assert.Equal(1,payments.Count(x=>x.StatusCode==HttpStatusCode.Conflict));Assert.Equal(HttpStatusCode.Created,(await finance.PostAsJsonAsync($"/api/phase1/finance/invoices/{first.Id}/payments",new{amount=40m,reference="FINAL",receivedAt=(DateTimeOffset?)null})).StatusCode);
  Assert.Equal(HttpStatusCode.Created,(await finance.PostAsJsonAsync($"/api/phase1/finance/invoices/{first.Id}/credit-notes",new{amount=10m,reason="Service adjustment"})).StatusCode);Assert.Equal(HttpStatusCode.Created,(await finance.PostAsJsonAsync($"/api/phase1/finance/invoices/{first.Id}/refunds",new{amount=10m,reference="REFUND-1",reason="Credit returned"})).StatusCode);
  Assert.Equal(HttpStatusCode.OK,(await finance.PostAsync($"/api/phase1/finance/invoices/{second.Id}/approve",null)).StatusCode);Assert.Equal(HttpStatusCode.OK,(await finance.PostAsync($"/api/phase1/finance/invoices/{second.Id}/issue",null)).StatusCode);Assert.Equal(HttpStatusCode.ServiceUnavailable,(await finance.PostAsJsonAsync($"/api/phase1/finance/invoices/{second.Id}/deliver",new{recipientEmail="customer@example.invalid"})).StatusCode);
  using(var scope=factory.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();await db.Database.ExecuteSqlRawAsync("update finance_invoice_details set invoice_date=current_date-31,due_date=current_date-1 where invoice_id={0}",second.Id);await Assert.ThrowsAnyAsync<Exception>(()=>db.Database.ExecuteSqlRawAsync("update finance_invoice_lines set amount=amount+1 where invoice_id={0}",first.Id));}
  var aged=await finance.GetStringAsync("/api/phase1/finance/aged-receivables");Assert.Contains(second.InvoiceNumber,aged);Assert.Contains("Overdue",aged);
  using var verify=factory.Services.CreateScope();var verifyDb=verify.ServiceProvider.GetRequiredService<CareDbContext>();Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="finance.invoice_pdf_downloaded"&&x.EntityId==first.Id));Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="finance.credit_note_issued"));Assert.True(await verifyDb.AuditEvents.AnyAsync(x=>x.Action=="finance.refund_recorded"));
 }
 private static ServiceUser Person(Guid id,string name)=>new(id,name,new DateOnly(1950,1,1),"+100","Care","Contact","",RiskLevel.Low,"Active","1 Customer Road","None","None","Private","","","Independent","Independent","Verbal","None","Standard",TenantDefaults.OrganizationId,TenantDefaults.BranchId);
 private static Visit Visit(Guid id,Guid person,DateTimeOffset at,int minutes)=>new(id,person,RegressionIds.WorkerId,at,"Personal care",minutes,"Personal care",VisitStatus.Completed,null,null,null,null,null,null,TenantDefaults.OrganizationId,TenantDefaults.BranchId);
 private static AppUser User(string name,Guid organization,Guid branch)=>new(Guid.NewGuid(),name,$"{name}@aicare.local",PasswordHasher.HashPassword("Admin123!"),UserRole.BackOffice,true,organization,branch,null,null);
 private async Task<HttpClient> Login(string name){var client=factory.CreateClient();var response=await client.PostAsJsonAsync("/api/auth/login",new{userName=name,password="Admin123!",mfaCode=(string?)null});response.EnsureSuccessStatusCode();client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString());return client;}
 private sealed record InvoiceBatch(int Count,List<InvoiceRow> Invoices);private sealed record InvoiceRow(Guid Id,Guid ServiceUserId,string InvoiceNumber,decimal Amount);
}
