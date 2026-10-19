using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Configuration;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Infrastructure;

internal static class RequestSecurity
{
    internal static IServiceCollection AddRequestLimits(this IServiceCollection services)
    {
        services.AddHealthChecks();
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<RequestLimitOptions>>((options, configured) =>
        {
            var limits = configured.Value;
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(context =>
                {
                    if (!ApiAccessMiddleware.RequiresAuthorization(context.Request))
                        return RateLimitPartition.GetNoLimiter("non-api");
                    var admin = IsAdministrative(context.Request);
                    var address = context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown";
                    return RateLimitPartition.GetFixedWindowLimiter($"{admin}:{address}", _ => new()
                    {
                        PermitLimit = admin ? limits.AdminRequestsPerMinute : limits.RequestsPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    });
                }),
                PartitionedRateLimiter.Create<HttpContext, string>(context =>
                    IsHeavy(context.Request)
                        ? RateLimitPartition.GetConcurrencyLimiter("heavy", _ => new()
                        {
                            PermitLimit = limits.HeavyConcurrency, QueueLimit = 0
                        })
                        : RateLimitPartition.GetNoLimiter("light")));
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (rejected, token) =>
            {
                var retrySeconds = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry)
                    ? Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)) : 1;
                rejected.HttpContext.Response.Headers.RetryAfter = retrySeconds.ToString(CultureInfo.InvariantCulture);
                await rejected.HttpContext.Response.WriteAsJsonAsync(
                    new ApiErrorResponse(new ApiError("RATE_LIMITED", "Too many requests. Please try again shortly.")), token);
            };
        });
        return services;
    }

    private static bool IsAdministrative(HttpRequest request) =>
        request.Path.StartsWithSegments("/api/admin", StringComparison.OrdinalIgnoreCase) ||
        !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method);

    private static bool IsHeavy(HttpRequest request)
    {
        if (!request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)) return false;
        if (IsAdministrative(request)) return true;
        var path = request.Path.Value?.TrimEnd('/') ?? "";
        return request.Path.StartsWithSegments("/api/v1/ranking", StringComparison.OrdinalIgnoreCase) ||
            request.Path.StartsWithSegments("/api/v1/compare", StringComparison.OrdinalIgnoreCase) ||
            request.Path.StartsWithSegments("/api/v1/analytics", StringComparison.OrdinalIgnoreCase) ||
            request.Path.StartsWithSegments("/api/v1/podium-predictor", StringComparison.OrdinalIgnoreCase) ||
            request.Path.StartsWithSegments("/api/v1/seasons", StringComparison.OrdinalIgnoreCase) ||
            request.Path.StartsWithSegments("/api/v1/riders", StringComparison.OrdinalIgnoreCase) &&
                (path.EndsWith("/overview", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/metrics", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith("/venues", StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class ProductionConfigurationCheck(
    IHostEnvironment environment, IOptions<ApiAccessOptions> read, IOptions<AdminOptions> admin) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (environment.IsProduction())
        {
            var readSecret = read.Value.ReadSecret;
            var adminSecret = admin.Value.ApiSecret;
            if (string.IsNullOrWhiteSpace(readSecret) || readSecret.Length < 32 ||
                !string.IsNullOrWhiteSpace(adminSecret) && (adminSecret.Length < 32 || adminSecret == readSecret))
                throw new InvalidOperationException(
                    "Production requires an API read secret of at least 32 characters and a different admin secret of at least 32 characters when administration is configured.");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
