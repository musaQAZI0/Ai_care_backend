using AiCare.Application;
using AiCare.Application.CarePlans;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AiCare.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<CareDbContext>(options =>
            options.UseNpgsql(connectionString));

        services.AddScoped<ICareRepository, EfCoreCareRepository>();
        services.AddScoped<ICarePlanLifecycleStore, CarePlanLifecycleStore>();
        services.AddScoped<ICarePlanLifecycleService, CarePlanLifecycleService>();
        return services;
    }
}
