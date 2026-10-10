using System.Text.Json.Nodes;
using RaceTrace.Api.Services;
using RaceTrace.Api.Models;

namespace RaceTrace.Api.Tests;

public sealed class PaceServiceSectorTests
{
    [Theory]
    [InlineData("DSQ")]
    [InlineData("DNS")]
    [InlineData("DNF")]
    public async Task PreaggregatedConsistencyPreservesFinalQualification(string finalStatus)
    {
        var service = new PaceService(null!); // All input data is supplied; no database access.
        var rows = new[] { Result("Q1", "OK", 1), Result("Q2", "OK", 1), Result("FINAL", finalStatus, null) };
        for (var index = 0; index < rows.Length; index++)
        {
            rows[index]["id"] = index + 1;
            rows[index]["rider_id"] = "1";
            rows[index]["session_id"] = rows[index]["sessions"]!["id"]!.DeepClone();
        }
        var inputs = rows.Select(row => new PacePrecomputedSessionData(
            row["session_id"]!.ToString(), "round:1:category:2", "2",
            PaceService.ParseSessionType(row["session_id"]!.ToString(), null),
            new PaceRiderRun("1", [], FinishRank: row["position"]?.GetValue<int>(),
                IsClassified: row["status"]!.ToString() == "OK", Status: row["status"]!.ToString()),
            row["position"] is null ? 0 : 1, [], null, null, null, [])).ToArray();
        var raw = await service.CalculateAsync(rows,
            new RiderMetricsContext(null, null, null, null, null, null, null, null, null, null, null),
            resultsContainFullField: true, suppliedSplits: []);
        var cached = service.CalculatePrecomputed(rows.Select(RankingReadResult.FromLegacy).ToArray(), inputs);

        Assert.NotNull(raw["1"].Consistency);
        Assert.Equal(raw["1"].Consistency, cached["1"].Consistency);
        Assert.Equal(raw["1"].ConsistencyUsedRuns, cached["1"].ConsistencyUsedRuns);
    }

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

    [Fact]
    public void CompletedQualifyingWithoutFinalEntryIsNonFinalist()
    {
        Assert.True(PaceService.IsNonFinalist(
            [Result("Q1", "OK", 35)],
            [Session("FINAL")]));
    }

    [Theory]
    [InlineData("DNS")]
    [InlineData("DNF")]
    [InlineData("DSQ")]
    public void AnyFinalEntryConfirmsQualification(string status)
    {
        Assert.False(PaceService.IsNonFinalist(
            [Result("Q1", "OK", 5), Result("FINAL", status, null)],
            [Session("FINAL")]));
    }

    [Fact]
    public void QualifyingOnlyFormatDoesNotApplyNonFinalistCap()
    {
        Assert.False(PaceService.IsNonFinalist(
            [Result("Q1", "OK", 1)],
            [Session("Q1")]));
    }

    private static JsonObject Result(string sessionType, string status, int? position)
    {
        return new JsonObject
        {
            ["status"] = status,
            ["position"] = position,
            ["sessions"] = Session(sessionType)
        };
    }

    private static JsonObject Session(string sessionType)
    {
        return new JsonObject
        {
            ["id"] = sessionType,
            ["session_type"] = sessionType,
            ["round_id"] = 1,
            ["category_id"] = 2
        };
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
