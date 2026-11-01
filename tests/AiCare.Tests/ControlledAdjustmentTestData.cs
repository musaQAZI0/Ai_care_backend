using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AiCare.Api;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace AiCare.Tests;

internal static class ControlledAdjustmentTestData
{
 internal static async Task<HttpResponseMessage> Post(PostgresRegressionFactory factory,HttpClient submitter,Guid invoice,string kind,decimal amount,string reference,string reason)
 {
  var name=$"adjustment.reviewer.{Guid.NewGuid():N}";var reviewer=Guid.NewGuid();DateOnly service;decimal net,tax;
  using(var scope=factory.Services.CreateScope())
  {
   var db=scope.ServiceProvider.GetRequiredService<CareDbContext>();var source=await db.Invoices.AsNoTracking().SingleAsync(i=>i.Id==invoice);var branch=source.BranchId??TenantDefaults.BranchId;
   db.AppUsers.Add(new AppUser(reviewer,name,name+"@test.invalid",PasswordHasher.HashPassword("Admin123!"),UserRole.BackOffice,true,source.OrganizationId,branch));await db.SaveChangesAsync();
   var detail=await db.Database.SqlQueryRaw<Detail>("select net_amount as \"Net\",gross_amount as \"Gross\",invoice_date as \"Date\" from finance_invoice_details where invoice_id={0}",invoice).SingleAsync();
   net=detail.Gross==0?0:Math.Round(amount*detail.Net/detail.Gross,2,MidpointRounding.AwayFromZero);tax=amount-net;service=detail.Date;
   // Explicit synthetic authority; no production approval policy is inferred here.
   await db.Database.ExecuteSqlRawAsync("insert into finance_adjustment_authorities(id,organization_id,branch_id,reviewer_id,currency,maximum_amount,valid_from,valid_to,reference,created_by,approved_by,approved_at) select {0},organization_id,branch_id,{1},currency,1000000,current_date-1,current_date+366,'EXPLICIT-TEST-AUTHORITY',{2},{3},now() from finance_invoice_details where invoice_id={4}",Guid.NewGuid(),reviewer,Guid.NewGuid(),Guid.NewGuid(),invoice);
  }
  var id=Guid.NewGuid();var request=new FinancialAdjustmentRequest(id,invoice,kind,amount,reason,reference,kind=="Credit"?net:null,kind=="Credit"?tax:null,kind=="Credit"?service:null);
  var created=await submitter.PostAsJsonAsync("/api/phase1/finance/adjustments",request);if(!created.IsSuccessStatusCode)return created;
  var client=factory.CreateClient();var login=await client.PostAsJsonAsync("/api/auth/login",new{userName=name,password="Admin123!",mfaCode=(string?)null});login.EnsureSuccessStatusCode();client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",(await login.Content.ReadFromJsonAsync<Token>())!.Value);
  var result=await client.PostAsJsonAsync($"/api/phase1/finance/adjustments/{id}/review",new{decision="Approve",reason="Independent synthetic test review"});if(result.StatusCode==HttpStatusCode.OK)result.StatusCode=HttpStatusCode.Created;return result;
 }
 sealed record Detail(decimal Net,decimal Gross,DateOnly Date);
 sealed record Token([property:System.Text.Json.Serialization.JsonPropertyName("token")]string Value);
}
