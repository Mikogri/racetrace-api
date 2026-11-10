using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Configuration;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Services;

static class IncidentCacheComparison
{
    internal static async Task Run(SupabaseOptions options, string[] args)
    {
        string Argument(string key, string fallback) =>
            Array.IndexOf(args, key) is var i && i >= 0 ? args[i + 1] : fallback;
        var output = Path.GetFullPath(Argument("--output", "artifacts/stored-incidents"));
        Directory.CreateDirectory(output);
        if (args.Contains("--prepare-incidents"))
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(110) };
            var client = new SupabaseRestClient(http, Options.Create(options));
            var targets = new List<string>();
            for (var offset = 0; ; offset += 100)
            {
                var page = await client.GetAsync(
                    new SupabaseQuery("ranking_session_preaggregates")
                        .Select("session_id,incident_version")
                        .Order("session_id", true)
                        .Offset(offset)
                        .Limit(100),
                    role: SupabaseRole.Service
                );
                targets.AddRange(
                    page.Data.Where(r =>
                            r.String("incident_version")
                            != RankingPreaggregationService.IncidentVersion
                        )
                        .Select(r => r.String("session_id")!)
                );
                if (page.Data.Count < 100)
                    break;
            }
            var service = new RankingPreaggregationService(client);
            var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
            var completed = 0;
            var timer = Stopwatch.StartNew();
            await Parallel.ForEachAsync(
                targets,
                new ParallelOptions { MaxDegreeOfParallelism = 2 },
                async (id, token) =>
                {
                    if (!await service.PrepareIncidentsAsync(id, token))
                        failures.Add(id);
                    var count = Interlocked.Increment(ref completed);
                    if (count % 25 == 0 || count == targets.Count)
                        Console.WriteLine(
                            $"Annotations {count}/{targets.Count}; conflicts={failures.Count}"
                        );
                }
            );
            var result = new
            {
                Targets = targets.Count,
                Failed = failures.ToArray(),
                Version = RankingPreaggregationService.IncidentVersion,
                Milliseconds = timer.Elapsed.TotalMilliseconds,
            };
            await File.WriteAllTextAsync(
                Path.Combine(output, "backfill.json"),
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
            );
            Console.WriteLine(JsonSerializer.Serialize(result));
            if (!failures.IsEmpty)
                throw new InvalidOperationException("Incomplete incident annotation backfill.");
            return;
        }
        var phase = Argument("--phase", "before");
        if (phase is not ("before" or "after"))
            throw new ArgumentException("phase must be before/after");
        var cases = args.Contains("--features")
            ? new[]
            {
                (
                    "compare-2026",
                    (string?)"compare",
                    "?season=2026&category=elite_men&riderIds=18,183"
                ),
                ("compare-all", (string?)"compare", "?category=elite_men&riderIds=18,183"),
                (
                    "compare-asymmetric",
                    (string?)"compare",
                    "?season=2026&category=elite_men&riderIds=18,8"
                ),
                ("home-2026", (string?)"home", "?season=2026&category=elite_men"),
            }
            : new[]
            {
                (
                    "ranking-2026",
                    (string?)null,
                    "?season=2026&category=elite_men&sort=rating&order=desc&pageSize=30&comparison=previousRound"
                ),
                (
                    "ranking-all",
                    (string?)null,
                    "?category=elite_men&sort=rating&order=desc&pageSize=30&comparison=previousRound"
                ),
                ("rider-2026", "18", "?season=2026&category=elite_men"),
                ("rider-all", "18", "?category=elite_men"),
            };
        var results = new List<object>();
        using var warmMemory = args.Contains("--warm")
            ? new MemoryCache(new MemoryCacheOptions())
            : null;
        foreach (
            var (name, riderId, query) in cases.Where(item =>
                (!args.Contains("--home-only") || item.Item2 == "home")
                && (!args.Contains("--asymmetric-only") || item.Item1 == "compare-asymmetric")
            )
        )
            for (var repeat = 0; repeat < 5; repeat++)
            {
                var operationMs = new Dictionary<string, double>();
                using var listener = new MeterListener();
                listener.InstrumentPublished = (instrument, meter) =>
                {
                    if (instrument.Name == "racetrace.operation.duration")
                        meter.EnableMeasurementEvents(instrument);
                };
                listener.SetMeasurementEventCallback<double>(
                    (instrument, value, tags, state) =>
                    {
                        foreach (var tag in tags)
                            if (tag.Key == "operation" && tag.Value is string name)
                                lock (operationMs)
                                    operationMs[name] =
                                        operationMs.GetValueOrDefault(name) + value * 1000;
                    }
                );
                listener.Start();
                using var handler = new MeasuredHandler();
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(110) };
                var client = new SupabaseRestClient(http, Options.Create(options));
                using var sampleMemory = new MemoryCache(new MemoryCacheOptions());
                var memory = warmMemory ?? sampleMemory;
                var domain = new DomainQueryService(
                    client,
                    packageCache: memory,
                    readOptions: Options.Create(
                        new ReadOptimizationOptions
                        {
                            TypedRanking = true,
                            PreparedRiderDetails = true,
                        }
                    )
                );
                var validator = new QueryValidationService();
                var pace = new PaceService(client);
                var metrics = new MetricsService(domain, pace);
                var ranking = new RankingService(
                    validator,
                    domain,
                    metrics,
                    new RankingSingleFlight(),
                    memory
                );
                var riders = new RidersService(
                    client,
                    validator,
                    domain,
                    metrics,
                    ranking,
                    new AnalyticsService(validator, domain, pace, TimeProvider.System)
                );
                var filter = new QueryCollection(QueryHelpers.ParseQuery(query));
                var timer = Stopwatch.StartNew();
                object response;
                if (riderId == "compare")
                    response = await new CompareService(
                        validator,
                        domain,
                        metrics,
                        ranking,
                        phase == "after"
                            ? new CompareInputPackageReader(
                                client,
                                Options.Create(
                                    new ReadOptimizationOptions { PreparedRiderDetails = true }
                                )
                            )
                            : null
                    ).CompareAsync(filter, default);
                else if (riderId == "home")
                {
                    async Task<object> HomeList(string sort)
                    {
                        var homeQuery = new QueryCollection(
                            QueryHelpers.ParseQuery(
                                query + "&page=1&pageSize=5&sortDirection=desc&sortBy=" + sort
                            )
                        );
                        return await ranking.ListRankingAsync(homeQuery, default);
                    }
                    response =
                        phase == "after"
                            ? await new HomeRankingService(ranking).GetAsync(filter, default)
                            : new
                            {
                                TopRated = await HomeList("rating"),
                                Standings = await HomeList("totalPoints"),
                            };
                }
                else
                    response = riderId is null
                        ? await ranking.ListRankingAsync(filter, default)
                        : await riders.GetOverviewAsync(riderId, filter, default);
                var elapsed = timer.Elapsed.TotalMilliseconds;
                var serialization = Stopwatch.StartNew();
                var bytes = JsonSerializer.SerializeToUtf8Bytes(
                    response,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                );
                serialization.Stop();
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (riderId is not null && args.Contains("--allow-cumulative-rating"))
                {
                    var overview = JsonNode.Parse(bytes)!;
                    var history =
                        overview["chartStatistics"]?["cumulativeRatingByRound"] as JsonObject;
                    if (
                        history is { Count: > 0 }
                        && !JsonNode.DeepEquals(
                            history.Last().Value,
                            overview["statistics"]?["rating"]
                        )
                    )
                        throw new InvalidOperationException(
                            "Latest cumulative rating differs from the current rider rating."
                        );
                }
                var responseFile = Path.Combine(output, $"{name}-{phase}.json");
                if (repeat == 0)
                    await File.WriteAllBytesAsync(responseFile, bytes);
                else if (
                    !(await File.ReadAllBytesAsync(responseFile)).AsSpan().SequenceEqual(bytes)
                )
                    throw new InvalidOperationException($"Unstable response during {phase}/{name}");
                if (phase == "after")
                {
                    var previous = JsonNode.Parse(
                        await File.ReadAllBytesAsync(Path.Combine(output, $"{name}-before.json"))
                    )!;
                    var current = JsonNode.Parse(bytes)!;
                    if (args.Contains("--allow-cumulative-rating"))
                    {
                        (previous["chartStatistics"] as JsonObject)?.Remove(
                            "cumulativeRatingByRound"
                        );
                        (current["chartStatistics"] as JsonObject)?.Remove(
                            "cumulativeRatingByRound"
                        );
                    }
                    if (!JsonNode.DeepEquals(previous, current))
                        throw new InvalidOperationException($"Metric/contract mismatch for {name}");
                }
                results.Add(
                    new
                    {
                        name,
                        phase,
                        repeat,
                        totalMs = elapsed,
                        networkMs = handler.NetworkMs,
                        apiOtherMs = elapsed - handler.NetworkMs,
                        serializationMs = serialization.Elapsed.TotalMilliseconds,
                        operationMs,
                        calls = handler.Calls,
                        inputBytes = handler.Bytes,
                        responseBytes = bytes.Length,
                        hash,
                    }
                );
                await File.WriteAllTextAsync(
                    Path.Combine(output, phase + ".json"),
                    JsonSerializer.Serialize(
                        results,
                        new JsonSerializerOptions { WriteIndented = true }
                    )
                );
                Console.WriteLine(
                    $"{phase}/{name}/{repeat}: total={elapsed:F1} ms network={handler.NetworkMs:F1} ms calls={handler.Calls} input={handler.Bytes}"
                );
            }
    }

    private sealed class MeasuredHandler()
        : DelegatingHandler(
            new SocketsHttpHandler
            {
                AutomaticDecompression =
                    DecompressionMethods.GZip
                    | DecompressionMethods.Deflate
                    | DecompressionMethods.Brotli,
            }
        )
    {
        private readonly List<(long Start, long End)> _intervals = [];
        private long _bytes;
        private int _calls;
        internal long Bytes => Interlocked.Read(ref _bytes);
        internal int Calls => Volatile.Read(ref _calls);
        internal double NetworkMs
        {
            get
            {
                lock (_intervals)
                {
                    long total = 0,
                        start = 0,
                        end = 0;
                    foreach (var interval in _intervals.OrderBy(i => i.Start))
                    {
                        if (interval.Start > end)
                        {
                            total += end - start;
                            start = interval.Start;
                            end = interval.End;
                        }
                        else
                            end = Math.Max(end, interval.End);
                    }
                    return (total + end - start) * 1000d / Stopwatch.Frequency;
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken token
        )
        {
            if (
                request.Method != HttpMethod.Get
                && !request.RequestUri!.AbsolutePath.Contains("/rpc/get_")
            )
                throw new InvalidOperationException("Benchmark writes are not allowed.");
            var start = Stopwatch.GetTimestamp();
            var response = await base.SendAsync(request, token);
            var body = await response.Content.ReadAsByteArrayAsync(token);
            var end = Stopwatch.GetTimestamp();
            lock (_intervals)
                _intervals.Add((start, end));
            Interlocked.Add(ref _bytes, body.Length);
            Interlocked.Increment(ref _calls);
            var previous = response.Content;
            response.Content = new ByteArrayContent(body);
            foreach (var header in previous.Headers)
                response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            previous.Dispose();
            return response;
        }
    }
}
