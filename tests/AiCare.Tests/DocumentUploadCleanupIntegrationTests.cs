using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using AiCare.Domain;
using AiCare.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AiCare.Tests;

public sealed class DocumentUploadCleanupIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedDocumentInsertRemovesSuccessfullyUploadedObject(bool useSupabase)
    {
        using var provider = new StorageProvider();
        var observer = new UploadObserver(provider);
        using var factory = new AiCareApiFactory();
        using var application = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Storage:Provider"] = useSupabase ? "Supabase" : "Local",
                    ["Supabase:Url"] = "https://storage.test.invalid",
                    ["Supabase:ServiceRoleKey"] = "cleanup-test-key",
                    ["Supabase:Bucket"] = "care-documents"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.AddDbContext<CareDbContext>(options => options.AddInterceptors(observer));
                // Every outbound request is handled in memory; no provider credentials or network are used.
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(provider);
            });
        });
        using var client = application.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            userName = "manager", password = "ManagerPassword123!", mfaCode = (string?)null
        });
        login.EnsureSuccessStatusCode();
        var token = await login.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetProperty("token").GetString());

        using (var scope = application.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CareDbContext>();
            // A real relational database failure, rather than a mocked SaveChanges exception.
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER fail_document_insert BEFORE INSERT ON "Documents"
                BEGIN SELECT RAISE(ABORT, 'forced_document_persistence_failure'); END;
                """);
        }

        var payload = Encoding.ASCII.GetBytes("%PDF-1.7\ncleanup integration document");
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(TestIds.ServiceUserId.ToString()), "serviceUserId");
        form.Add(new StringContent("Care plan"), "category");
        var file = new ByteArrayContent(payload);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "cleanup-regression.pdf");
        try
        {
            using var response = await client.PostAsync("/api/phase1/documents/upload", form);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var failure = Assert.IsType<DbUpdateException>(observer.PersistenceFailure);
            Assert.Contains("forced_document_persistence_failure", failure.ToString());
            Assert.DoesNotContain("forced_document_persistence_failure", await response.Content.ReadAsStringAsync());
            Assert.NotNull(observer.Document);
            Assert.Equal(payload, observer.UploadedBytes);
            if (useSupabase)
            {
                Assert.Equal(new[] { HttpMethod.Post, HttpMethod.Delete }, provider.Methods);
                Assert.Equal(provider.UploadUri, provider.DeleteUri);
                Assert.Empty(provider.Objects);
                Assert.StartsWith($"supabase://care-documents/{TenantDefaults.OrganizationId}/{TenantDefaults.BranchId}/", observer.Document!.StoragePath);
            }
            else
            {
                Assert.False(File.Exists(observer.Document!.StoragePath));
                Assert.Empty(provider.Methods);
            }

            using var verifyScope = application.Services.CreateScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<CareDbContext>();
            Assert.False(await verify.Documents.AnyAsync(item => item.Id == observer.Document!.Id));
            Assert.False(await verify.AuditEvents.AnyAsync(item => item.EntityId == observer.Document!.Id));
        }
        finally
        {
            // Only remove this test's exact upload if a regression left it behind.
            if (!useSupabase && observer.Document is not null && File.Exists(observer.Document.StoragePath))
                File.Delete(observer.Document.StoragePath);
        }
    }

    private sealed class UploadObserver(StorageProvider provider) : SaveChangesInterceptor
    {
        public DocumentItem? Document { get; private set; }
        public byte[]? UploadedBytes { get; private set; }
        public Exception? PersistenceFailure { get; private set; }

        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            PersistenceFailure = eventData.Exception;
            return Task.CompletedTask;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var document = eventData.Context!.ChangeTracker.Entries<DocumentItem>()
                .SingleOrDefault(entry => entry.State == EntityState.Added)?.Entity;
            if (document is not null)
            {
                Document = document;
                UploadedBytes = document.StoragePath.StartsWith("supabase://", StringComparison.Ordinal)
                    ? provider.Objects.Values.Single()
                    : File.ReadAllBytes(document.StoragePath);
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class StorageProvider : HttpMessageHandler, IHttpClientFactory
    {
        public Dictionary<string, byte[]> Objects { get; } = new();
        public List<HttpMethod> Methods { get; } = new();
        public Uri? UploadUri { get; private set; }
        public Uri? DeleteUri { get; private set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("storage.test.invalid", request.RequestUri!.Host);
            Assert.Equal("Bearer cleanup-test-key", request.Headers.Authorization?.ToString());
            Assert.Equal("cleanup-test-key", Assert.Single(request.Headers.GetValues("apikey")));
            Methods.Add(request.Method);
            var key = request.RequestUri.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("false", Assert.Single(request.Headers.GetValues("x-upsert")));
                UploadUri = request.RequestUri;
                Objects.Add(key, await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            }
            else
            {
                Assert.Equal(HttpMethod.Delete, request.Method);
                Assert.True(cancellationToken.CanBeCanceled);
                DeleteUri = request.RequestUri;
                Assert.True(Objects.Remove(key));
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
}
