using System.Diagnostics;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace RaceTrace.Api.Infrastructure.Telemetry;

public sealed class ApiTelemetryMiddleware(RequestDelegate next, IOptions<TelemetryOptions> options, ILogger<ApiTelemetryMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!options.Value.Enabled) { await next(context); return; }
        var started = Stopwatch.GetTimestamp();
        var original = context.Response.Body;
        long bytes = 0;
        using var counted = new CountingStream(original, count => bytes += count);
        context.Response.Body = counted;
        using var activity = ApiTelemetry.Activities.StartActivity("http.request", ActivityKind.Server);
        var failed = false;
        try { await next(context); }
        catch { failed = true; throw; }
        finally
        {
            context.Response.Body = original;
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
            var status = failed ? 500 : context.Response.StatusCode;
            var method = context.Request.Method is "GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE" or "OPTIONS" ? context.Request.Method : "OTHER";
            var tags = new TagList { { "http.route", route }, { "http.request.method", method }, { "http.response.status_code", status } };
            var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
            ApiTelemetry.HttpRequests.Add(1, tags);
            ApiTelemetry.HttpDuration.Record(elapsed, tags);
            ApiTelemetry.HttpResponseSize.Record(bytes, tags);
            if (status >= 500) ApiTelemetry.HttpErrors.Add(1, tags);
            if (status == 504 || context.Items.ContainsKey(ApiTelemetry.TimeoutKey)) ApiTelemetry.HttpTimeouts.Add(1, tags);
            ApiTelemetry.Cache("output", context.Items[ApiTelemetry.OutputCacheOutcomeKey] as string ?? "bypass");
            activity?.SetTag("http.route", route);
            activity?.SetTag("http.response.status_code", status);
            if (status >= 500) activity?.SetStatus(ActivityStatusCode.Error);
            if (options.Value.LogRequests)
                logger.LogInformation("API {Method} {Route} returned {StatusCode} in {DurationSeconds}s; uncompressed body {BodyBytes} bytes", method, route, status, elapsed, bytes);
        }
    }
}
