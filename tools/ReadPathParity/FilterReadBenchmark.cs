using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RaceTrace.Api.Configuration;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

internal static class FilterReadBenchmark
{
    internal static async Task Run(SupabaseOptions options)
    {
        using var http = new HttpClient();
        var client = new SupabaseRestClient(http, Options.Create(options));
        var samples = new List<object>();
        for (var sample = 0; sample < 5; sample++)
        {
            var timer = Stopwatch.StartNew();
            var seasons = client.GetAsync(
                new SupabaseQuery("rounds")
                    .Select("season_year")
                    .Order("season_year", true)
                    .Limit(1000)
            );
            var tracks = client.GetAsync(
                new SupabaseQuery("tracks").Select("*").Order("name", false).Limit(150),
                count: true
            );
            var conditions = client.GetAsync(
                new SupabaseQuery("conditions").Select("*").Order("name", false).Limit(100),
                count: true
            );
            var classes = client.GetAsync(
                new SupabaseQuery("competition_levels").Select("*").Order("priority", false)
            );
            await Task.WhenAll(seasons, tracks, conditions, classes);
            var oldMs = timer.Elapsed.TotalMilliseconds;
            var expected = new FilterOptionsDto
            {
                Seasons = (await seasons)
                    .Data.Select(r => r.Int("season_year"))
                    .OfType<int>()
                    .Distinct()
                    .OrderDescending()
                    .ToArray(),
                Tracks = (await tracks)
                    .Data.Select(r => new ContextTrackOptionDto(
                        long.Parse(r.String("id")!),
                        r.String("name"),
                        r.String("venue"),
                        r.String("country_code")
                    ))
                    .ToArray(),
                Conditions = (await conditions)
                    .Data.Select(r => new ContextNamedOptionDto(
                        long.Parse(r.String("id")!),
                        r.String("name")!
                    ))
                    .ToArray(),
                CompetitionClasses = (await classes)
                    .Data.Select(r => new ContextNamedOptionDto(
                        long.Parse(r.String("id")!),
                        r.String("name")!
                    ))
                    .ToArray(),
            };
            timer.Restart();
            var actual = await new FilterOptionsService(client).GetAsync(default);
            var newMs = timer.Elapsed.TotalMilliseconds;
            if (JsonSerializer.Serialize(expected) != JsonSerializer.Serialize(actual))
                throw new InvalidOperationException(
                    "Bundled filters changed dictionary values/order."
                );
            var result = new
            {
                sample,
                oldMs,
                newMs,
                oldCalls = 4,
                newCalls = 1,
                actual.Seasons.Count,
                tracks = actual.Tracks.Count,
                conditions = actual.Conditions.Count,
                classes = actual.CompetitionClasses.Count,
                bytes = JsonSerializer.SerializeToUtf8Bytes(actual).Length,
            };
            samples.Add(result);
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
        Directory.CreateDirectory("artifacts/feature-bundles");
        await File.WriteAllTextAsync(
            "artifacts/feature-bundles/filter-rpc.json",
            JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true })
        );
    }
}
