using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using UCI_Stats_API.Configuration;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.PodiumBacktest;

// Replay the actual ranking service against identical read-only HTTP snapshots.
internal static class RankingIncidentComparison
{
    public static async Task RunAsync(string[] args)
    {
        string Argument(string key, string fallback) => Array.IndexOf(args, key) is var i && i >= 0 ? args[i + 1] : fallback;
        var api = Path.GetFullPath(Argument("--api", "UCI_Stats_API"));
        var output = Path.GetFullPath(Argument("--output", "artifacts/ranking-incident-comparison"));
        var name = Argument("--name", "before");
        var config = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(api, "appsettings.Development.json")))!;
        using var http = new HttpClient(new ReadOnlyCacheHandler(Path.Combine(output, "cache"), args.Contains("--offline")))
            { Timeout = TimeSpan.FromMinutes(3) };
        var client = new SupabaseRestClient(http, Options.Create(config["Supabase"]!.Deserialize<SupabaseOptions>()!));
        var domain = new DomainQueryService(client);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var ranking = new RankingService(new QueryValidationService(), domain,
            new MetricsService(domain, new PaceService(client)), new RankingSingleFlight(), cache);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        Directory.CreateDirectory(output);
        foreach (var year in new[] { 2026, 2025 })
        {
            var items = new JsonArray();
            for (var page = 1; ; page++)
            {
                var query = new QueryCollection(new Dictionary<string, StringValues>
                {
                    ["season"] = year.ToString(), ["category"] = "ME", ["sort"] = "rating",
                    ["order"] = "desc", ["pageSize"] = "150", ["page"] = page.ToString()
                });
                var response = JsonSerializer.SerializeToNode(await ranking.ListRankingAsync(query, CancellationToken.None), json)!;
                foreach (var item in response["items"]!.AsArray()) items.Add(item!.DeepClone());
                if (page >= response["totalPages"]!.GetValue<int>()) break;
            }
            await File.WriteAllTextAsync(Path.Combine(output, $"{name}-{year}.json"), items.ToJsonString(json));
            Console.WriteLine($"{name} {year}: {items.Count} riders; saved all metrics and ranking positions.");
        }
    }
}
