using System.Net;
using AiCare.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AiCare.Tests;

public sealed class ProductionMonitoringTests : IClassFixture<AiCareApiFactory>
{
    private readonly AiCareApiFactory _factory;

    public ProductionMonitoringTests(AiCareApiFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData(500, 20, 2000, "http_5xx")]
    [InlineData(503, 20, 2000, "http_5xx")]
    [InlineData(429, 20, 2000, "rate_limit_triggered")]
    [InlineData(200, 2500, 2000, "slow_request")]
    public void MonitoringPolicyClassifiesActionableConditions(int statusCode, long elapsedMs, long thresholdMs, string expected)
    {
        Assert.Equal(expected, MonitoringPolicy.Classify(statusCode, elapsedMs, thresholdMs));
    }

    [Fact]
    public void HealthyFastRequestDoesNotCreateAlertClassification()
    {
        Assert.Null(MonitoringPolicy.Classify(200, 120, 2000));
    }

    [Theory]
    [InlineData(null, 2000)]
    [InlineData("100", 2000)]
    [InlineData("250", 250)]
    [InlineData("5000", 5000)]
    [InlineData("70000", 2000)]
    public void SlowRequestThresholdUsesSafeBounds(string? configuredValue, long expected)
    {
        var values = new Dictionary<string, string?>();
        if (configuredValue is not null) values["Monitoring:SlowRequestThresholdMs"] = configuredValue;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        Assert.Equal(expected, MonitoringPolicy.SlowRequestThreshold(configuration));
    }

    [Fact]
    public async Task LivenessEndpointIsAvailable()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/live");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("liveness", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("healthy", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadinessEndpointChecksDatabaseAndStorageWithoutExposingSecrets()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("readiness", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("database", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("storage", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password=", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ServiceRole", body, StringComparison.OrdinalIgnoreCase);
    }
}
