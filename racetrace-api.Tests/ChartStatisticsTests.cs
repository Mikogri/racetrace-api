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
    [Fact]
    public async Task RatingHistoryUsesChronologicalPrefixesWithoutFutureRoundsOrExtraReads()
    {
        var calculator = new DownhillPaceCalculator();
        var rows = new List<JsonObject>();
        var prepared = new List<PacePrecomputedSessionData>();
        foreach (var round in new[] { 2, 1 })
        {
            var field = new PaceSessionData(PaceSessionType.Final, Enumerable.Range(1, 10).Select(i =>
                new PaceRiderRun(i.ToString(), new double?[] { 40 + i, 45 + i, 50 + i, 55 + i },
                    FinishRank: i, FinishTimeSeconds: 190 + 4 * i, Status: "OK")).ToArray());
            prepared.AddRange(calculator.PrepareSession(field).Select(run => run with
            {
                SessionId = round.ToString(), CourseKey = "track:1:category:1", CategoryId = "1"
            }));
            rows.Add(new JsonObject
            {
                ["id"] = round, ["rider_id"] = "1", ["session_id"] = round.ToString(),
                ["position"] = 1, ["status"] = "OK", ["is_classified"] = true, ["race_time_ms"] = 194000,
                ["sessions"] = new JsonObject
                {
                    ["id"] = round.ToString(), ["round_id"] = round.ToString(), ["session_type"] = "FINAL",
                    ["session_date"] = $"2026-06-0{round}",
                    ["categories"] = new JsonObject { ["id"] = "1", ["code"] = "EM" },
                    ["rounds"] = new JsonObject { ["id"] = round.ToString(), ["start_date"] = $"2026-06-0{round}" }
                }
            });
        }
        // Null clients guarantee that calculation cannot accidentally issue a database request.
        var metrics = new MetricsService(null!, new PaceService(null!));
        var query = new RaceTrace.Api.Features.Riders.RiderResultQueries(null!, null!, metrics, null!);
        var context = new RiderMetricsContext(2026, "EM", null, null, null, null, null, null, null, null, null);
        var snapshot = new RankingSnapshot([], rows, rows, [], [], prepared);
        var history = await query.CumulativeRatingsAsync("1", context, snapshot, default);
        Assert.Equal(new[] { "1", "2" }, history.Keys);
        foreach (var cutoff in new[] { 1, 2 })
        {
            var prefix = rows.Where(row => row.Int("id") <= cutoff).ToArray();
            var expected = metrics.CalculatePrecomputedPerformanceByRider(prefix,
                prepared.Where(run => int.Parse(run.SessionId) <= cutoff).ToArray(),
                prefix.Select(row => row.Object("sessions")!).ToArray());
            if (cutoff == 1) Assert.Null(history["1"]); // One run has no consistency evidence yet.
            else Assert.NotNull(history["2"]);
            Assert.Equal(expected["1"].Rating, history[cutoff.ToString()]);
        }
        Assert.Empty(await query.CumulativeRatingsAsync("99", context, snapshot, default));
    }

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
