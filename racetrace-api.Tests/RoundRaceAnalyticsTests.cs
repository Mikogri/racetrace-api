using System.Text.Json.Nodes;
using System.Text.Json;
using RaceTrace.Api.Features.Analytics;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class RoundRaceAnalyticsTests
{
    [Fact]
    public void ChaosUsesModerateCalibrationWithoutChangingComponentEvidence()
    {
        // Two of three riders swap ranks: footrule = 2/4, weight .4 => raw 20.
        var chaos = Calculate().Chaos;
        Assert.Equal(50m, chaos.Components.Single(c => c.Key == "positionChanges").Score);
        Assert.Equal(0.4m, chaos.Components.Single(c => c.Key == "positionChanges").Weight);
        Assert.Equal(33.6m, chaos.Score);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 4.5)]
    [InlineData(10, 12.4)]
    [InlineData(15, 22.6)]
    [InlineData(20, 33.6)]
    [InlineData(25, 44.1)]
    [InlineData(30, 53.2)]
    [InlineData(31, 54.8)]
    [InlineData(40, 66.1)]
    [InlineData(100, 100)]
    public void ChaosCalibrationMatchesAgreedScale(decimal raw, decimal expected) =>
        Assert.Equal(expected, RoundRaceAnalytics.CalibrateChaos(raw));

    [Fact]
    public void ChaosCalibrationDoesNotReverseOrderingAndStaysBounded()
    {
        var previous = 0m;
        for (var raw = 0m; raw <= 100m; raw += .1m)
        {
            var score = RoundRaceAnalytics.CalibrateChaos(raw);
            Assert.InRange(score, previous, 100m);
            previous = score;
        }
    }

    [Fact]
    public void AnalyticsUsesPublicCamelCaseContractUnderApplicationSnakeCasePolicy()
    {
        var json = JsonSerializer.SerializeToNode(Calculate(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!;
        Assert.NotNull(json["sectorEfficiency"]);
        Assert.NotNull(json["chaos"]!["dnfCount"]);
        Assert.NotNull(json["correlation"]!["points"]![0]!["riderId"]);
        Assert.NotNull(json["winningMargin"]!["marginSeconds"]);
        Assert.NotNull(json["runEfficiency"]!["sectorsFor80PercentGain"]);
        Assert.NotNull(json["distribution"]!["ridersCount"]);
    }

    [Fact]
    public void ContributionsPreserveNegativeSectorsAndSumToWinningMargin()
    {
        var result = Calculate();
        var margin = Assert.IsType<WinningMarginDto>(result.WinningMargin);
        Assert.Equal(2m, margin.MarginSeconds);
        Assert.Equal(new[] { -1m, 3m }, margin.Sectors.Select(x => x.GapSeconds));
        Assert.Equal(new decimal?[] { -50m, 150m }, margin.Sectors.Select(x => x.SharePercent));
        Assert.Equal(margin.MarginSeconds, margin.Sectors.Sum(x => x.GapSeconds));
        var run = Assert.IsType<RunEfficiencyDto>(result.RunEfficiency);
        Assert.Equal(1, run.SectorsWon);
        Assert.Equal(2, run.SectorsTop3);
        Assert.Equal(1.5m, run.AverageSectorPosition);
        Assert.Equal(1, run.SectorsFor80PercentGain);
    }

    [Fact]
    public void CorrelationUsesPairedRidersAndAverageRanksForTies()
    {
        var preFinal = new[] { Row(1, 1, 30000), Row(2, 1, 30000), Row(3, 3, 32000), Row(9, 4, 33000) };
        var result = Calculate(preFinal: preFinal);
        Assert.Equal(3, result.Correlation.Points.Count);
        Assert.Equal(0.866m, result.Correlation.Spearman);
        Assert.Equal("Q2", result.Correlation.PreFinalSession);
    }

    [Fact]
    public void ChaosExcludesDnsFromStartersAndCountsDnfWithPartialClock()
    {
        var rows = Rows().Append(Row(4, 4, 25000, "DNF")).Append(Row(5, 5, 25000, "DNS")).ToArray();
        var result = Calculate(rows);
        Assert.Equal(4, result.Chaos.Starters);
        Assert.Equal(1, result.Chaos.DnfCount);
        Assert.Equal(25m, result.Chaos.Components.Single(x => x.Key == "dnf").Score);
        Assert.Equal(3, result.Distribution.RidersCount);
        Assert.DoesNotContain(result.Correlation.Points, x => x.RiderId is 4 or 5);
    }

    [Fact]
    public void MissingSectorDoesNotInventContributionOrTreatItAsZero()
    {
        var result = Calculate(splits: [Split(1, 1, 10000), Split(3, 1, 12000)]);
        Assert.Null(result.WinningMargin);
        Assert.Null(result.RunEfficiency!.SectorsFor80PercentGain);
    }

    [Fact]
    public void SectorDensityUsesSecondsAndDistributionIncludesBoundaryScores()
    {
        var result = Calculate();
        Assert.Equal(0.75m, result.SectorEfficiency[0].PositionsPerSecond);
        Assert.Equal(3, result.Distribution.Bins.Sum(x => x.Count));
        Assert.Equal(1, result.Distribution.Bins[0].Count);
        Assert.Equal(1, result.Distribution.Bins[^1].Count);
    }

    [Fact]
    public void MissingDataIsNotReportedAsZeroChaosOrPerfectCorrelation()
    {
        var result = RoundRaceAnalytics.Calculate([], [], [], [], null);
        Assert.Null(result.Chaos.Score);
        Assert.Null(result.Correlation.Spearman);
        Assert.Null(result.WinningMargin);
        Assert.Null(result.RunEfficiency);
        Assert.Empty(result.SectorEfficiency);
        Assert.Equal(0, result.Distribution.RidersCount);
    }

    [Fact]
    public void PreFinalSelectionUsesLatestRelevantStageNotTrainingOrAnotherCategory()
    {
        var final = Session(9, "FINAL", "2026-09-27", 5);
        var selected = RoundRaceAnalytics.SelectPreFinal(
            [Session(1, "Q1", "2026-09-26", 1), Session(2, "Q2", "2026-09-27", 2),
             Session(3, "TIMED_TRAINING", "2026-09-27", 3), Session(4, "SEMI", "2026-09-27", 4, "2"),
             Session(10, "Q2", "2026-09-28", 1)], final);
        Assert.Equal(2, selected!["id"]!.GetValue<int>());
    }

    private static RoundRaceAnalyticsDto Calculate(JsonObject[]? rows = null, JsonObject[]? splits = null, JsonObject[]? preFinal = null) =>
        RoundRaceAnalytics.Calculate(rows ?? Rows(), splits ?? [Split(1, 1, 10000), Split(2, 1, 9000), Split(3, 1, 12000)],
            [Observation(1, 100), Observation(2, 50), Observation(3, 0), Observation(4, 10)],
            preFinal ?? [Row(1, 1, 30000), Row(2, 2, 32000), Row(3, 3, 34000)], "Q2");

    private static JsonObject[] Rows() => [Row(1, 1, 30000), Row(2, 2, 32000), Row(3, 3, 34000)];
    private static JsonObject Row(int id, int position, int time, string status = "OK") => new()
    {
        ["id"] = id, ["rider_id"] = id, ["session_id"] = 9, ["position"] = position,
        ["race_time_ms"] = time, ["status"] = status,
        ["riders"] = new JsonObject { ["first_name"] = "Rider", ["last_name"] = id.ToString() }
    };
    private static JsonObject Split(int resultId, int index, int time) => new()
    {
        ["result_id"] = resultId, ["split_index"] = index, ["race_time"] = time
    };
    private static JsonObject Session(int id, string type, string date, int order, string category = "1") => new()
    {
        ["id"] = id, ["session_type"] = type, ["session_date"] = date, ["session_order"] = order,
        ["round_id"] = "10", ["category_id"] = category
    };
    private static RiderSessionPaceObservation Observation(int id, double pace) => new("9", id.ToString(),
        new RiderSessionPaceResult(id.ToString(), PaceSessionType.Final, pace, null, null, null, 1, 1, true, null, []));
}
