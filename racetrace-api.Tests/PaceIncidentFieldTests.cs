using System.Text.Json.Nodes;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class PaceIncidentFieldTests
{
    [Fact]
    public void DetailContextMatchesWholeRankingWithoutReturningOtherRiderRows()
    {
        var full = Package();
        var ranking = DomainQueryService.ParseRiderDetailDataBundle(full);
        var detail = (JsonObject)full.DeepClone();
        foreach (var key in new[] { "r", "a", "q" })
            detail[key] = new JsonArray(((JsonArray)full[key]!).OfType<JsonArray>()
                .Where(r => r[0]!.GetValue<int>() == 1).Select(r => r.DeepClone()).ToArray());
        detail["i"] = new JsonArray(Enumerable.Range(1, 3).Select(id => (JsonNode)new JsonArray(
            10, id, id == 1 ? 280 : 250,
            new JsonArray(Enumerable.Range(0, 5).Select(i => (JsonNode)new JsonArray(
                i, id == 1 && i == 2 ? 80 : 50, id == 1 && i == 2 ? 3 : 1)).ToArray()))).ToArray());
        var parsed = DomainQueryService.ParseRiderDetailDataBundle(detail);
        var target = Assert.Single(parsed.PaceSessions);
        Assert.Single(parsed.Data.Results);
        var expected = ranking.PaceSessions.Single(s => s.Run.RiderId == "1");
        Assert.NotNull(target.PaceFinish);
        Assert.Equal(expected.PaceFinish, target.PaceFinish);
        Assert.Equal(250, target.PaceFinish.TimeSeconds);
        Assert.Equal(30, target.PaceFinish.RemovedLossSeconds);
        Assert.Equal(280, target.Run.FinishTimeSeconds);
        var calculator = new DownhillPaceCalculator();
        Assert.Equal(calculator.CalculatePrecomputedSession(expected).Pace,
            calculator.CalculatePrecomputedSession(target).Pace);
        // Filtering and repeated preparation must not shrink or recalculate the field.
        Assert.Equal(target.PaceFinish, calculator.PrepareField([target]).Single().PaceFinish);
    }

    [Theory]
    [InlineData("DNF")]
    [InlineData("DNS")]
    [InlineData("DSQ")]
    public void SportingStatusesDoNotReceiveInventedFinishTimes(string status)
    {
        var full = Package();
        var rows = (JsonArray)full["r"]!;
        ((JsonArray)rows[0]!)[6] = false;
        ((JsonArray)rows[0]!)[7] = status;
        ((JsonArray)full["h"]![0]!)[8] = 2;
        var bundle = DomainQueryService.ParseRiderDetailDataBundle(full);
        var run = bundle.PaceSessions.Single(s => s.Run.RiderId == "1");
        Assert.Null(run.PaceFinish?.TimeSeconds);
        Assert.Equal(0, run.PaceFinish?.RemovedLossSeconds ?? 0);
    }

    private static JsonObject Package()
    {
        var r = new JsonArray(); var a = new JsonArray(); var q = new JsonArray(); var g = new JsonArray();
        for (var id = 1; id <= 3; id++)
        {
            r.Add(new JsonArray(id, id, 10, id == 1 ? 280000 : 250000, null, id, true, "Finished", 0));
            a.Add(new JsonArray(id, 10, 3, null));
            for (var i = 0; i < 5; i++) q.Add(new JsonArray(id, i, id == 1 && i == 2 ? 80 : 50, id == 1 && i == 2 ? 3 : 1));
        }
        for (var i = 0; i < 5; i++) g.Add(new JsonArray(10, i, 3, 50, Math.Log(50), .02, 1, 50, 50));
        return new JsonObject
        {
            ["s"] = new JsonArray(new JsonArray(10, 20, 30, "Final", "FINAL", "2026-06-01", 40, 2026,
                "2026-06-01", 2, "EM")),
            ["r"] = r, ["a"] = a, ["q"] = q, ["g"] = g,
            ["h"] = new JsonArray(new JsonArray(10, null, null, null, null, null, null, null,
                3, 250, Math.Log(250), .02, 1, 250, 250))
        };
    }
}
