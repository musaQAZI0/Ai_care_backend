using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiCare.Domain;
using Xunit;
namespace AiCare.Tests;
internal static class TimesheetTestWorkflow
{
 internal static async Task<Guid> Prepare(HttpClient manager,DateTimeOffset start,DateTimeOffset end,decimal rate,decimal mileageRate,decimal mileage=0)
 {
  var created=await manager.PostAsJsonAsync("/api/phase1/timesheets/periods",new{branchId=TenantDefaults.BranchId,periodStart=start,periodEnd=end,hourlyRate=rate,travelRate=0m,mileageRate});
  Assert.Equal(HttpStatusCode.Created,created.StatusCode);
  var id=(await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();var url=$"/api/phase1/timesheets/periods/{id}";
  (await manager.PostAsync(url+"/import",null)).EnsureSuccessStatusCode();
  var detail=await manager.GetFromJsonAsync<JsonElement>(url);
  foreach(var entry in detail.GetProperty("entries").EnumerateArray())
  {
   var entryUrl=$"/api/phase1/timesheets/entries/{entry.GetProperty("id").GetGuid()}";
   var minutes=entry.GetProperty("actual_minutes");
   (await manager.PutAsJsonAsync(entryUrl,new{expectedRevision=1,payableMinutes=minutes.ValueKind==JsonValueKind.Null?entry.GetProperty("planned_minutes").GetDecimal():minutes.GetDecimal(),travelMinutes=0m,mileage,reason="Test fixture: verified duration and mileage"})).EnsureSuccessStatusCode();
   (await manager.PostAsJsonAsync(entryUrl+"/transition",new{expectedRevision=2,action="Submit",reason="Verified fixture evidence"})).EnsureSuccessStatusCode();
   (await manager.PostAsJsonAsync(entryUrl+"/transition",new{expectedRevision=3,action="Approve",reason="Fixture manager approval"})).EnsureSuccessStatusCode();
  }
  (await manager.PostAsJsonAsync(url+"/lock",new{expectedRevision=1})).EnsureSuccessStatusCode();return id;
 }
}
