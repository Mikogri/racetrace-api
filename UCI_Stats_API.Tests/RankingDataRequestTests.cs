using System.Text.Json.Nodes;
using UCI_Stats_API.Infrastructure;
using UCI_Stats_API.Models;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Tests;

public sealed class RankingDataRequestTests
{
    [Fact]
    public async Task RiderDetailsKeepFullTransport()
    {
        var client = new CapturingSupabaseClient();
        var domain = new DomainQueryService(client);
        await domain.GetRiderDetailDataAsync(new RiderMetricsContext(null, "EM", null, null, null, null, null, null, null, null, null), "18");
        Assert.Equal("get_rider_detail_data", client.FunctionName);
        Assert.Equal(18, client.Payload!.Int("p_rider_id"));
    }

    [Fact]
    public async Task SlimPackagesCannotPoisonFullPackageCache()
    {
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var client = new CapturingSupabaseClient();
        var domain = new DomainQueryService(client, packageCache: cache);
        JsonObject[] sessions = [new() { ["id"] = 1 }];
        await domain.GetRankingPreaggregatesAsync(sessions, includeSplits: false);
        await domain.GetRankingPreaggregatesAsync(sessions);
        await domain.GetRankingPreaggregatesAsync(sessions, includeSplits: false);
        Assert.Equal(new[] { "get_ranking_metric_packages", "get_ranking_preaggregate_packages" }, client.PackageFunctions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PointsSummaryRequiresCompleteCache(bool complete)
    {
        var client = new CapturingSupabaseClient { PointsComplete = complete };
        var domain = new DomainQueryService(client);
        var rows = await domain.GetPointsSummaryAsync([new JsonObject { ["id"] = 1 }], default);
        if (!complete) { Assert.Null(rows); return; }
        var row = Assert.Single(rows!);
        Assert.Equal("A Rider", row.String("rider_name"));
        Assert.Equal(0m, row.Decimal("points"));
        Assert.Equal("2", row.String("rider_id"));
    }

    [Fact]
    public async Task OverlappingPackagesAreFetchedOnlyOnceAndRolesStaySeparate()
    {
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var client = new CapturingSupabaseClient();
        var domain = new DomainQueryService(client, packageCache: cache);
        JsonObject Session(int id) => new() { ["id"] = id };
        await domain.GetRankingPreaggregatesAsync([Session(1), Session(2)]);
        await domain.GetRankingPreaggregatesAsync([Session(2), Session(3)]);
        await domain.GetRankingPreaggregatesAsync([Session(1)]);
        Assert.Equal(new[] { "[1,2]", "[3]" }, client.PackageRequests);
        await domain.GetRankingPreaggregatesAsync([Session(1)], role: SupabaseRole.Service);
        Assert.Equal("[1]", client.PackageRequests.Last());
    }

    [Fact]
    public void PointsOnlyRowsPreserveTotalsAndTieBreaks()
    {
        JsonObject Row(int id, int rider, string name) => new()
        {
            ["id"] = id, ["rider_id"] = rider,
            ["riders"] = new JsonObject { ["first_name"] = name, ["last_name"] = "Rider" }
        };
        var data = new RankingDataBundle([], [Row(1, 9, "Z"), Row(2, 9, "Z"), Row(3, 2, "A"), Row(4, 3, "A"), Row(5, 4, "None")], [], [],
            new Dictionary<string, decimal> { ["1"] = 5, ["2"] = 5, ["3"] = 10, ["4"] = 10 });
        var rows = RankingService.BuildPointsRows(data);
        Assert.Equal(1, RankingService.PointsPosition(rows, "2"));
        Assert.Equal(2, RankingService.PointsPosition(rows, "3"));
        Assert.Equal(3, RankingService.PointsPosition(rows, "9"));
        Assert.Equal(4, RankingService.PointsPosition(rows, "4"));
        Assert.Null(RankingService.PointsPosition(rows, "99"));
    }

    [Fact]
    public async Task ExactRiderIdIsSentSeparatelyFromTextSearch()
    {
        var supabase = new CapturingSupabaseClient();
        var service = new DomainQueryService(supabase);
        var context = new RiderMetricsContext(
            null, null, null, null, null, null, null, null, null, null, null);

        await service.GetRankingDataAsync(context, riderId: "9");

        Assert.Equal("get_ranking_data", supabase.FunctionName);
        var payload = Assert.IsType<JsonObject>(supabase.Payload);
        Assert.Equal(9, payload.Int("p_rider_id"));
        Assert.Null(payload["p_rider_query"]);
    }

    private sealed class CapturingSupabaseClient : ISupabaseRestClient
    {
        public string? FunctionName { get; private set; }
        public JsonObject? Payload { get; private set; }
        public List<string> PackageRequests { get; } = [];
        public List<string> PackageFunctions { get; } = [];
        public bool PointsComplete { get; init; }

        public Task<JsonNode?> RpcAsync(
            string functionName,
            JsonObject payload,
            SupabaseRole role,
            CancellationToken cancellationToken = default)
        {
            FunctionName = functionName;
            Payload = payload;
            if (functionName == "get_ranking_points_summary")
                return Task.FromResult<JsonNode?>(new JsonObject
                {
                    ["complete"] = PointsComplete,
                    ["rows"] = new JsonArray(new JsonObject { ["rider_id"] = "2", ["points"] = 0,
                        ["rider"] = new JsonObject { ["first_name"] = "A", ["last_name"] = "Rider" } })
                });
            if (functionName is "get_ranking_preaggregate_packages" or "get_ranking_metric_packages")
            {
                PackageFunctions.Add(functionName);
                PackageRequests.Add(payload["p_session_ids"]!.ToJsonString());
                return Task.FromResult<JsonNode?>(new JsonArray(payload["p_session_ids"]!.AsArray()
                    .Select(id => (JsonNode)new JsonObject { ["session_id"] = id!.DeepClone(), ["payload"] = new JsonObject { ["v"] = 2 } }).ToArray()));
            }
            return Task.FromResult<JsonNode?>(new JsonObject
            {
                ["v"] = 1,
                ["s"] = new JsonArray(),
                ["r"] = new JsonArray(),
                ["f"] = new JsonArray(),
                ["x"] = new JsonArray(),
                ["d"] = new JsonArray(),
                ["t"] = new JsonArray()
            });
        }

        public Task<SupabaseQueryResult> GetAsync(
            SupabaseQuery query,
            bool count = false,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<JsonObject?> GetSingleAsync(
            SupabaseQuery query,
            SupabaseRole role = SupabaseRole.Anon,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<JsonObject>> PostAsync(
            string table,
            JsonNode payload,
            SupabaseRole role,
            string? onConflict = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<JsonObject>> PatchAsync(
            string table,
            JsonObject payload,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteAsync(
            string table,
            SupabaseQuery query,
            SupabaseRole role,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
