using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ManhuaPipeline.Services;

public sealed class SessionAuthorizeFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var path = context.HttpContext.Request.Path.Value ?? "";
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        var userId = context.HttpContext.Session.GetInt32("UserId");
        if (userId is null or 0)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        await next();
    }
}
