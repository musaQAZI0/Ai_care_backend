using AiCare.Infrastructure;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace AiCare.Api;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class TransactionalActionAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var db = context.HttpContext.RequestServices.GetRequiredService<CareDbContext>();
        if (!db.Database.IsRelational())
        {
            await next();
            return;
        }

        var cancellationToken = context.HttpContext.RequestAborted;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var executed = await next();
            if (executed.Exception is null || executed.ExceptionHandled)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        });
    }
}
