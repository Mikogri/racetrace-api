using System.Text.Json.Nodes;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Tests;

public sealed class PaceServiceSectorTests
{
    [Fact]
    public void MissingSplitKeepsItsSectorIndexAndDoesNotMergeAdjacentTimes()
    {
        var splits = new JsonObject[]
        {
            Split(1, 40_000),
            Split(3, 100_000),
            Split(4, 150_000)
        };

        var sectors = PaceService.BuildSectorTimes(
            splits,
            maximumSplitIndex: 4);

        Assert.Equal([40d, null, null, 50d], sectors);
    }

    [Fact]
    public void FinishTimeCreatesTheSectorAfterTheLastSplit()
    {
        var splits = new JsonObject[]
        {
            Split(1, 40_000),
            Split(3, 100_000),
            Split(4, 130_000)
        };

        var sectors = PaceService.BuildSectorTimes(
            splits,
            maximumSplitIndex: 4,
            finishTimeMs: 180_000);

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
