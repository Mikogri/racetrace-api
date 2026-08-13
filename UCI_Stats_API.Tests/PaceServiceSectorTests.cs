using System.Text.Json.Nodes;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Tests;

public sealed class PaceServiceSectorTests
{
    [Fact]
    public void MissingSplitKeepsItsSectorIndexAndDoesNotMergeAdjacentTimes()
    {
        var result = new JsonObject { ["race_time_ms"] = 150_000 };
        var splits = new JsonObject[]
        {
            Split(1, 40_000),
            Split(3, 100_000),
            Split(4, 150_000)
        };

        var sectors = PaceService.BuildSectorTimes(
            result,
            splits,
            maximumSplitIndex: 4,
            finishIsTimingSplit: true);

        Assert.Equal([40d, null, null, 50d], sectors);
    }

    [Fact]
    public void ChronoRaceFinishIsAppendedAfterTheConfiguredSplitLayout()
    {
        var result = new JsonObject { ["race_time_ms"] = 180_000 };
        var splits = new JsonObject[]
        {
            Split(1, 40_000),
            Split(3, 100_000),
            Split(4, 130_000)
        };

        var sectors = PaceService.BuildSectorTimes(
            result,
            splits,
            maximumSplitIndex: 4,
            finishIsTimingSplit: false);

        Assert.Equal([40d, null, null, 30d, 50d], sectors);
    }

    private static JsonObject Split(int index, int raceTime)
    {
        return new JsonObject
        {
            ["id"] = index,
            ["split_index"] = index,
            ["race_time"] = raceTime
        };
    }
}
