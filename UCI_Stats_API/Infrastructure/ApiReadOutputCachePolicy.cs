using Microsoft.AspNetCore.OutputCaching;

namespace UCI_Stats_API.Infrastructure;

public sealed class ApiReadOutputCachePolicy : IOutputCachePolicy
{
    private readonly TimeSpan _duration;

    public ApiReadOutputCachePolicy(TimeSpan duration)
    {
        _duration = duration;
    }

    public ValueTask CacheRequestAsync(
        OutputCacheContext context,
        CancellationToken cancellationToken)
    {
        var method = context.HttpContext.Request.Method;
        var cacheable = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        context.EnableOutputCaching = cacheable;
        context.AllowCacheLookup = cacheable;
        context.AllowCacheStorage = cacheable;
        context.AllowLocking = true;
        context.CacheVaryByRules.QueryKeys = "*";
        context.ResponseExpirationTimeSpan = _duration;
        return ValueTask.CompletedTask;
    }

    public ValueTask ServeFromCacheAsync(
        OutputCacheContext context,
        CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask ServeResponseAsync(
        OutputCacheContext context,
        CancellationToken cancellationToken)
    {
        var response = context.HttpContext.Response;
        if (response.StatusCode != StatusCodes.Status200OK ||
            response.Headers.ContainsKey("Set-Cookie"))
        {
            context.AllowCacheStorage = false;
        }

        return ValueTask.CompletedTask;
    }
}
