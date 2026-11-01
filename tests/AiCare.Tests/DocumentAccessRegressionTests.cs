using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AiCare.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiCare.Tests;

public sealed partial class MeasurableCareGoalsRegressionTests
{
    [Fact]
    public async Task DocumentsUseServerOwnedStorageMetadataAndEnforcePersonAccessAndWithdrawal()
    {
        var f = await Setup();

        using var upload = new MultipartFormDataContent();
        upload.Add(new StringContent(f.Person.ToString()), "serviceUserId");
        upload.Add(new StringContent("Support agreement"), "category");
        var bytes = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.7\nsecure regression document"));
        bytes.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        upload.Add(bytes, "file", "support-agreement.pdf");
        var created = await f.Manager.PostAsync("/api/phase1/documents/upload", upload);
        await Expect(created, HttpStatusCode.Created);
        var document = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = document.GetProperty("id").GetGuid();
        Assert.False(document.TryGetProperty("storagePath", out _));
        Assert.Equal("support-agreement.pdf", document.GetProperty("fileName").GetString());

        var assigned = await f.Worker.GetFromJsonAsync<JsonElement[]>("/api/phase1/documents");
        Assert.Contains(assigned!, item => item.GetProperty("id").GetGuid() == id);
        var unassigned = await f.Unassigned.GetFromJsonAsync<JsonElement[]>("/api/phase1/documents");
        Assert.DoesNotContain(unassigned!, item => item.GetProperty("id").GetGuid() == id);
        await Expect(await f.Unassigned.GetAsync($"/api/phase1/documents/{id}"), HttpStatusCode.NotFound);
        await Expect(await f.OtherBranch.GetAsync($"/api/phase1/documents/{id}/download-url"), HttpStatusCode.NotFound);
        await Expect(await (await ForeignManager()).GetAsync($"/api/phase1/documents/{id}/download-url"), HttpStatusCode.NotFound);

        var directMetadata = new
        {
            serviceUserId = f.Person,
            fileName = "forged.pdf",
            category = "Support agreement",
            storagePath = "C:\\private\\forged.pdf",
            uploadedBy = "forged actor"
        };
        await Expect(await f.Manager.PostAsJsonAsync("/api/phase1/documents", directMetadata), HttpStatusCode.MethodNotAllowed);
        await Expect(await f.Manager.PutAsJsonAsync($"/api/phase1/documents/{id}", directMetadata), HttpStatusCode.MethodNotAllowed);
        await Expect(await f.Manager.DeleteAsync($"/api/phase1/documents/{id}"), HttpStatusCode.BadRequest);
        await Expect(await f.Manager.DeleteAsync($"/api/phase1/documents/{id}?reason=Replaced%20by%20a%20signed%20copy"), HttpStatusCode.NoContent);
        await Expect(await f.Manager.GetAsync($"/api/phase1/documents/{id}"), HttpStatusCode.NotFound);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        var retained = await db.Documents.AsNoTracking().SingleAsync(item => item.Id == id);
        Assert.Equal("Withdrawn", retained.Category);
        Assert.Contains(await db.AuditEvents.AsNoTracking().Where(item => item.EntityId == id).Select(item => item.Action).ToListAsync(),
            action => action.StartsWith("document.withdrawn:", StringComparison.Ordinal));
    }
}
