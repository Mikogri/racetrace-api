using System.Text.Json.Nodes;
using RaceTrace.Api.Features.Results;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class PerfectRunResultsTests
{
    [Theory]
    [InlineData("DNF")]
    [InlineData("DNS")]
    [InlineData("DSQ")]
    [InlineData(" dnf ")]
    public void InvalidFinishDoesNotCreateFinishSector(string status)
    {
        var invalid = Result(2, 151_000);
        invalid["status"] = status;
        var splits = Enumerable
            .Range(1, 4)
            .SelectMany(index =>
                new[] { Split(1, index, index * 37_500), Split(2, index, index * 37_500) }
            )
            .ToArray();

        var sectors = ResultTimingProjection.BuildSectorsByResultId(
            splits,
            new[] { Result(1, 180_000), invalid },
            includeFinishSector: true
        );

        Assert.Equal(5, sectors["1"].Count);
        Assert.Equal(4, sectors["2"].Count);
        Assert.Equal(150_000, sectors["2"][^1].RaceTimeMs);
    }

    [Fact]
    public void PerfectRunIncludesTheFifthFinishSector()
    {
        var results = new[] { Result(1, 180_000), Result(2, 182_000) };
        var splits = new[]
        {
            Split(1, 1, 30_000),
            Split(1, 2, 70_000),
            Split(1, 3, 110_000),
            Split(1, 4, 150_000),
            Split(2, 1, 29_000),
            Split(2, 2, 71_000),
            Split(2, 3, 112_000),
            Split(2, 4, 151_000),
        };

        var perfect = ResultsService.BuildPerfectRun(results, splits, "sectors");

        Assert.NotNull(perfect);
        Assert.Equal(5, perfect.Times.Count);
        Assert.Equal(178m, perfect.FinalTime);
        Assert.Equal(5, perfect.Times[^1].Index);
        Assert.Equal(30m, perfect.Times[^1].Time);

        var splitView = ResultsService.BuildPerfectRun(results, splits, "splits");
        Assert.NotNull(splitView);
        Assert.Equal(4, splitView.Times.Count);
        Assert.Equal(178m, splitView.FinalTime);
    }

    [Fact]
    public void BuildsPerfectRunFromFastestSectorsWithGapsToSessionLeader()
    {
        var perfect = ResultsService.BuildPerfectRun(
            Results(),
            Splits(),
            "sectors",
            trackLengthM: 1000
        );

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
                Assert.Equal(0m, second.GapToBest);
                Assert.Equal(1, second.Position);
            }
        );
    }

    [Fact]
    public void BuildsPerfectRunSplitsAsCumulativeFastestSectorTimes()
    {
        var perfect = ResultsService.BuildPerfectRun(Results(), Splits(), "splits");

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
            }
        );
    }

    private static IReadOnlyList<JsonObject> Results() =>
        [
            new JsonObject
            {
                ["id"] = 1,
                ["race_time_ms"] = 30_000,
                ["position"] = 2,
                ["is_classified"] = true,
                ["status"] = "Finished",
            },
            new JsonObject
            {
                ["id"] = 2,
                ["race_time_ms"] = 29_000,
                ["position"] = 1,
                ["is_classified"] = true,
                ["status"] = "Finished",
            },
        ];

    private static IReadOnlyList<JsonObject> Splits() =>
        [Split(1, 1, 10_000), Split(1, 2, 30_000), Split(2, 1, 11_000), Split(2, 2, 29_000)];

    private static JsonObject Split(int resultId, int index, int timeMs) =>
        new()
        {
            ["id"] = $"{resultId}{index}",
            ["result_id"] = resultId,
            ["split_index"] = index,
            ["race_time"] = timeMs,
        };

    private static JsonObject Result(int resultId, int finishTimeMs) =>
        new()
        {
            ["id"] = resultId,
            ["race_time_ms"] = finishTimeMs,
            ["position"] = resultId,
            ["is_classified"] = true,
            ["status"] = "Finished",
        };
}
