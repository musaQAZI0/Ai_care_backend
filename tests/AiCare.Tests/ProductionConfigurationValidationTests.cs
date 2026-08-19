using AiCare.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AiCare.Tests;

public sealed class ProductionConfigurationValidationTests
{
    [Fact]
    public void ValidProductionConfigurationPasses()
    {
        var configuration = BuildConfiguration();

        ProductionConfigurationValidator.Validate(configuration, "Production");
    }

    [Fact]
    public void DevelopmentConfigurationIsNotSubjectToProductionRules()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        ProductionConfigurationValidator.Validate(configuration, "Development");
    }

    [Fact]
    public void MissingRequiredProductionValuesFailWithoutLeakingSecrets()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Supabase:ServiceRoleKey"] = "",
            ["FamilyPortal:FrontendBaseUrl"] = "",
            ["Email:Password"] = ""
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("Supabase:ServiceRoleKey is required", exception.Message);
        Assert.Contains("FamilyPortal:FrontendBaseUrl is required", exception.Message);
        Assert.Contains("Email:Password is required", exception.Message);
        Assert.DoesNotContain(ValidSigningKey, exception.Message);
        Assert.DoesNotContain(ValidEmailPassword, exception.Message);
    }

    [Fact]
    public void WeakOrPlaceholderJwtSigningKeyFails()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["JwtOptions:SigningKey"] = "change-me"
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("at least 32 bytes", exception.Message);
        Assert.Contains("placeholder/default", exception.Message);
        Assert.DoesNotContain("change-me", exception.Message);
    }

    [Theory]
    [InlineData("http://ai-care-frontend.vercel.app")]
    [InlineData("http://localhost:5173")]
    [InlineData("https://localhost:5173")]
    public void InsecureOrLoopbackCorsOriginFails(string origin)
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Cors:AllowedOrigins:0"] = origin
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("Cors:AllowedOrigins", exception.Message);
    }

    [Theory]
    [InlineData("http://ai-care-frontend.vercel.app")]
    [InlineData("http://localhost:5173")]
    [InlineData("https://localhost:5173")]
    public void InsecureOrLoopbackFamilyPortalUrlFails(string url)
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["FamilyPortal:FrontendBaseUrl"] = url
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("FamilyPortal:FrontendBaseUrl", exception.Message);
    }

    [Fact]
    public void ProductionRequiresSupabaseStorage()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Local"
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("Storage:Provider must be Supabase", exception.Message);
    }

    [Fact]
    public void PublicDocumentBaseUrlCannotBeConfiguredInProduction()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Supabase:PublicFileBaseUrl"] = "https://project.supabase.co/storage/v1/object/public/care-documents"
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("short-lived signed URLs", exception.Message);
    }

    [Theory]
    [InlineData("false", "smtp.hostinger.com", "587", "true")]
    [InlineData("true", "", "587", "true")]
    [InlineData("true", "smtp.hostinger.com", "0", "true")]
    [InlineData("true", "smtp.hostinger.com", "587", "false")]
    public void InvalidProductionEmailConfigurationFails(string enabled, string host, string port, string enableSsl)
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Email:Enabled"] = enabled,
            ["Email:SmtpHost"] = host,
            ["Email:SmtpPort"] = port,
            ["Email:EnableSsl"] = enableSsl
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("Email:", exception.Message);
    }

    [Fact]
    public void DemoModeCannotBeEnabledInProduction()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Demo:Enabled"] = "true"
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("Demo:Enabled must be false", exception.Message);
    }

    private const string ValidSigningKey = "prod-test-signing-key-2026-very-long-and-random-value";
    private const string ValidEmailPassword = "smtp-test-only-secret-value";

    private static IConfiguration BuildConfiguration(Dictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=db.example.com;Database=aicare;Username=aicare;Password=test-only",
            ["JwtOptions:Issuer"] = "AiCare",
            ["JwtOptions:Audience"] = "AiCareClient",
            ["JwtOptions:SigningKey"] = ValidSigningKey,
            ["Storage:Provider"] = "Supabase",
            ["Supabase:Url"] = "https://project.supabase.co",
            ["Supabase:ServiceRoleKey"] = "service-role-test-only-value",
            ["Supabase:Bucket"] = "care-documents",
            ["Supabase:PublicFileBaseUrl"] = "",
            ["Cors:AllowedOrigins:0"] = "https://care.example.com",
            ["FamilyPortal:FrontendBaseUrl"] = "https://care.example.com",
            ["Email:Enabled"] = "true",
            ["Email:SmtpHost"] = "smtp.hostinger.com",
            ["Email:SmtpPort"] = "587",
            ["Email:Username"] = "no-reply@care.example.com",
            ["Email:Password"] = ValidEmailPassword,
            ["Email:FromAddress"] = "no-reply@care.example.com",
            ["Email:FromName"] = "AiCare",
            ["Email:EnableSsl"] = "true",
            ["Demo:Enabled"] = "false"
        };

        if (overrides is not null)
        {
            foreach (var item in overrides)
            {
                values[item.Key] = item.Value;
            }
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
