using System.Text.Json.Nodes;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Tests;

public sealed class FieldSpreadTests
{
    [Fact]
    public void ProgressionKeepsCutoffsInsideTiedPlaces()
    {
        var rows = Enumerable.Range(1, 80)
            .Select(position => Row(position == 65 ? 64 : position,
                100000 + (position == 65 ? 64 : position) * 1000)).ToArray();
        var points = AnalyticsService.BuildFieldProgression(rows, 101000);
        Assert.Equal(4m, points.Single(point => point.Position == 65).IncrementalSpread);
        Assert.Equal(6m, points.Single(point => point.Position == 70).IncrementalSpread);
        Assert.All(points, point => Assert.NotNull(point.IncrementalSpread));
    }

    [Fact]
    public void SpreadUsesPreviousThresholdRatherThanWinner()
    {
        var spread = AnalyticsService.BuildFieldSpread(
            [Row(1, 100000), Row(3, 107000), Row(5, 108200), Row(10, 112000), Row(20, 116000)], 100000, [3, 5, 10, 20]);

        Assert.Equal(new decimal?[] { 7m, 1.2m, 3.8m, 4m }, spread.Points.Select(p => p.IncrementalSpread));
        Assert.Equal(new[] { 1, 3, 5, 10 }, spread.Points.Select(p => p.PreviousPosition));
        Assert.Equal(new decimal?[] { 7m, 1.2m, 3.8m, 4m }, spread.Points.Select(p => p.IncrementalSpreadPercent));
        Assert.Equal(12m, spread.P10GapPercent);
    }

    [Fact]
    public void MissingPreviousThresholdDoesNotFallBackToWinner()
    {
        var spread = AnalyticsService.BuildFieldSpread([Row(1, 100000), Row(5, 108200)], 100000, [3, 5, 10, 20]);
        var point = Assert.Single(spread.Points);
        Assert.Equal(3, point.PreviousPosition);
        Assert.Null(point.IncrementalSpread);
    }

    [Fact]
    public void ProgressionCoversWholeFieldIncludingFinalPartialBlock()
    {
        var points = AnalyticsService.BuildFieldProgression(
            [Row(1, 100000), Row(3, 107000), Row(5, 108200), Row(10, 112000),
             Row(15, 114000), Row(20, 116000), Row(23, 119000)], 100000);
        Assert.Equal(new[] { 3, 5, 10, 15, 20, 23 }, points.Select(p => p.Position));
        Assert.Equal(new[] { 1, 3, 5, 10, 15, 20 }, points.Select(p => p.PreviousPosition));
        Assert.Equal(new decimal?[] { 7m, 1.2m, 3.8m, 2m, 2m, 3m }, points.Select(p => p.IncrementalSpread));
    }

    [Fact]
    public void ProgressionRequiresAtLeastThirdPlace()
    {
        Assert.Empty(AnalyticsService.BuildFieldProgression([Row(1, 100000), Row(2, 101000)], 100000));
    }

    [Fact]
    public void ProgressionTrimsOnlyAnOutlyingSuffix()
    {
        var points = AnalyticsService.BuildFieldProgression(
            [Row(1, 100000), Row(3, 102000), Row(5, 104000), Row(10, 109000),
             Row(15, 114000), Row(20, 164000), Row(23, 194000)], 100000);
        Assert.Equal(new[] { 3, 5, 10, 15 }, points.Select(p => p.Position));
    }

    [Fact]
    public void ProgressionKeepsInteriorSpikeAndNormalTail()
    {
        var points = AnalyticsService.BuildFieldProgression(
            [Row(1, 100000), Row(3, 102000), Row(5, 104000), Row(10, 109000),
             Row(15, 159000), Row(20, 164000), Row(23, 167000)], 100000);
        Assert.Equal(6, points.Count);
    }

    [Fact]
    public void ProgressionNormalizesFinalPartialBlock()
    {
        var points = AnalyticsService.BuildFieldProgression(
            [Row(1, 100000), Row(3, 102000), Row(5, 104000), Row(10, 109000),
             Row(15, 114000), Row(20, 119000), Row(21, 124000)], 100000);
        Assert.Equal(20, points.Last().Position);
    }

    [Fact]
    public void RunPercentagesUseMeasuredSectorsAndHandleMissingEvidence()
    {
        var run = new UCI_Stats_API.Models.RunEfficiencyDto(1, "Rider", 5, 4, 1, 3, 2m, 2);
        Assert.Equal(25m, run.SectorsWonPercent);
        Assert.Equal(75m, run.SectorsTop3Percent);
        Assert.Null((run with { MeasuredSectors = 0 }).SectorsWonPercent);
    }

    [Theory]
    [InlineData(15, new[] { 3, 5, 10 })]
    [InlineData(30, new[] { 3, 5, 10, 20 })]
    [InlineData(60, new[] { 3, 10, 20, 40 })]
    [InlineData(0, new int[0])]
    [InlineData(2, new int[0])]
    [InlineData(3, new[] { 3 })]
    [InlineData(4, new[] { 3 })]
    [InlineData(6, new[] { 3, 4 })]
    [InlineData(12, new[] { 3, 4, 8 })]
    public void SpreadScalesThresholdsToClassifiedField(int count, int[] expected)
    {
        var rows = Enumerable.Range(1, count).Select(position => Row(position, 100000 + (position - 1) * 1000)).ToArray();
        var spread = AnalyticsService.BuildFieldSpread(rows, 100000);
        Assert.Equal(expected, spread.Points.Select(point => point.Position));
        Assert.All(spread.Points, point => {
            Assert.True(point.Position > point.PreviousPosition);
            Assert.Equal((decimal)(point.Position - point.PreviousPosition), point.IncrementalSpread);
        });
    }

    [Fact]
    public void P10ClassificationDoesNotDependOnAdaptiveChartThresholds()
    {
        var rows = Enumerable.Range(1, 45).Select(position => Row(position, 100000 + (position - 1) * 1000)).ToArray();
        var spread = AnalyticsService.BuildFieldSpread(rows, 100000);
        Assert.DoesNotContain(spread.Points, point => point.Position == 10);
        Assert.Equal(9m, spread.P10GapPercent);
        Assert.Equal("spread", spread.Classification);
    }

    private static JsonObject Row(int position, int time) => new()
    {
        ["position"] = position, ["race_time_ms"] = time
    };
}
