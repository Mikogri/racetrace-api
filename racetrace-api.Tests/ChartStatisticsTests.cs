using System.Text.Json;
using System.Text.Json.Nodes;
using RaceTrace.Api.Features.Riders;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using RaceTrace.Api.Infrastructure;

namespace RaceTrace.Api.Tests;

public sealed class ChartStatisticsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("elite_men,elite_women")]
    public async Task ComparisonRejectsMissingOrMultipleCategoriesBeforeDataAccess(string category)
    {
        var service = new CompareService(new QueryValidationService(), new DomainQueryService(null!), null!, null!);
        var query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["riderIds"] = "1,2", ["category"] = category
        });
        var error = await Assert.ThrowsAsync<ApiException>(() => service.CompareAsync(query, CancellationToken.None));
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public async Task ComparisonRejectsReversedDateRangeBeforeDataAccess()
    {
        var service = new CompareService(new QueryValidationService(), new DomainQueryService(null!), null!, null!);
        var query = new QueryCollection(new Dictionary<string, StringValues>
        {
            ["riderIds"] = "1,2", ["category"] = "elite_men",
            ["dateFrom"] = "2026-12-31", ["dateTo"] = "2026-01-01"
        });
        var error = await Assert.ThrowsAsync<ApiException>(() => service.CompareAsync(query, CancellationToken.None));
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public void SectorSummaryUsesSampleWeightsAndExcludesFinish()
    {
        var summary = CompareService.SummarizeSectorGaps([
            new(1, "A", 1, false, 1m, 1), new(2, "B", 1, false, 3m, 3),
            new(1, "A", 2, false, 9m, 0), new(1, "A", 1, true, 90m, 20)]);
        Assert.Equal(2.5m, summary[0].AverageGap);
        Assert.Equal(4, summary[0].SamplesCount);
        Assert.Null(summary[1].AverageGap);
    }

    [Fact]
    public void RiderChartAggregatesPreserveChronologyAndExcludeUnclassifiedPositions()
    {
        static JsonObject Result(int id, int position, string type, string status, bool classified = true) => new()
        {
            ["id"] = id, ["position"] = position, ["status"] = status, ["is_classified"] = classified,
            ["sessions"] = new JsonObject { ["id"] = id, ["session_type"] = type,
                ["rounds"] = new JsonObject { ["id"] = id, ["track_id"] = 7 } }
        };
        var rounds = new[] {
            JsonSerializer.Deserialize<RiderRoundResultDto>("""{"roundId":2,"roundStartDate":"2026-06-01","roundNumber":2,"awardedPoints":20}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!,
            JsonSerializer.Deserialize<RiderRoundResultDto>("""{"roundId":1,"roundStartDate":"2026-05-01","roundNumber":1,"awardedPoints":10}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!
        };
        var tracks = new[] { JsonSerializer.Deserialize<RiderTrackStatisticsDto>("""{"trackId":7}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))! };
        var statistics = RiderChartStatistics.Build([
            Result(1, 2, "Q1", "Finished"), Result(2, 4, "Q2", "Classified"),
            Result(3, 1, "Final Run", "Finished"), Result(4, 90, "Q1", "Did not finish"),
            Result(5, 70, "Q1", "Finished", false)], rounds, tracks);
        Assert.Equal(7m / 3m, statistics.ResultsAveragePosition);
        Assert.Equal(3m, statistics.PreFinalAverageByTrack["7"]);
        Assert.Equal(10m, statistics.CumulativePointsByRound["1"]);
        Assert.Equal(30m, statistics.CumulativePointsByRound["2"]);
    }

    [Fact]
    public void EmptyRiderDataHasNoInventedStatistics()
    {
        var statistics = RiderChartStatistics.Build([], [], []);
        Assert.Null(statistics.ResultsAveragePosition);
        Assert.Empty(statistics.CumulativePointsByRound);
        Assert.Empty(statistics.PreFinalAverageByTrack);
    }
}
