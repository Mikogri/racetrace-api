using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RaceTrace.Api.Infrastructure.Telemetry;

public static class ApiTelemetry
{
    public const string Name = "RaceTrace.Api";
    public static readonly object OutputCacheOutcomeKey = new();
    public static readonly object TimeoutKey = new();
    public static readonly Meter Meter = new(Name);
    public static readonly ActivitySource Activities = new(Name);
    internal static readonly Counter<long> FlightCalls = Meter.CreateCounter<long>("racetrace.singleflight.calls");
    internal static readonly UpDownCounter<long> FlightWaiters = Meter.CreateUpDownCounter<long>("racetrace.singleflight.waiters");
    internal static readonly Histogram<double> FlightWaitDuration = Meter.CreateHistogram<double>("racetrace.singleflight.wait.duration", "s");
    internal static readonly Counter<long> HttpRequests = Meter.CreateCounter<long>("racetrace.http.requests");
    internal static readonly Counter<long> HttpErrors = Meter.CreateCounter<long>("racetrace.http.errors");
    internal static readonly Counter<long> HttpTimeouts = Meter.CreateCounter<long>("racetrace.http.timeouts");
    internal static readonly Histogram<double> HttpDuration = Meter.CreateHistogram<double>("racetrace.http.duration", "s");
    internal static readonly Histogram<long> HttpResponseSize = Meter.CreateHistogram<long>("racetrace.http.response.body.size", "By");
    internal static readonly Counter<long> SupabaseRequests = Meter.CreateCounter<long>("racetrace.supabase.requests");
    internal static readonly Histogram<double> SupabaseDuration = Meter.CreateHistogram<double>("racetrace.supabase.duration", "s");
    internal static readonly Counter<long> SupabaseBytes = Meter.CreateCounter<long>("racetrace.supabase.downloaded.bytes", "By");
    // No automatic transport retries are configured. Kept explicit, never inferred from paging or repeated calls.
    internal static readonly Counter<long> SupabaseRetries = Meter.CreateCounter<long>("racetrace.supabase.retries");
    private static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>("racetrace.operation.duration", "s");
    private static readonly Counter<long> CacheRequests = Meter.CreateCounter<long>("racetrace.cache.requests");
    private static readonly Counter<long> JobRuns = Meter.CreateCounter<long>("racetrace.job.runs");
    private static readonly Counter<long> JobRetries = Meter.CreateCounter<long>("racetrace.job.retries");
    private static readonly Histogram<double> JobDuration = Meter.CreateHistogram<double>("racetrace.job.duration", "s");
    private static readonly ConcurrentDictionary<string, long> LastSuccess = new();
    private static readonly ObservableGauge<long> JobLastSuccess = Meter.CreateObservableGauge(
        "racetrace.job.last_success", () => Enabled ? LastSuccess.Select(x => new Measurement<long>(x.Value, new KeyValuePair<string, object?>("job", x.Key))) : [], "s");
    internal static bool Enabled { get; set; } = true;
    internal static bool DetailedEnabled { get; set; } = true;

    public static OperationMeasurement Measure(string operation) => new(Operation(operation));

    public static void Cache(string cache, string outcome)
    {
        if (!DetailedEnabled) return;
        CacheRequests.Add(1, new("cache", cache is "memory" or "output" or "preaggregation" ? cache : "other"),
            new KeyValuePair<string, object?>("outcome", outcome is "hit" or "miss" or "bypass" or "unavailable" or "stale" or "fallback" ? outcome : "other"));
    }

    public static void Job(string job, string outcome, TimeSpan? duration = null)
    {
        if (!Enabled) return;
        job = JobName(job);
        outcome = outcome is "success" or "failure" or "lease_conflict" or "lease_loss" or "skipped" or "cancelled" ? outcome : "other";
        var tags = new TagList { { "job", job }, { "outcome", outcome } };
        JobRuns.Add(1, tags);
        if (duration is { } elapsed) JobDuration.Record(elapsed.TotalSeconds, tags);
        if (outcome == "success") LastSuccess[job] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    public static void JobRetry(string job)
    {
        if (Enabled) JobRetries.Add(1, new KeyValuePair<string, object?>("job", JobName(job)));
    }
    private static string JobName(string name) => name is "fantasy_sync" or "fantasy_prepare" or "point_system_seed" or "import" ? name : "other";
    private static string Operation(string name) => name is "pace" or "consistency" or "rating" or "podium" or "fantasy" or "round_analytics" or "preaggregation" or "preaggregation_rebuild" ? name : "other";

    public readonly struct OperationMeasurement : IDisposable
    {
        private readonly string _operation;
        private readonly long _started;
        private readonly Activity? _activity;
        internal OperationMeasurement(string operation)
        {
            _operation = operation;
            _started = DetailedEnabled && (OperationDuration.Enabled || Activities.HasListeners()) ? Stopwatch.GetTimestamp() : 0;
            _activity = _started == 0 ? null : Activities.StartActivity(operation);
        }
        public void Dispose()
        {
            if (_started == 0) return;
            OperationDuration.Record(Stopwatch.GetElapsedTime(_started).TotalSeconds, new KeyValuePair<string, object?>("operation", _operation));
            _activity?.Dispose();
        }
    }
}
