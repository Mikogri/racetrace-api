using System.Diagnostics;
using System.Text.Json;
using RaceTrace.Api.Infrastructure.Calculations;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.PodiumBacktest;

internal static class IncidentCacheAudit
{
    internal static async Task RunAsync(string[] args)
    {
        string Argument(string key, string fallback) => Array.IndexOf(args, key) is var i && i >= 0 ? args[i+1] : fallback;
        var input = Path.GetFullPath(Argument("--input", "artifacts/incident-multiseason"));
        var output = Path.GetFullPath(Argument("--output", "artifacts/stored-incidents"));
        Directory.CreateDirectory(output);
        var calculator = new DownhillPaceCalculator(AlgorithmConfiguration.Pace);
        var original = new List<PacePrecomputedSessionData>();
        for (var year = 2021; year <= 2026; year++)
        {
            var data = JsonSerializer.Deserialize<RiderDetailDataBundle>(await File.ReadAllTextAsync(Path.Combine(input, year.ToString(), $"inputs-{year}.json")))!;
            original.AddRange(data.PaceSessions.Select(s => s with { PaceFinish = null, PreparedAnomalies = null }));
        }
        var encoded = original.GroupBy(s => s.SessionId).ToDictionary(g => g.Key, g => IncidentAnnotationCache.Encode(g.ToArray()));
        var restored = original.Select(s =>
        {
            var annotations = IncidentAnnotationCache.Decode(IncidentAnnotationCache.Version, encoded[s.SessionId])![s.Run.RiderId];
            var full = IncidentAnnotationCache.Materialize(annotations, s.Sectors, AlgorithmConfiguration.Pace)
                ?? throw new InvalidOperationException("Could not restore cached normal diagnostics.");
            return s with { PreparedAnomalies = (s.Sectors, AlgorithmConfiguration.Pace, full) };
        }).ToArray();
        var baseline = calculator.PrepareField(original).Select(calculator.CalculatePrecomputedSession).ToArray();
        var cached = calculator.PrepareField(restored).Select(calculator.CalculatePrecomputedSession).ToArray();
        for (var i = 0; i < baseline.Length; i++)
            if (!JsonSerializer.SerializeToUtf8Bytes(baseline[i]).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(cached[i])))
                throw new InvalidOperationException($"Cached output changed: {original[i].SessionId}/{original[i].Run.RiderId}");
        var samples = new Dictionary<string, double[]>();
        foreach (var (name, rows) in new[] { ("dynamic", original.ToArray()), ("cached", restored) })
        {
            var durations = new double[5];
            for (var repeat = 0; repeat < durations.Length; repeat++)
            {
                var timer = Stopwatch.StartNew();
                foreach (var run in calculator.PrepareField(rows)) GC.KeepAlive(calculator.CalculatePrecomputedSession(run));
                durations[repeat] = timer.Elapsed.TotalMilliseconds;
            }
            samples[name] = durations;
        }
        var result = new { Runs = original.Count, Sessions = encoded.Count, FullSerializedOutputsEqual = true,
            EncodedCharacters = encoded.Values.Sum(v => v.Length), samples,
            TimingScope = "Prepared annotations already decoded/materialized; complete real read-path overhead is measured separately." };
        await File.WriteAllTextAsync(Path.Combine(output, "frozen-parity.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(result));
    }
}
