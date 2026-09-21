using System.Net;
using System.Text;
using AiCare.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AiCare.Tests;

public sealed class MedicationTerminologyTests
{
    [Fact]
    public async Task SearchAsync_ReturnsOnlyDmdConcepts()
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Contains("/ValueSet/$expand", request.RequestUri!.AbsoluteUri);
            return Json(HttpStatusCode.OK, """
            {
              "resourceType":"ValueSet",
              "expansion":{
                "contains":[
                  {"system":"https://dmd.nhs.uk","code":"123","display":"Example medicine"},
                  {"system":"http://example.org/other","code":"999","display":"Other concept"}
                ]
              }
            }
            """);
        });
        var service = CreateService(handler);

        var results = await service.SearchAsync("example");

        var result = Assert.Single(results);
        Assert.Equal("123", result.Code);
        Assert.Equal("Example medicine", result.Display);
        Assert.Equal("https://dmd.nhs.uk", result.System);
    }

    [Fact]
    public async Task SearchAsync_ShortQuery_DoesNotCallNhs()
    {
        var called = false;
        var handler = new StubHandler((_, _) =>
        {
            called = true;
            return Json(HttpStatusCode.OK, "{}");
        });
        var service = CreateService(handler);

        var results = await service.SearchAsync("p");

        Assert.Empty(results);
        Assert.False(called);
    }

    [Fact]
    public async Task LookupAsync_ParsesDisplayInactiveAndProperties()
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Contains("CodeSystem/$lookup", request.RequestUri!.AbsoluteUri);
            Assert.Contains("code=123", request.RequestUri.AbsoluteUri);
            return Json(HttpStatusCode.OK, """
            {
              "resourceType":"Parameters",
              "parameter":[
                {"name":"display","valueString":"Example medicine 500mg tablet"},
                {"name":"inactive","valueBoolean":false},
                {"name":"property","part":[
                  {"name":"code","valueCode":"parent"},
                  {"name":"value","valueCode":"456"}
                ]}
              ]
            }
            """);
        });
        var service = CreateService(handler);

        var concept = await service.LookupAsync("123");

        Assert.NotNull(concept);
        Assert.Equal("123", concept.Code);
        Assert.Equal("Example medicine 500mg tablet", concept.Display);
        Assert.False(concept.Inactive);
        Assert.Equal("456", concept.Properties["parent"]);
    }

    [Fact]
    public async Task LookupAsync_NotFound_ReturnsNull()
    {
        var service = CreateService(new StubHandler((_, _) => Json(HttpStatusCode.NotFound, "{}")));

        var concept = await service.LookupAsync("missing");

        Assert.Null(concept);
    }

    [Fact]
    public async Task NhsFailure_ThrowsHttpRequestException()
    {
        var service = CreateService(new StubHandler((_, _) =>
            Json(HttpStatusCode.ServiceUnavailable, """{"issue":"temporarily unavailable"}""")));

        await Assert.ThrowsAsync<HttpRequestException>(() => service.SearchAsync("paracetamol"));
    }

    [Fact]
    public async Task ConfiguredCredentials_RequestOAuthTokenAndUseBearerToken()
    {
        var tokenRequested = false;
        var terminologyRequested = false;
        var handler = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("/token"))
            {
                tokenRequested = true;
                return Json(HttpStatusCode.OK, """{"access_token":"test-token","expires_in":300}""");
            }

            terminologyRequested = true;
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
            return Json(HttpStatusCode.OK, """{"resourceType":"ValueSet","expansion":{"contains":[]}}""");
        });
        var service = CreateService(handler, new Dictionary<string, string?>
        {
            ["MedicationTerminology:ClientId"] = "client-id",
            ["MedicationTerminology:ClientSecret"] = "client-secret",
            ["MedicationTerminology:TokenUrl"] = "https://auth.test/token"
        });

        await service.SearchAsync("paracetamol");

        Assert.True(tokenRequested);
        Assert.True(terminologyRequested);
    }

    private static NhsDmdTerminologyService CreateService(
        HttpMessageHandler handler,
        IDictionary<string, string?>? settings = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["MedicationTerminology:BaseUrl"] = "https://terminology.test/fhir"
        };
        if (settings is not null)
            foreach (var pair in settings) values[pair.Key] = pair.Value;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        return new NhsDmdTerminologyService(new HttpClient(handler), configuration);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/fhir+json") };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request, cancellationToken));
    }
}
