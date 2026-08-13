using System.Text.Json.Nodes;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Tests;

public sealed class PerfectRunResultsTests
{
    [Fact]
    public void BuildsPerfectRunFromFastestSectorsWithNegativeWinningGaps()
    {
        var perfect = ResultsService.BuildPerfectRun(
            Results(),
            Splits(),
            "sectors",
            trackLengthM: 1000);

        Assert.NotNull(perfect);
        Assert.True(perfect.IsPerfectRun);
        Assert.Null(perfect.Position);
        Assert.Null(perfect.ResultId);
        Assert.Null(perfect.RiderId);
        Assert.Equal("Perfect Run", perfect.FullName);
        Assert.Equal(28m, perfect.FinalTime);
        Assert.Equal(-1m, perfect.FinalGapToBest);
        Assert.Equal(128.571m, perfect.AverageSpeed);
        Assert.Collection(
            perfect.Times,
            first =>
            {
                Assert.Equal(10m, first.Time);
                Assert.Equal(-1m, first.GapToBest);
                Assert.Equal(1, first.Position);
            },
            second =>
            {
                Assert.Equal(18m, second.Time);
                Assert.Equal(-2m, second.GapToBest);
                Assert.Equal(1, second.Position);
            });
    }

    [Fact]
    public void BuildsPerfectRunSplitsAsCumulativeFastestSectorTimes()
    {
        var perfect = ResultsService.BuildPerfectRun(
            Results(),
            Splits(),
            "splits");

        Assert.NotNull(perfect);
        Assert.Collection(
            perfect.Times,
            first =>
            {
                Assert.Equal(10m, first.Time);
                Assert.Equal(-1m, first.GapToBest);
            },
            second =>
            {
                Assert.Equal(28m, second.Time);
                Assert.Equal(-1m, second.GapToBest);
            });
    }

    private static IReadOnlyList<JsonObject> Results() =>
    [
        new JsonObject
        {
            ["id"] = 1,
            ["race_time_ms"] = 30_000,
            ["position"] = 2,
            ["is_classified"] = true,
            ["status"] = "Finished"
        },
        new JsonObject
        {
            ["id"] = 2,
            ["race_time_ms"] = 29_000,
            ["position"] = 1,
            ["is_classified"] = true,
            ["status"] = "Finished"
        }
    ];

    private static IReadOnlyList<JsonObject> Splits() =>
    [
        Split(1, 1, 10_000),
        Split(1, 2, 30_000),
        Split(2, 1, 11_000),
        Split(2, 2, 29_000)
    ];

    private static JsonObject Split(int resultId, int index, int timeMs) => new()
    {
        ["id"] = $"{resultId}{index}",
        ["result_id"] = resultId,
        ["split_index"] = index,
        ["race_time"] = timeMs
    };
}
