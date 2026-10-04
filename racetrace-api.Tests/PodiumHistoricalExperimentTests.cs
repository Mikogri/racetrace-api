using System.Net;
using System.Text.Json.Nodes;
using RaceTrace.Api.Models;
using RaceTrace.Api.PodiumBacktest;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class PodiumHistoricalExperimentTests
{
    [Fact]
    public void HistoryExcludesTargetWeekendFutureSessionsAndOtherCategories()
    {
        var cutoff = DateTimeOffset.Parse("2024-06-14T00:00:00+02:00");
        var old = Round("1", "2024-06-01", [Session("1", "EM", "2024-06-02"), Session("2", "EW", "2024-06-02"), Session("3", "EM", "2024-06-15")]);
        // Even an incorrectly early target-session date cannot leak the target round.
        var target = Round("2", "2024-06-14T00:00:00+02:00", [Session("4", "EM", "2024-06-13")]);
        var future = Round("3", "2024-07-01", [Session("5", "EM", "2024-07-01")]);
        var result = HistoricalData.Combine([old, target, future], "EM", cutoff);
        Assert.Equal("1", Assert.Single(result.Sessions)["id"]!.ToString());
        Assert.Equal("1", Assert.Single(result.Results)["session_id"]!.ToString());
        Assert.Single(result.FieldResults);
        Assert.Single(result.Splits);
        Assert.Single(result.PointsByResultId);
    }

    [Fact]
    public void HistoryWithoutSessionDateFailsClosed()
    {
        var round = Round("1", "2021-01-01", [new JsonObject { ["id"] = "1", ["categories"] = new JsonObject { ["code"] = "EM" } }]);
        Assert.Throws<InvalidOperationException>(() => HistoricalData.Combine([round], "EM", DateTimeOffset.Parse("2024-01-01")));
    }

    [Fact]
    public void IncidentAndDnfDoNotContaminateCleanMeanOrVariance()
    {
        HistoricalRun[] clean = [new(88, 1, false, false, true, false), new(92, 1, false, false, true, false)];
        var normal = CleanPaceExperiment.Fit("1", clean, 50, true, false);
        var incidents = CleanPaceExperiment.Fit("1", [..clean, new(10, 1, true, false, true, false), new(5, 1, true, true, false, false)], 50, true, false);
        Assert.Equal(normal.Mean, incidents.Mean);
        Assert.Equal(normal.StandardDeviation, incidents.StandardDeviation);
        Assert.True(incidents.IncidentProbability > normal.IncidentProbability);
        Assert.True(incidents.DnfProbability > normal.DnfProbability);
        Assert.Equal((1 + 8 * 0.08) / 12, incidents.IncidentProbability, 10);
        Assert.Equal((1 + 8 * 0.03) / 12, incidents.DnfProbability, 10);
    }

    [Fact]
    public void VariabilityIncreasesUncertaintyWithoutChangingMean()
    {
        var stable = CleanPaceExperiment.Fit("1", [new(89, 1, false, false, true, false), new(91, 1, false, false, true, false)], 50, true, false);
        var variable = CleanPaceExperiment.Fit("1", [new(75, 1, false, false, true, false), new(105, 1, false, false, true, false)], 50, true, false);
        Assert.Equal(stable.Mean, variable.Mean);
        Assert.True(variable.StandardDeviation > stable.StandardDeviation);
    }

    [Fact]
    public void NoEvidenceHasFinitePriorAndNoTrackBoost()
    {
        var distribution = CleanPaceExperiment.Fit("1", [], 75, true, true);
        Assert.Equal(75, distribution.Mean);
        Assert.Equal(5, distribution.StandardDeviation);
        Assert.Equal(0.08, distribution.IncidentProbability, 10);
        Assert.Equal(0.03, distribution.DnfProbability, 10);
    }

    [Fact]
    public void RiskAblationDoesNotChangeCleanPerformance()
    {
        HistoricalRun[] runs = [new(90, 1, false, false, true, false), new(null, 1, false, true, false, false)];
        var withRisk = CleanPaceExperiment.Fit("1", runs, 50, true, false);
        var withoutRisk = CleanPaceExperiment.Fit("1", runs, 50, false, false);
        Assert.Equal(withRisk.Mean, withoutRisk.Mean);
        Assert.Equal(withRisk.StandardDeviation, withoutRisk.StandardDeviation);
        Assert.Equal(0, withoutRisk.IncidentProbability);
        Assert.Equal(0, withoutRisk.DnfProbability);
    }

    [Fact]
    public void SimulationIsReproducibleAndDnfNeverGetsPodium()
    {
        CleanPaceDistribution[] riders = [new("1", 100, 1, 0, 1), new("2", 90, 1, 0, 0), new("3", 80, 1, 0, 0), new("4", 70, 1, 0, 0)];
        var first = CleanPaceExperiment.Simulate(riders, 2000, 123);
        Assert.Equal(first, CleanPaceExperiment.Simulate(riders, 2000, 123));
        Assert.Equal(0, first[0].PodiumProbability);
        Assert.Equal(0, first[0].WinProbability);
        Assert.Equal(1, first.Sum(value => value.WinProbability), 10);
        Assert.Equal(3, first.Sum(value => value.PodiumProbability), 10);
    }

    [Fact]
    public void MissingPodiumRidersCountAsMissesAndUnclassifiedZeroIsNotPodium()
    {
        PodiumBacktestPrediction[] predictions = [new("a", "0", 0, 0.9, 0.9), new("a", "4", 4, 0.8, 0.8),
            new("a", "1", 1, 0.7, 0.7), new("a", "2", 2, 0, -1), new("a", "3", 3, 0, -1)];
        var result = PodiumPredictionBacktester.Evaluate(predictions);
        Assert.Equal(1.0 / 3, result.PodiumRecallAt3, 10);
        Assert.Equal(0, result.WinnerAccuracy);
    }

    [Fact]
    public void WinnerAccuracyUsesWinProbabilityRatherThanPodiumProbability()
    {
        PodiumBacktestPrediction[] predictions = [new("a", "2", 2, 0.9, 0.9, 0.3), new("a", "1", 1, 0.8, 0.8, 0.6), new("a", "3", 3, 0.7, 0.7, 0.1)];
        Assert.Equal(1, PodiumPredictionBacktester.Evaluate(predictions).WinnerAccuracy);
    }

    [Theory]
    [InlineData("DELETE", "results")]
    [InlineData("POST", "results")]
    [InlineData("PATCH", "riders")]
    [InlineData("POST", "rpc/refresh_rider_team_history")]
    public async Task DatabaseMutationIsBlockedBeforeNetwork(string method, string path)
    {
        using var client = new HttpClient(new ReadOnlyCacheHandler(Path.GetTempPath(), true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "https://example.invalid/rest/v1/" + path)));
    }

    private static JsonObject Session(string id, string category, string date) => new()
    {
        ["id"] = id, ["session_date"] = date, ["categories"] = new JsonObject { ["code"] = category }
    };

    private static HistoricalRound Round(string id, string date, JsonObject[] sessions)
    {
        var results = sessions.Select(session => new JsonObject { ["id"] = session["id"]!.ToString(), ["session_id"] = session["id"]!.ToString() }).ToArray();
        var splits = results.Select(result => new JsonObject { ["result_id"] = result["id"]!.ToString() }).ToArray();
        var data = new RankingDataBundle(sessions, results, results, splits, results.ToDictionary(result => result["id"]!.ToString(), _ => 1m));
        return new HistoricalRound(new JsonObject { ["id"] = id, ["start_date"] = date }, data, []);
    }
}
