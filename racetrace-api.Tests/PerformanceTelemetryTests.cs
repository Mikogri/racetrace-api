using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Configuration;
using RaceTrace.Api.Features.Analytics;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Infrastructure.Telemetry;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

[CollectionDefinition("Telemetry", DisableParallelization = true)]
public sealed class TelemetryCollection;

[Collection("Telemetry")]
public sealed class PerformanceTelemetryTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task PreaggregationRebuildRefreshesInputsWithoutCalculatorPreparation()
    {
        using var measurements = new Measurements();
        using var http = new HttpClient(
            new ResponseFactoryHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]"),
            })
        );
        var database = new SupabaseRestClient(
            http,
            Options.Create(
                new SupabaseOptions { Url = "https://unused.invalid", ServiceRoleKey = "test" }
            )
        );
        Assert.False(
            await new RankingPreaggregationService(database).RefreshAsync(
                "1",
                CancellationToken.None
            )
        );
        Assert.Single(
            measurements.Values,
            item =>
                item.Name == "racetrace.operation.duration"
                && Equals(item.Tags.GetValueOrDefault("operation"), "preaggregation_rebuild")
        );
    }

    [Fact]
    public async Task CalculatorStagesEmitBoundedMeasurementsWithoutChangingEmptyResults()
    {
        using var measurements = new Measurements();
        using var http = new HttpClient(new FixedResponseHandler(new StringContent("[]")));
        var database = new SupabaseRestClient(
            http,
            Options.Create(
                new SupabaseOptions
                {
                    Url = "https://unused.invalid",
                    AnonKey = "test",
                    ServiceRoleKey = "test",
                }
            )
        );
        Assert.Empty(
            new DownhillPaceCalculator().CalculateSession(
                new PaceSessionData(PaceSessionType.Final, [])
            )
        );
        Assert.Null(new DownhillConsistencyCalculator().Calculate([]).Consistency);
        Assert.Empty(new PaceService(database).CalculatePrecomputed(Array.Empty<JsonObject>(), []));
        Assert.Empty(new MonteCarloRaceSimulator(new PodiumPredictionOptions()).Simulate([]));
        Assert.NotNull(RoundRaceAnalytics.Calculate([], [], [], [], null));
        Assert.False(
            await new RankingPreaggregationService(database).PrepareAsync(
                "1",
                CancellationToken.None
            )
        );
        foreach (
            var operation in new[]
            {
                "pace",
                "consistency",
                "rating",
                "podium",
                "round_analytics",
                "preaggregation",
            }
        )
            Assert.Single(
                measurements.Values,
                item =>
                    item.Name == "racetrace.operation.duration"
                    && Equals(item.Tags.GetValueOrDefault("operation"), operation)
            );
    }

    [Fact]
    public async Task DisabledTelemetryDoesNotEmitDetailedOrJobMeasurements()
    {
        using var measurements = new Measurements();
        var disabled = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["Telemetry:Enabled"] = "false" }
            )
            .Build();
        new ServiceCollection().AddRaceTraceTelemetry(disabled);
        try
        {
            using (ApiTelemetry.Measure("rating")) { }
            ApiTelemetry.Cache("memory", "hit");
            ApiTelemetry.Job("fantasy_sync", "success", TimeSpan.FromSeconds(1));
            ApiTelemetry.JobRetry("fantasy_sync");
            await new RankingSingleFlight().RunAsync(
                "test",
                () => Task.FromResult(new RankingSnapshot([], [], [], [], [])),
                CancellationToken.None
            );
            Assert.Empty(measurements.Values);
        }
        finally
        {
            new ServiceCollection().AddRaceTraceTelemetry(new ConfigurationBuilder().Build());
        }
    }

    [Fact]
    public async Task TelemetryHostStartsWithoutACollector()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddRaceTraceTelemetry(new ConfigurationBuilder().Build());
        await using var app = builder.Build();
        app.MapGet("/", () => "ok");
        await app.StartAsync();
        try
        {
            using var client = new HttpClient();
            Assert.Equal("ok", await client.GetStringAsync(app.Urls.Single()));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task RealOutputCacheHitIsMeasuredBeforeCompressionWithoutReexecutingEndpoint()
    {
        using var measurements = new Measurements();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddResponseCompression();
        builder.Services.Configure<TelemetryOptions>(_ => { });
        builder.Services.AddOutputCache(options =>
            options.AddPolicy("test", new ApiReadOutputCachePolicy(TimeSpan.FromMinutes(1)))
        );
        await using var app = builder.Build();
        app.UseRouting();
        app.UseResponseCompression();
        app.UseMiddleware<ApiTelemetryMiddleware>();
        app.UseOutputCache();
        var executed = 0;
        var body = new string('x', 4096);
        app.MapGet(
                "/cached/{id}",
                () =>
                {
                    Interlocked.Increment(ref executed);
                    return Results.Text(body);
                }
            )
            .CacheOutput("test");
        await app.StartAsync();
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip");
            for (var i = 0; i < 2; i++)
            {
                using var response = await client.GetAsync(app.Urls.Single() + "/cached/42");
                response.EnsureSuccessStatusCode();
                Assert.Contains("gzip", response.Content.Headers.ContentEncoding);
                Assert.True((await response.Content.ReadAsByteArrayAsync()).Length < 4096);
            }
            Assert.Equal(1, executed);
            Assert.Equal(2, measurements.Sum("racetrace.http.requests"));
            Assert.Equal(8192, measurements.Sum("racetrace.http.response.body.size"));
            Assert.Equal(1, measurements.Sum("racetrace.cache.requests", "outcome", "hit"));
            Assert.Equal(1, measurements.Sum("racetrace.cache.requests", "outcome", "miss"));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task CancellingOneSharedWaiterDoesNotCancelCalculationOrLeakWaiterCount()
    {
        using var measurements = new Measurements();
        var flight = new RankingSingleFlight();
        var release = new TaskCompletionSource<RankingSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var cancellation = new CancellationTokenSource();
        var first = flight.RunAsync("same", () => release.Task, CancellationToken.None);
        var cancelled = flight.RunAsync(
            "same",
            () => throw new Exception("must share"),
            cancellation.Token
        );
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(first.IsCompleted);
        release.SetResult(new RankingSnapshot([], [], [], [], []));
        await first;
        Assert.Equal(0, measurements.Sum("racetrace.singleflight.waiters"));
        Assert.Equal(1, measurements.Sum("racetrace.singleflight.calls", "outcome", "new"));
    }

    [Fact]
    public void OperationScopeOverheadProbe()
    {
        const int iterations = 100_000;
        static double Probe(int count)
        {
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < count; i++)
            {
                using var scope = ApiTelemetry.Measure("rating");
            }
            return watch.Elapsed.TotalNanoseconds / count;
        }
        Probe(10_000);
        var inactive = Probe(iterations);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Name == "racetrace.operation.duration")
                current.EnableMeasurementEvents(instrument);
        };
        var recorded = 0;
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => recorded++);
        listener.Start();
        var active = Probe(iterations);
        Assert.Equal(iterations, recorded);
        output.WriteLine(
            $"Operation scope probe ({iterations} iterations): inactive {inactive:F1} ns/op; listener active {active:F1} ns/op. No exporter/network; not a production benchmark."
        );
    }

    [Theory]
    [InlineData("hit")]
    [InlineData("miss")]
    [InlineData("bypass")]
    public async Task EndpointMetricsIncludeCachedBodiesAndOnlyRouteTemplates(string cacheOutcome)
    {
        using var measurements = new Measurements();
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/riders/secret-id";
        context.Request.QueryString = new QueryString("?secret=hidden");
        context.SetEndpoint(
            new RouteEndpoint(
                _ => Task.CompletedTask,
                RoutePatternFactory.Parse("/riders/{id}"),
                0,
                EndpointMetadataCollection.Empty,
                "rider"
            )
        );
        context.Response.Body = new MemoryStream();
        var middleware = new ApiTelemetryMiddleware(
            async ctx =>
            {
                ctx.Items[ApiTelemetry.OutputCacheOutcomeKey] = cacheOutcome;
                await ctx.Response.WriteAsync("ąbc");
            },
            Options.Create(new TelemetryOptions()),
            NullLogger<ApiTelemetryMiddleware>.Instance
        );
        await middleware.InvokeAsync(context);
        Assert.Equal(1, measurements.Sum("racetrace.http.requests"));
        Assert.Equal(4, measurements.Sum("racetrace.http.response.body.size"));
        Assert.Equal(1, measurements.Sum("racetrace.cache.requests", "outcome", cacheOutcome));
        Assert.Contains(
            measurements.Values,
            v => v.Tags.GetValueOrDefault("http.route") as string == "/riders/{id}"
        );
        Assert.DoesNotContain(
            measurements.Values.SelectMany(v => v.Tags.Values),
            v => v?.ToString()?.Contains("secret") == true
        );
    }

    [Fact]
    public async Task ConvertedUpstreamTimeoutCountsAsTimeoutAndError()
    {
        using var measurements = new Measurements();
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var errors = new ApiExceptionMiddleware(
            _ => throw new TaskCanceledException(),
            NullLogger<ApiExceptionMiddleware>.Instance
        );
        var middleware = new ApiTelemetryMiddleware(
            errors.InvokeAsync,
            Options.Create(new TelemetryOptions()),
            NullLogger<ApiTelemetryMiddleware>.Instance
        );
        await middleware.InvokeAsync(context);
        Assert.Equal(503, context.Response.StatusCode);
        Assert.Equal(1, measurements.Sum("racetrace.http.timeouts"));
        Assert.Equal(1, measurements.Sum("racetrace.http.errors"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhysicalSupabaseRequestsCountActualConsumedBytesWithoutEagerBuffering(
        bool streaming
    )
    {
        using var measurements = new Measurements();
        var content = new DeferredContent("[\"ą\"]");
        using var handler = new SupabaseTelemetryHandler
        {
            InnerHandler = new FixedResponseHandler(content),
        };
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync(
            "https://private.supabase.co/rest/v1/rpc/sensitive?token=hidden",
            streaming
                ? HttpCompletionOption.ResponseHeadersRead
                : HttpCompletionOption.ResponseContentRead
        );
        if (streaming)
            Assert.Equal(0, content.Reads);
        Assert.Equal("[\"ą\"]", await response.Content.ReadAsStringAsync());
        response.Dispose();
        Assert.Equal(1, measurements.Sum("racetrace.supabase.requests"));
        Assert.Equal(6, measurements.Sum("racetrace.supabase.downloaded.bytes"));
        Assert.Equal(0, measurements.Sum("racetrace.supabase.retries"));
        Assert.Single(measurements.Values, v => v.Name == "racetrace.supabase.duration");
        Assert.DoesNotContain(
            measurements.Values.SelectMany(v => v.Tags.Values),
            v =>
                v?.ToString()?.Contains("private") == true
                || v?.ToString()?.Contains("sensitive") == true
        );
    }

    [Fact]
    public void OperationAndCacheNamesAreBoundedAndJobsExposeLastSuccess()
    {
        using var measurements = new Measurements();
        using (ApiTelemetry.Measure("rating")) { }
        ApiTelemetry.Cache("memory", "hit");
        ApiTelemetry.Cache("private-key", "private-result");
        ApiTelemetry.Job("fantasy_sync", "success", TimeSpan.FromSeconds(2));
        measurements.Observe();
        Assert.Equal(1, measurements.Sum("racetrace.cache.requests", "outcome", "hit"));
        Assert.Equal(1, measurements.Sum("racetrace.cache.requests", "outcome", "other"));
        Assert.Equal(2, measurements.Sum("racetrace.job.duration"));
        Assert.True(measurements.Sum("racetrace.job.last_success") > 0);
        Assert.Single(measurements.Values, v => v.Name == "racetrace.operation.duration");
    }

    private sealed class FixedResponseHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    private sealed class ResponseFactoryHandler(
        Func<HttpRequestMessage, HttpResponseMessage> response
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(response(request));
    }

    private sealed class DeferredContent(string text) : HttpContent
    {
        public int Reads { get; private set; }

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context
        )
        {
            Reads++;
            await stream.WriteAsync(Encoding.UTF8.GetBytes(text));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Fact]
    public async Task TwentyIdenticalRequestsRecordOneCalculationAndNineteenSharedWaits()
    {
        using var measurements = new Measurements();
        var flight = new RankingSingleFlight();
        var release = new TaskCompletionSource<RankingSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var requests = Enumerable
            .Range(0, 20)
            .Select(_ =>
                flight.RunAsync("private-filter-key", () => release.Task, CancellationToken.None)
            )
            .ToArray();
        release.SetResult(new RankingSnapshot([], [], [], [], []));
        await Task.WhenAll(requests);

        Assert.Equal(1, measurements.Sum("racetrace.singleflight.calls", "outcome", "new"));
        Assert.Equal(19, measurements.Sum("racetrace.singleflight.calls", "outcome", "shared"));
        Assert.Equal(0, measurements.Sum("racetrace.singleflight.waiters"));
        Assert.Equal(
            19,
            measurements.Values.Count(v => v.Name == "racetrace.singleflight.wait.duration")
        );
        Assert.DoesNotContain(
            measurements.Values.SelectMany(v => v.Tags.Values),
            v => Equals(v, "private-filter-key")
        );
    }

    internal sealed class Measurements : IDisposable
    {
        private readonly MeterListener _listener = new();
        public ConcurrentQueue<(
            string Name,
            double Value,
            Dictionary<string, object?> Tags
        )> Values { get; } = new();

        public Measurements()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "RaceTrace.Api")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((i, v, t, _) => Record(i, v, t));
            _listener.SetMeasurementEventCallback<double>((i, v, t, _) => Record(i, v, t));
            _listener.Start();
        }

        private void Record(
            Instrument instrument,
            double value,
            ReadOnlySpan<KeyValuePair<string, object?>> tags
        ) =>
            Values.Enqueue(
                (instrument.Name, value, tags.ToArray().ToDictionary(k => k.Key, v => v.Value))
            );

        public double Sum(string name, string? tag = null, string? value = null) =>
            Values
                .Where(v =>
                    v.Name == name && (tag is null || Equals(v.Tags.GetValueOrDefault(tag), value))
                )
                .Sum(v => v.Value);

        public void Observe() => _listener.RecordObservableInstruments();

        public void Dispose() => _listener.Dispose();
    }
}
