using System.Text.Json.Nodes;
using RaceTrace.Api.Features.Compare;
using RaceTrace.Api.Features.Metrics;
using RaceTrace.Api.Features.Results;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class ReadPathResponsibilityTests
{
    [Fact]
    public void SessionSelectionKeepsSourceOrderAndLatestOwnedResultWithoutFilteringDns()
    {
        var older = Result("9", "1", "100");
        var latest = Result("10", "1", "100");
        latest["status"] = "DNS";
        var unrelated = Result("999", "2", "100");
        var second = Result("11", "2", "100");
        var firstSource = Source("1", [older, unrelated, latest]);
        var secondSource = Source("2", [second]);

        var selected = CompareSessionSelection.SelectSessionResults([firstSource, secondSource], "100");

        Assert.Equal(["1", "2"], selected.Select(item => item.Source.RiderId));
        Assert.Same(latest, selected[0].Result);
        Assert.Same(second, selected[1].Result);
        Assert.Equal(["100"], CompareSessionSelection.SessionIds([firstSource, secondSource]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TimingDecorationUsesWholeFieldEvenWhenOnlyOneResultIsOnThePage(bool sectors)
    {
        var leader = Result("1", "1", "100");
        leader["position"] = 1;
        leader["race_time_ms"] = 80_000;
        var follower = Result("2", "2", "100");
        follower["position"] = 2;
        follower["race_time_ms"] = 100_000;
        JsonObject[] field = [leader, follower];
        JsonObject[] splits = [Split("1", 30_000), Split("2", 40_000)];
        var loaded = field.Select(row => row.DeepClone().AsObject()).ToArray();
        var mode = sectors ? "sectors" : "splits";

        ResultTimingAnalytics.DecorateLoadedTiming(loaded, splits, mode);
        if (sectors)
            ResultTimingAnalytics.DecorateSectors([follower], field, splits);
        else
            ResultTimingAnalytics.DecorateSplits([follower], field, splits);

        Assert.True(JsonNode.DeepEquals(loaded[1][mode], follower[mode]));
        Assert.Null(leader[mode]);
        Assert.NotEmpty(follower.Array(mode)!);
    }

    [Fact]
    public void RunMappingPreservesDuplicateSplitChoiceAndInvalidClassification()
    {
        var result = Result("12", "7", "100");
        result["status"] = "DNF";
        result["position"] = 3;
        result["is_classified"] = true;
        result["race_time_ms"] = 90_000;
        result["top_speed"] = 42.5m;
        var older = Split("12", 30_000);
        older["id"] = 1;
        var latest = Split("12", 35_000);
        latest["id"] = 2;
        var splits = new Dictionary<string, IReadOnlyList<JsonObject>> { ["12"] = [latest, older] };

        var run = PaceRunMapper.BuildRun(result, splits, new PaceRunMapper.SessionSplitLayout(1));
        var typedRun = PaceRunMapper.BuildRun(RankingReadResult.FromLegacy(result), splits, new PaceRunMapper.SessionSplitLayout(1));

        Assert.Equal([35d, 55d], run.SectorTimes);
        Assert.Equal(run.SectorTimes, typedRun.SectorTimes);
        Assert.Equal(90d, run.FinishTimeSeconds);
        Assert.Equal(42.5d, run.TopSpeed);
        Assert.False(run.IsClassified);
        Assert.Equal("DNF", run.Status);
    }

    private static JsonObject Result(string id, string riderId, string sessionId) => new()
    {
        ["id"] = id, ["rider_id"] = riderId, ["session_id"] = sessionId,
        ["status"] = "Finished", ["is_classified"] = true
    };

    private static JsonObject Split(string resultId, int time) => new()
    {
        ["id"] = resultId, ["result_id"] = resultId, ["split_index"] = 1, ["race_time"] = time
    };

    private static CompareRiderSource Source(string riderId, JsonObject[] results) =>
        new(riderId, new JsonObject(), null, new RankingSnapshot([], results, results, [], []));
}
