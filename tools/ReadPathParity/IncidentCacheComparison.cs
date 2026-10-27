using System.Diagnostics;
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
        string Argument(string key, string fallback) => Array.IndexOf(args, key) is var i && i >= 0 ? args[i+1] : fallback;
        var output = Path.GetFullPath(Argument("--output", "artifacts/stored-incidents"));
        Directory.CreateDirectory(output);
        if (args.Contains("--prepare-incidents"))
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(110) };
            var client = new SupabaseRestClient(http, Options.Create(options));
            var targets = new List<string>();
            for (var offset = 0; ; offset += 100)
            {
                var page = await client.GetAsync(new SupabaseQuery("ranking_session_preaggregates")
                    .Select("session_id,incident_version").Order("session_id", true).Offset(offset).Limit(100), role: SupabaseRole.Service);
                targets.AddRange(page.Data.Where(r => r.String("incident_version") != RankingPreaggregationService.IncidentVersion)
                    .Select(r => r.String("session_id")!));
                if (page.Data.Count < 100) break;
            }
            var service = new RankingPreaggregationService(client);
            var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
            var completed = 0;
            var timer = Stopwatch.StartNew();
            await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = 2 }, async (id, token) =>
            {
                if (!await service.PrepareIncidentsAsync(id, token)) failures.Add(id);
                var count = Interlocked.Increment(ref completed);
                if (count % 25 == 0 || count == targets.Count) Console.WriteLine($"Annotations {count}/{targets.Count}; conflicts={failures.Count}");
            });
            var result = new { Targets = targets.Count, Failed = failures.ToArray(), Version = RankingPreaggregationService.IncidentVersion,
                Milliseconds = timer.Elapsed.TotalMilliseconds };
            await File.WriteAllTextAsync(Path.Combine(output, "backfill.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(result));
            if (!failures.IsEmpty) throw new InvalidOperationException("Incomplete incident annotation backfill.");
            return;
        }
        var phase = Argument("--phase", "before");
        if (phase is not ("before" or "after")) throw new ArgumentException("phase must be before/after");
        var cases = new[]
        {
            ("ranking-2026", (string?)null, "?season=2026&category=elite_men&sort=rating&order=desc&pageSize=30&comparison=previousRound"),
            ("ranking-all", (string?)null, "?category=elite_men&sort=rating&order=desc&pageSize=30&comparison=previousRound"),
            ("rider-2026", "18", "?season=2026&category=elite_men"),
            ("rider-all", "18", "?category=elite_men")
        };
        var results = new List<object>();
        foreach (var (name, riderId, query) in cases)
        for (var repeat = 0; repeat < 5; repeat++)
        {
            using var handler = new MeasuredHandler();
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(110) };
            var client = new SupabaseRestClient(http, Options.Create(options));
            using var memory = new MemoryCache(new MemoryCacheOptions());
            var domain = new DomainQueryService(client, packageCache: memory,
                readOptions: Options.Create(new ReadOptimizationOptions { TypedRanking = true, PreparedRiderDetails = true }));
            var validator = new QueryValidationService();
            var pace = new PaceService(client);
            var metrics = new MetricsService(domain, pace);
            var ranking = new RankingService(validator, domain, metrics, new RankingSingleFlight(), memory);
            var riders = new RidersService(client, validator, domain, metrics, ranking,
                new AnalyticsService(validator, domain, pace, TimeProvider.System));
            var filter = new QueryCollection(QueryHelpers.ParseQuery(query));
            var timer = Stopwatch.StartNew();
            object response = riderId is null ? await ranking.ListRankingAsync(filter, default)
                : await riders.GetOverviewAsync(riderId, filter, default);
            var elapsed = timer.Elapsed.TotalMilliseconds;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var responseFile = Path.Combine(output, $"{name}-{phase}.json");
            if (repeat == 0) await File.WriteAllBytesAsync(responseFile, bytes);
            else if (!(await File.ReadAllBytesAsync(responseFile)).AsSpan().SequenceEqual(bytes))
                throw new InvalidOperationException($"Unstable response during {phase}/{name}");
            if (phase == "after" && !(await File.ReadAllBytesAsync(Path.Combine(output, $"{name}-before.json"))).AsSpan().SequenceEqual(bytes))
                throw new InvalidOperationException($"Metric/contract mismatch for {name}");
            results.Add(new { name, phase, repeat, totalMs = elapsed, networkMs = handler.NetworkMs,
                apiOtherMs = elapsed - handler.NetworkMs, calls = handler.Calls, inputBytes = handler.Bytes, responseBytes = bytes.Length, hash });
            await File.WriteAllTextAsync(Path.Combine(output, phase + ".json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{phase}/{name}/{repeat}: total={elapsed:F1} ms network={handler.NetworkMs:F1} ms calls={handler.Calls} input={handler.Bytes}");
        }
    }

    private sealed class MeasuredHandler() : DelegatingHandler(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    })
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
                    long total = 0, start = 0, end = 0;
                    foreach (var interval in _intervals.OrderBy(i => i.Start))
                    {
                        if (interval.Start > end) { total += end - start; start = interval.Start; end = interval.End; }
                        else end = Math.Max(end, interval.End);
                    }
                    return (total + end - start) * 1000d / Stopwatch.Frequency;
                }
            }
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Get && !request.RequestUri!.AbsolutePath.Contains("/rpc/get_"))
                throw new InvalidOperationException("Benchmark writes are not allowed.");
            var start = Stopwatch.GetTimestamp();
            var response = await base.SendAsync(request, token);
            var body = await response.Content.ReadAsByteArrayAsync(token);
            var end = Stopwatch.GetTimestamp();
            lock (_intervals) _intervals.Add((start, end));
            Interlocked.Add(ref _bytes, body.Length);
            Interlocked.Increment(ref _calls);
            var previous = response.Content;
            response.Content = new ByteArrayContent(body);
            foreach (var header in previous.Headers) response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            previous.Dispose();
            return response;
        }
    }
}
