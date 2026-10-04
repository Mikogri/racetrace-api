using System.Text.Json.Nodes;
using RaceTrace.Api.Features.Results;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public class RoundResultSelectionTests
{
    [Fact]
    public void AllKeepsFastestCompletedWholeRunAndReRanksCombinedField()
    {
        var rows = new[] { Row(1, 10, 120, "OK"), Row(2, 10, 110, "OK"), Row(3, 10, 1, "DNF"), Row(4, 20, 115, "OK"), Row(5, 30, 0, "DNS") };
        var selected = RoundResultSelection.Select(rows, bestOnly: true);
        Assert.Equal(new[] { "2", "4" }, selected.Select(row => row.String("id")));
        Assert.Equal(new int?[] { 1, 2 }, selected.Select(row => row.Int("position")));
        Assert.Equal("Q2", selected[0].Object("sessions")!.String("session_type"));
        Assert.Equal(9, rows[1].Int("position"));
    }

    [Fact]
    public void CombinedCategoryRetainsNonFinishersAndTiedRanks()
    {
        var selected = RoundResultSelection.Select(new[] { Row(1, 1, 100, "OK"), Row(2, 2, 100, "OK"), Row(3, 3, 120, "OK"), Row(4, 4, 10, "DNF") }, false);
        Assert.Equal(new int?[] { 1, 1, 3, null }, selected.Select(row => row.Int("position")));
    }

    private static JsonObject Row(int id, int rider, int time, string status) => new()
    {
        ["id"] = id, ["rider_id"] = rider, ["race_time_ms"] = time, ["position"] = 9,
        ["status"] = status, ["sessions"] = new JsonObject { ["session_type"] = "Q2" }
    };
}
