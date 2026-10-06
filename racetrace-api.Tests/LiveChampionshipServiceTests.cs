using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class LiveChampionshipServiceTests
{
    [Fact]
    public async Task LoadsOnlyEarlierSameSeriesRoundsAndUsesFinalBonusWithoutRequeryingBaseline()
    {
        var client = new Client();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new LiveChampionshipService(client, new DomainQueryService(client), cache);
        var context = await service.GetContextAsync("2", "EM", CancellationToken.None);
        Assert.True(context.Eligible);
        Assert.Equal(["final"], context.SessionTypes); // Qualifying disabled at season finale.
        var result = await service.ProjectAsync("2", "EM",
            [new("FINAL", true, [new("100", "A", 1, "finished"), new("200", "B", 2, "dnf")])], CancellationToken.None);
        Assert.Equal(750, result.Items[0].Points); // 500 previous + 250 finale, not regular 200.
        Assert.Equal(1, client.ResultsReads);
        Assert.False(result.CanChange);
    }

    [Theory]
    [InlineData("1", "world_cup")]
    [InlineData("2", "world_championship")]
    public async Task HiddenOutsideLastWorldCupRound(string roundId, string series)
    {
        var client = new Client(series);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var context = await new LiveChampionshipService(client, new DomainQueryService(client), cache)
            .GetContextAsync(roundId, "EM", CancellationToken.None);
        Assert.False(context.Eligible);
        Assert.Equal(0, client.ResultsReads);
    }

    private sealed class Client(string series = "world_cup") : ISupabaseRestClient
    {
        public int ResultsReads { get; private set; }
        public Task<JsonObject?> GetSingleAsync(SupabaseQuery query, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default) =>
            Task.FromResult<JsonObject?>(query.Table == "categories" ? JsonNode.Parse("{\"id\":4,\"code\":\"EM\"}")!.AsObject()
                : Round(query.Parameters.First(p => p.Key == "id").Value[3..]));
        private JsonObject Round(string id) => new()
        {
            ["id"] = id, ["season_year"] = 2026, ["round_number"] = int.Parse(id), ["start_date"] = $"2026-10-0{id}",
            ["competition_level_id"] = 2, ["competition_rank_id"] = 1, ["is_season_final_round"] = false,
            ["competition_levels"] = new JsonObject { ["code"] = series }
        };
        public Task<SupabaseQueryResult> GetAsync(SupabaseQuery query, bool count = false, SupabaseRole role = SupabaseRole.Anon, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<JsonObject> rows = query.Table switch
            {
                "rounds" => [Round("1"), Round("2")],
                "points_systems" => [JsonNode.Parse("""
                    {"id":1,"point_system_stages":[
                     {"session_type":"FINAL","awards_points":true,"awards_points_on_season_final_round":true,"point_rules":[{"position_from":1,"position_to":1,"points":200,"season_final_points":250}]},
                     {"session_type":"Q1","awards_points":true,"awards_points_on_season_final_round":false,"point_rules":[{"position_from":1,"position_to":1,"points":50}]}]}
                    """)!.AsObject()],
                "sessions" => Sessions(query),
                "results" => Results(query),
                _ => throw new NotSupportedException(query.Table)
            };
            return Task.FromResult(new SupabaseQueryResult(rows, rows.Count));
        }
        private static JsonObject[] Sessions(SupabaseQuery query)
        {
            Assert.Contains(query.Parameters, p => p.Key == "round_id" && p.Value == "in.(1)");
            Assert.Contains(query.Parameters, p => p.Key == "category_id" && p.Value == "eq.4");
            return [new() { ["id"] = "10", ["session_type"] = "FINAL" }, new() { ["id"] = "11", ["session_type"] = "TIMED_TRAINING" }];
        }
        private JsonObject[] Results(SupabaseQuery query)
        {
            ResultsReads++;
            Assert.Contains(query.Parameters, p => p.Key == "session_id" && p.Value == "in.(10)");
            return [JsonNode.Parse("""
                {"id":1,"rider_id":"1","riders":{"id":1,"uci_id":"100","first_name":"A","last_name":"Rider"},"result_points":[{"points_awarded":500}]}
                """)!.AsObject(), JsonNode.Parse("""
                {"id":2,"rider_id":"2","riders":{"id":2,"uci_id":"200","first_name":"B","last_name":"Rider"},"result_points":[{"points_awarded":100}]}
                """)!.AsObject()];
        }
        public Task<JsonNode?> RpcAsync(string functionName, JsonObject payload, SupabaseRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JsonObject>> PostAsync(string table, JsonNode payload, SupabaseRole role, string? onConflict = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JsonObject>> PatchAsync(string table, JsonObject payload, SupabaseQuery query, SupabaseRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string table, SupabaseQuery query, SupabaseRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
