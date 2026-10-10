using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace RaceTrace.Api.Infrastructure.Telemetry;

public static class TelemetryServiceCollectionExtensions
{
    public static IServiceCollection AddRaceTraceTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Telemetry");
        var options = section.Get<TelemetryOptions>() ?? new();
        services.Configure<TelemetryOptions>(section);
        services.AddTransient<SupabaseTelemetryHandler>();
        ApiTelemetry.Enabled = options.Enabled;
        ApiTelemetry.DetailedEnabled = options.Enabled && options.Detailed;
        if (!options.Enabled) return services;
        if (options.TraceSampleRatio is < 0 or > 1 || double.IsNaN(options.TraceSampleRatio))
            throw new InvalidOperationException("Telemetry:TraceSampleRatio must be between 0 and 1.");
        Uri? endpoint = null;
        if (!string.IsNullOrWhiteSpace(options.OtlpEndpoint) &&
            (!Uri.TryCreate(options.OtlpEndpoint, UriKind.Absolute, out endpoint) || endpoint.Scheme is not ("http" or "https")))
            throw new InvalidOperationException("Telemetry:OtlpEndpoint must be an absolute HTTP(S) endpoint.");

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("racetrace-api"))
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(ApiTelemetry.Name)
                    .AddView("*.duration", new ExplicitBucketHistogramConfiguration
                    {
                        Boundaries = [0.001, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120]
                    });
                if (endpoint is not null) metrics.AddOtlpExporter(exporter => exporter.Endpoint = endpoint);
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(ApiTelemetry.Name)
                    .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(options.TraceSampleRatio)));
                if (endpoint is not null) tracing.AddOtlpExporter(exporter => exporter.Endpoint = endpoint);
            });
        return services;
    }
}
