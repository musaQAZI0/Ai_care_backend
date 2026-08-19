using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AiCare.Infrastructure;

public static class RenderVercelTestConfiguration
{
    public const string FrontendBaseUrl = "https://ai-care-frontend.vercel.app";

    public static void Normalize(IConfiguration configuration, string environmentName)
    {
        if (!string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(configuration["RENDER"], "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var child in configuration.GetSection("Cors:AllowedOrigins").GetChildren())
        {
            configuration[child.Path] = null;
        }

        configuration["Cors:AllowedOrigins:0"] = FrontendBaseUrl;
        configuration["FamilyPortal:FrontendBaseUrl"] = FrontendBaseUrl;
        configuration["Supabase:PublicFileBaseUrl"] = null;
        configuration["Demo:Enabled"] = "false";
    }
}
