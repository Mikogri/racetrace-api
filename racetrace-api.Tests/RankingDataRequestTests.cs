using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class RankingDataRequestTests
{
    private static RiderMetricsContext EmptyContext => new(null, null, null, null, null, null, null, null, null, null, null);
    [Fact]
    public async Task TeamHistoryIsSharedWithinRequestButReturnedJsonIsIndependent()
    {
        var client = new CapturingSupabaseClient();
        var domain = new DomainQueryService(client);
        var first = domain.GetRiderTeamHistoryAsync(["18"]);
        var second = domain.GetRiderTeamHistoryAsync(["18"]);
        await Task.WhenAll(first, second);
        Assert.Equal(1, client.HistoryRequests);
        (await first)[0]["team_id"] = 999;
        Assert.Equal(8, (await second)[0].Int("team_id"));
        Assert.Equal(8, (await domain.GetRiderTeamHistoryAsync(["18"]))[0].Int("team_id"));
        await domain.GetRiderTeamHistoryAsync(["18"], fromDate: new DateOnly(2025, 1, 1));
        await domain.GetRiderTeamHistoryAsync(["18"], toDate: new DateOnly(2026, 1, 1));
        await domain.GetRiderTeamHistoryAsync(["19"]);
        Assert.Equal(4, client.HistoryRequests);
        // Imports can update history mid-request. Do not memoize privileged reads.
        await domain.GetRiderTeamHistoryAsync(["18"], role: SupabaseRole.Service);
        await domain.GetRiderTeamHistoryAsync(["18"], role: SupabaseRole.Service);
        Assert.Equal(6, client.HistoryRequests);
        await new DomainQueryService(client).GetRiderTeamHistoryAsync(["18"]);
        Assert.Equal(7, client.HistoryRequests);
    }

    [Fact]
    public async Task FailedHistoryReadCanBeRetried()
    {
        var client = new CapturingSupabaseClient { FailNextHistoryRead = true };
        var domain = new DomainQueryService(client);
        await Assert.ThrowsAsync<HttpRequestException>(() => domain.GetRiderTeamHistoryAsync(["18"]));
        Assert.Single(await domain.GetRiderTeamHistoryAsync(["18"]));
        Assert.Equal(2, client.HistoryRequests);
    }

    [Fact]
    public async Task ConcurrentHistoryReadersSharePendingReadAndOneWaiterCanCancel()
    {
        var gate = new TaskCompletionSource<SupabaseQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new CapturingSupabaseClient { PendingHistory = gate.Task };
        var domain = new DomainQueryService(client);
        var first = domain.GetRiderTeamHistoryAsync(["18"]);
        using var cancel = new CancellationTokenSource();
        var waiter = domain.GetRiderTeamHistoryAsync(["18"], cancellationToken: cancel.Token);
        Assert.Equal(1, client.HistoryRequests);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        gate.SetResult(new SupabaseQueryResult([new JsonObject { ["team_id"] = 8 }], null));
        Assert.Single(await first);
        Assert.Single(await domain.GetRiderTeamHistoryAsync(["18"]));
        Assert.Equal(1, client.HistoryRequests);
    }

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
        Assert.Equal(new[] { "get_ranking_input_packages", "get_ranking_input_packages" }, client.PackageFunctions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PointsSummaryUsesRawPointsOnlyWhenPreaggregationIsIncomplete(bool complete)
    {
        var client = new CapturingSupabaseClient { PointsComplete = complete };
        var domain = new DomainQueryService(client);
        var rows = await domain.GetPointsSummaryAsync(EmptyContext, [new JsonObject { ["id"] = 1 }], default);
        Assert.Equal(complete ? "get_ranking_points_summary" : "get_ranking_points_raw_summary", client.FunctionName);
        var row = Assert.Single(rows!);
        Assert.Equal("A Rider", row.String("rider_name"));
        Assert.Equal(0m, row.Decimal("points"));
        Assert.Equal("2", row.String("rider_id"));
    }

    [Fact]
    public async Task StatusFilteredPointsNeverUseAnUnfilteredSessionSummary()
    {
        var client = new CapturingSupabaseClient { PointsComplete = true };
        var domain = new DomainQueryService(client);
        await domain.GetPointsSummaryAsync(EmptyContext with { Status = "DNF" }, [new JsonObject { ["id"] = 1 }], default);
        Assert.Equal("get_ranking_points_raw_summary", client.FunctionName);
        Assert.Equal("DNF", client.Payload!.String("p_status"));
    }

    [Fact]
    public async Task RawPointsSummaryKeepsSessionScopeAndStatusWithoutDownloadingResults()
    {
        var client = new CapturingSupabaseClient();
        var domain = new DomainQueryService(client);
        var rows = await domain.GetPointsSummaryAsync(
            EmptyContext with { Status = "DNF" },
            [new JsonObject { ["id"] = 1 }, new JsonObject { ["id"] = 1 }, new JsonObject { ["id"] = 2 }], default);
        Assert.Single(rows);
        Assert.Equal("get_ranking_points_raw_summary", client.FunctionName);
        Assert.Equal("[1,2]", client.Payload!["p_session_ids"]!.ToJsonString());
        Assert.Equal("DNF", client.Payload.String("p_status"));
    }

    [Fact]
    public async Task EmptyPointsScopeDoesNotQueryDatabase()
    {
        var client = new CapturingSupabaseClient();
        Assert.Empty(await new DomainQueryService(client).GetPointsSummaryAsync(EmptyContext, [], default));
        Assert.Null(client.FunctionName);
    }

    [Fact]
    public async Task RawPointsSummaryUsesExactlyTheSameContextPredicatesAsRawRanking()
    {
        var client = new CapturingSupabaseClient();
        var domain = new DomainQueryService(client);
        var context = EmptyContext with
        {
            SeasonYears = [2025, 2026], RoundId = "10", TrackIds = ["2"], TrackName = "%Snake%",
            CountryCodes = ["ITA"], VenueNames = ["Val di Sole"], ConditionIds = [1], ConditionNames = ["Wet"],
            CompetitionClassIds = [2], CompetitionClassCodes = ["world_championship"], CategoryCodes = ["EM"],
            SessionTypes = ["FINAL"], FromDate = new DateOnly(2025, 1, 1), ToDate = new DateOnly(2026, 12, 31), Status = "Finished"
        };
        await domain.GetRankingDataAsync(context, resolvedSessionIds: ["1", "2"]);
        var expected = client.Payload!.DeepClone().AsObject();
        foreach (var key in new[] { "p_rider_id", "p_rider_query", "p_nation" }) expected.Remove(key);
        await domain.GetPointsSummaryAsync(context, [new JsonObject { ["id"] = 1 }, new JsonObject { ["id"] = 2 }], default);
        Assert.Equal("get_ranking_points_raw_summary", client.FunctionName);
        Assert.True(JsonNode.DeepEquals(expected, client.Payload));
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
    public void PointsOnlyRowsPreserveTieBreaksAndZeroPointRiders()
    {
        JsonObject Row(int rider, string name, decimal points) => new()
        {
            ["rider_id"] = rider, ["rider_name"] = name + " Rider", ["points"] = points
        };
        JsonObject[] rows = [Row(9, "Z", 10), Row(2, "A", 10), Row(3, "A", 10), Row(4, "None", 0)];
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
        public int HistoryRequests { get; private set; }
        public bool FailNextHistoryRead { get; set; }
        public Task<SupabaseQueryResult>? PendingHistory { get; init; }

        public Task<JsonNode?> RpcAsync(
            string functionName,
            JsonObject payload,
            SupabaseRole role,
            CancellationToken cancellationToken = default)
        {
            FunctionName = functionName;
            Payload = payload;
            if (functionName is "get_ranking_points_summary" or "get_ranking_points_raw_summary")
                return Task.FromResult<JsonNode?>(new JsonObject
                {
                    ["complete"] = PointsComplete || functionName == "get_ranking_points_raw_summary",
                    ["rows"] = new JsonArray(new JsonObject { ["rider_id"] = "2", ["points"] = 0,
                        ["rider"] = new JsonObject { ["first_name"] = "A", ["last_name"] = "Rider" } })
                });
            if (functionName is "get_ranking_input_packages")
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
            CancellationToken cancellationToken = default)
        {
            if (query.Table != "rider_team_history") throw new NotSupportedException();
            HistoryRequests++;
            if (PendingHistory is not null) return PendingHistory.WaitAsync(cancellationToken);
            if (FailNextHistoryRead)
            {
                FailNextHistoryRead = false;
                return Task.FromException<SupabaseQueryResult>(new HttpRequestException("Unavailable"));
            }
            return Task.FromResult(new SupabaseQueryResult([new JsonObject { ["rider_id"] = 18, ["team_id"] = 8 }], null));
        }

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
