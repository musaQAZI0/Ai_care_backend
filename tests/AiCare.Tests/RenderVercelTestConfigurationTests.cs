using AiCare.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AiCare.Tests;

public sealed class RenderVercelTestConfigurationTests
{
    [Fact]
    public void RenderProductionNormalizesStaleNonSecretValues()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RENDER"] = "true",
            ["Cors:AllowedOrigins:0"] = "http://localhost:5173",
            ["Cors:AllowedOrigins:1"] = "https://old.example.com",
            ["FamilyPortal:FrontendBaseUrl"] = "",
            ["Supabase:PublicFileBaseUrl"] = "https://public.example.com/files",
            ["Demo:Enabled"] = "true"
        }).Build();

        RenderVercelTestConfiguration.Normalize(configuration, "Production");

        Assert.Equal(RenderVercelTestConfiguration.FrontendBaseUrl, configuration["Cors:AllowedOrigins:0"]);
        Assert.Null(configuration["Cors:AllowedOrigins:1"]);
        Assert.Equal(RenderVercelTestConfiguration.FrontendBaseUrl, configuration["FamilyPortal:FrontendBaseUrl"]);
        Assert.Null(configuration["Supabase:PublicFileBaseUrl"]);
        Assert.Equal("false", configuration["Demo:Enabled"]);
    }

    [Fact]
    public void NonRenderProductionIsNotModified()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RENDER"] = "false",
            ["Cors:AllowedOrigins:0"] = "https://care.example.com",
            ["FamilyPortal:FrontendBaseUrl"] = "https://care.example.com",
            ["Demo:Enabled"] = "false"
        }).Build();

        RenderVercelTestConfiguration.Normalize(configuration, "Production");

        Assert.Equal("https://care.example.com", configuration["Cors:AllowedOrigins:0"]);
        Assert.Equal("https://care.example.com", configuration["FamilyPortal:FrontendBaseUrl"]);
    }
}
