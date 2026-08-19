using System.Net.Http.Json;
using AiCare.Application;
using AiCare.Application.FamilyPortal;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[ApiController]
[Authorize(Policy = "Phase1User")]
[Route("api/family/service-users/{serviceUserId:guid}/documents")]
public sealed class FamilyDocumentDownloadController(
    IFamilyPortalService familyPortal,
    IFamilyPortalQueryService familyQueries,
    CareDbContext db,
    ITenantContext tenant,
    ICurrentUserContext currentUser,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory) : ControllerBase
{
    [HttpGet("{documentId:guid}/download-url")]
    public async Task<IActionResult> GetDownloadUrl(Guid serviceUserId, Guid documentId, CancellationToken cancellationToken)
    {
        if (!currentUser.IsFamilyMember || currentUser.FamilyMemberId is null) return Forbid();

        try
        {
            await familyPortal.EnsurePermissionAsync(
                tenant.OrganizationId,
                currentUser.FamilyMemberId.Value,
                serviceUserId,
                FamilyPermissions.ViewDocuments,
                cancellationToken);

            var shared = await familyQueries.GetDocumentsAsync(
                tenant.OrganizationId,
                currentUser.FamilyMemberId.Value,
                serviceUserId,
                cancellationToken);
            if (!shared.Any(item => item.Id == documentId)) return NotFound();

            var document = await db.Documents.AsNoTracking().FirstOrDefaultAsync(item =>
                item.Id == documentId &&
                item.ServiceUserId == serviceUserId &&
                item.OrganizationId == tenant.OrganizationId,
                cancellationToken);
            if (document is null) return NotFound();

            if (!document.StoragePath.StartsWith("supabase://", StringComparison.OrdinalIgnoreCase))
                return Conflict(new { message = "This document is not stored in cloud storage and cannot be opened through the Family Portal." });

            var signedUrl = await CreateSupabaseSignedUrl(document.StoragePath, cancellationToken);
            return Ok(new { provider = "Supabase", url = signedUrl, expiresInSeconds = 900 });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    private async Task<string> CreateSupabaseSignedUrl(string storagePath, CancellationToken cancellationToken)
    {
        var supabaseUrl = configuration["Supabase:Url"]?.TrimEnd('/')
            ?? throw new InvalidOperationException("Supabase:Url is not configured.");
        var serviceRoleKey = configuration["Supabase:ServiceRoleKey"]
            ?? throw new InvalidOperationException("Supabase:ServiceRoleKey is not configured.");
        var (bucket, objectKey) = ParseStoragePath(storagePath);

        var encodedPath = string.Join('/', objectKey.Split('/').Select(Uri.EscapeDataString));
        var request = new HttpRequestMessage(HttpMethod.Post, $"{supabaseUrl}/storage/v1/object/sign/{Uri.EscapeDataString(bucket)}/{encodedPath}")
        {
            Content = JsonContent.Create(new { expiresIn = 900 })
        };
        request.Headers.TryAddWithoutValidation("apikey", serviceRoleKey);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {serviceRoleKey}");

        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<SignedUrlResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Supabase did not return a signed document URL.");
        var relative = payload.signedURL ?? payload.signedUrl;
        if (string.IsNullOrWhiteSpace(relative)) throw new InvalidOperationException("Supabase did not return a signed document URL.");
        return relative.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? relative
            : $"{supabaseUrl}/storage/v1{(relative.StartsWith('/') ? relative : $"/{relative}")}";
    }

    private static (string Bucket, string ObjectKey) ParseStoragePath(string storagePath)
    {
        var value = storagePath["supabase://".Length..];
        var separator = value.IndexOf('/');
        if (separator <= 0 || separator == value.Length - 1)
            throw new InvalidOperationException("Invalid Supabase document storage path.");
        return (value[..separator], value[(separator + 1)..]);
    }

    private sealed record SignedUrlResponse(string? signedURL, string? signedUrl);
}
