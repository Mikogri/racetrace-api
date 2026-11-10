using Microsoft.AspNetCore.OutputCaching;
using RaceTrace.Api.Infrastructure.Calculations;
using RaceTrace.Api.Infrastructure.Telemetry;

namespace RaceTrace.Api.Infrastructure;

public sealed class ApiReadOutputCachePolicy : IOutputCachePolicy
{
    private readonly TimeSpan _duration;

    public ApiReadOutputCachePolicy(TimeSpan duration)
    {
        _duration = duration;
    }

    public async ValueTask CacheRequestAsync(
        OutputCacheContext context,
        CancellationToken cancellationToken
    )
    {
        var method = context.HttpContext.Request.Method;
        var cacheable = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        var refresh =
            context.HttpContext.Request.Path.StartsWithSegments("/api/v1/ranking")
            && context.HttpContext.Request.Query["refresh"] == "1";
        context.HttpContext.Items[ApiTelemetry.OutputCacheOutcomeKey] = cacheable
            ? "miss"
            : "bypass";
        context.EnableOutputCaching = cacheable;
        context.AllowCacheLookup = cacheable && !refresh;
        context.AllowCacheStorage = cacheable && !refresh;
        context.AllowLocking = true;
        context.CacheVaryByRules.QueryKeys = "*";
        var revisions = context.HttpContext.RequestServices?.GetService<DataRevisionService>();
        if (cacheable && revisions is { Enabled: true })
        {
            var snapshot = await revisions.GetAsync(cancellationToken);
            var scopes = RequestCalculationIdentity.Scopes(context.HttpContext);
            var revision = snapshot.ForScopes(scopes);
            context.CacheVaryByRules.VaryByValues["data-revision"] = revision;
            context.CacheVaryByRules.VaryByValues["algorithm"] =
                AlgorithmCatalog.Current.Fingerprint(
                    RequestCalculationIdentity.Algorithm(context.HttpContext)
                );
            context.HttpContext.Items["calculation-revision"] = revision;
            foreach (var scope in scopes)
                context.Tags.Add("data:" + scope);
        }
        context.ResponseExpirationTimeSpan = _duration;
    }

    public ValueTask ServeFromCacheAsync(
        OutputCacheContext context,
        CancellationToken cancellationToken
    )
    {
        context.HttpContext.Items[ApiTelemetry.OutputCacheOutcomeKey] = "hit";
        return ValueTask.CompletedTask;
    }

    public async ValueTask ServeResponseAsync(
        OutputCacheContext context,
        CancellationToken cancellationToken
    )
    {
        var response = context.HttpContext.Response;
        if (
            response.StatusCode != StatusCodes.Status200OK
            || response.Headers.ContainsKey("Set-Cookie")
        )
        {
            context.AllowCacheStorage = false;
        }

        var revisions = context.HttpContext.RequestServices?.GetService<DataRevisionService>();
        if (
            context.AllowCacheStorage
            && revisions is { Enabled: true }
            && context.HttpContext.Items["calculation-revision"] is string startedRevision
        )
        {
            var latest = await revisions.GetAsync(cancellationToken, forceRefresh: true);
            if (
                latest.ForScopes(RequestCalculationIdentity.Scopes(context.HttpContext))
                != startedRevision
            )
                context.AllowCacheStorage = false;
        }
    }
}
