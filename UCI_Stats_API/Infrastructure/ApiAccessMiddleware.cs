using UCI_Stats_API.Services;

namespace UCI_Stats_API.Infrastructure;

public sealed class ApiAccessMiddleware
{
    private readonly RequestDelegate _next;

    public ApiAccessMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ApiAccessGuard guard)
    {
        if (RequiresAuthorization(context.Request) &&
            !HttpMethods.IsOptions(context.Request.Method))
        {
            guard.EnsureAuthorized(context.Request);
        }

        await _next(context);
    }

    internal static bool RequiresAuthorization(HttpRequest request)
    {
        return request.Path.StartsWithSegments(
            "/api",
            StringComparison.OrdinalIgnoreCase) ||
            request.Path.StartsWithSegments(
                "/openapi",
                StringComparison.OrdinalIgnoreCase);
    }
}
