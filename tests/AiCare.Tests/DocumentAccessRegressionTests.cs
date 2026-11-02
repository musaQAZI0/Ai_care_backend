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
        var storageStatus = await f.Manager.GetFromJsonAsync<JsonElement>("/api/phase1/storage/status");
        Assert.False(storageStatus.TryGetProperty("root", out _));

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
        var searched = await f.Manager.GetFromJsonAsync<JsonElement[]>("/api/phase1/documents?search=support-agreement&category=Support%20agreement");
        Assert.Contains(searched!, item => item.GetProperty("id").GetGuid() == id);
        var noMatch = await f.Manager.GetFromJsonAsync<JsonElement[]>("/api/phase1/documents?search=not-present");
        Assert.DoesNotContain(noMatch!, item => item.GetProperty("id").GetGuid() == id);
        await Expect(await f.Manager.GetAsync("/api/phase1/documents?uploadedFrom=2026-11-02T12%3A00%3A00Z&uploadedTo=2026-11-01T12%3A00%3A00Z"), HttpStatusCode.BadRequest);
        var unassigned = await f.Unassigned.GetFromJsonAsync<JsonElement[]>("/api/phase1/documents");
        Assert.DoesNotContain(unassigned!, item => item.GetProperty("id").GetGuid() == id);
        await Expect(await f.Unassigned.GetAsync($"/api/phase1/documents/{id}"), HttpStatusCode.NotFound);
        await Expect(await f.Worker.GetAsync($"/api/phase1/documents/{id}/history"), HttpStatusCode.Forbidden);
        await Expect(await f.OtherBranch.GetAsync($"/api/phase1/documents/{id}/download-url"), HttpStatusCode.NotFound);
        await Expect(await f.OtherBranch.GetAsync($"/api/phase1/documents/{id}/history"), HttpStatusCode.NotFound);
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
        var withdrawn = await f.Manager.GetFromJsonAsync<JsonElement[]>("/api/phase1/documents?includeWithdrawn=true");
        Assert.Contains(withdrawn!, item => item.GetProperty("id").GetGuid() == id && item.GetProperty("category").GetString() == "Support agreement" && item.GetProperty("status").GetString() == "Withdrawn");
        var history = await f.Manager.GetFromJsonAsync<JsonElement>($"/api/phase1/documents/{id}/history");
        Assert.Contains(history.GetProperty("history").EnumerateArray(), item => item.GetProperty("action").GetString()!.StartsWith("document.withdrawn:", StringComparison.Ordinal));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
        var retained = await db.Documents.AsNoTracking().SingleAsync(item => item.Id == id);
        Assert.Equal("Support agreement", retained.Category);
        var lifecycle = await db.DocumentLifecycleEvents.AsNoTracking().SingleAsync(item => item.DocumentId == id);
        Assert.Equal("Withdrawn", lifecycle.EventType);
        Assert.Equal("Replaced by a signed copy", lifecycle.Reason);
        Assert.Contains(await db.AuditEvents.AsNoTracking().Where(item => item.EntityId == id).Select(item => item.Action).ToListAsync(),
            action => action.StartsWith("document.withdrawn:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DocumentSupersessionPreservesCategoryAndLinksOnlySamePersonReplacement()
    {
        var f = await Setup();
        var sourceId = Guid.NewGuid();
        var replacementId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            db.Documents.AddRange(
                new AiCare.Domain.DocumentItem(sourceId, f.Person, "old-plan.pdf", "Care plan", "local://old-plan.pdf", "Regression", DateTimeOffset.UtcNow.AddMinutes(-2), TenantDefaults.OrganizationId, TenantDefaults.BranchId),
                new AiCare.Domain.DocumentItem(replacementId, f.Person, "signed-plan.pdf", "Care plan", "local://signed-plan.pdf", "Regression", DateTimeOffset.UtcNow, TenantDefaults.OrganizationId, TenantDefaults.BranchId));
            await db.SaveChangesAsync();
        }

        var response = await f.Manager.PostAsJsonAsync($"/api/phase1/documents/{sourceId}/supersede", new
        {
            replacementDocumentId = replacementId,
            reason = "Signed version received"
        });
        await Expect(response, HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Care plan", result.GetProperty("document").GetProperty("category").GetString());
        Assert.Equal("Superseded", result.GetProperty("document").GetProperty("status").GetString());
        Assert.Equal(replacementId, result.GetProperty("document").GetProperty("replacementDocumentId").GetGuid());
        await Expect(await f.Manager.GetAsync($"/api/phase1/documents/{sourceId}/download-url"), HttpStatusCode.NotFound);
        await Expect(await f.Manager.GetAsync($"/api/phase1/documents/{replacementId}"), HttpStatusCode.OK);
        var history = await f.Manager.GetFromJsonAsync<JsonElement>($"/api/phase1/documents/{replacementId}/history");
        var chain = history.GetProperty("replacementChain").EnumerateArray().ToArray();
        Assert.Equal(new[] { sourceId, replacementId }, chain.Select(item => item.GetProperty("id").GetGuid()).ToArray());

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<CareDbContext>();
        var lifecycle = await verifyDb.DocumentLifecycleEvents.AsNoTracking().SingleAsync(item => item.DocumentId == sourceId);
        Assert.Equal(replacementId, lifecycle.ReplacementDocumentId);
        Assert.Equal("Care plan", (await verifyDb.Documents.AsNoTracking().SingleAsync(item => item.Id == sourceId)).Category);
    }
}
