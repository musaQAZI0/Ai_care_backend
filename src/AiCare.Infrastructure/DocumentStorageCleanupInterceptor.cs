using System.Net;
using AiCare.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace AiCare.Infrastructure;

public sealed class DocumentStorageCleanupInterceptor : SaveChangesInterceptor
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;

    public DocumentStorageCleanupInterceptor(IConfiguration configuration, IHttpClientFactory httpClientFactory)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        DeleteSupabaseObjects(eventData.Context);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await DeleteSupabaseObjectsAsync(eventData.Context, cancellationToken);
        return result;
    }

    private void DeleteSupabaseObjects(DbContext? context)
    {
        var documents = GetDeletedSupabaseDocuments(context);
        if (documents.Count == 0 || !UsesSupabaseStorage())
        {
            return;
        }

        var (supabaseUrl, serviceRoleKey) = GetSupabaseConfiguration();
        var client = _httpClientFactory.CreateClient();

        foreach (var document in documents)
        {
            var (bucket, objectKey) = ParseStoragePath(document.StoragePath);
            using var request = CreateDeleteRequest(supabaseUrl, serviceRoleKey, bucket, objectKey);
            using var response = client.Send(request);
            EnsureDeleteSucceeded(response, document.StoragePath);
        }
    }

    private async Task DeleteSupabaseObjectsAsync(DbContext? context, CancellationToken cancellationToken)
    {
        var documents = GetDeletedSupabaseDocuments(context);
        if (documents.Count == 0 || !UsesSupabaseStorage())
        {
            return;
        }

        var (supabaseUrl, serviceRoleKey) = GetSupabaseConfiguration();
        var client = _httpClientFactory.CreateClient();

        foreach (var document in documents)
        {
            var (bucket, objectKey) = ParseStoragePath(document.StoragePath);
            using var request = CreateDeleteRequest(supabaseUrl, serviceRoleKey, bucket, objectKey);
            using var response = await client.SendAsync(request, cancellationToken);
            await EnsureDeleteSucceededAsync(response, document.StoragePath, cancellationToken);
        }
    }

    private bool UsesSupabaseStorage() =>
        string.Equals(_configuration["Storage:Provider"], "Supabase", StringComparison.OrdinalIgnoreCase);

    private (string SupabaseUrl, string ServiceRoleKey) GetSupabaseConfiguration()
    {
        var supabaseUrl = _configuration["Supabase:Url"];
        var serviceRoleKey = _configuration["Supabase:ServiceRoleKey"];
        if (string.IsNullOrWhiteSpace(supabaseUrl) || string.IsNullOrWhiteSpace(serviceRoleKey))
        {
            throw new InvalidOperationException(
                "Supabase document deletion requires Supabase:Url and Supabase:ServiceRoleKey.");
        }

        return (supabaseUrl, serviceRoleKey);
    }

    private static IReadOnlyList<DocumentItem> GetDeletedSupabaseDocuments(DbContext? context)
    {
        if (context is null)
        {
            return Array.Empty<DocumentItem>();
        }

        return context.ChangeTracker
            .Entries<DocumentItem>()
            .Where(entry =>
                entry.State == EntityState.Deleted &&
                entry.Entity.StoragePath.StartsWith("supabase://", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Entity)
            .ToArray();
    }

    private static HttpRequestMessage CreateDeleteRequest(
        string supabaseUrl,
        string serviceRoleKey,
        string bucket,
        string objectKey)
    {
        var encodedBucket = Uri.EscapeDataString(bucket);
        var encodedObjectKey = Uri.EscapeDataString(objectKey)
            .Replace("%2F", "/", StringComparison.Ordinal);
        var deleteUrl = $"{supabaseUrl.TrimEnd('/')}/storage/v1/object/{encodedBucket}/{encodedObjectKey}";

        var request = new HttpRequestMessage(HttpMethod.Delete, deleteUrl);
        request.Headers.TryAddWithoutValidation("apikey", serviceRoleKey);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {serviceRoleKey}");
        return request;
    }

    internal static (string Bucket, string ObjectKey) ParseStoragePath(string storagePath)
    {
        const string prefix = "supabase://";
        if (!storagePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Document storage path is not a Supabase object path.");
        }

        var value = storagePath[prefix.Length..];
        var separator = value.IndexOf('/');
        if (separator <= 0 || separator == value.Length - 1)
        {
            throw new InvalidOperationException("Supabase document storage path is invalid.");
        }

        return (value[..separator], value[(separator + 1)..]);
    }

    private static void EnsureDeleteSucceeded(HttpResponseMessage response, string storagePath)
    {
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        var detail = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        throw new InvalidOperationException(
            $"Supabase failed to delete document object '{storagePath}' ({(int)response.StatusCode}): {detail}");
    }

    private static async Task EnsureDeleteSucceededAsync(
        HttpResponseMessage response,
        string storagePath,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"Supabase failed to delete document object '{storagePath}' ({(int)response.StatusCode}): {detail}");
    }
}
