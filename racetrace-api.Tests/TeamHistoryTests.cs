using System.Text.Json.Nodes;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class TeamHistoryTests
{
    [Fact]
    public void TeamPeriodMatchesInclusiveBoundariesAndOpenEndedPeriod()
    {
        var history = new[]
        {
            Period("10", "1", "2024-01-01", "2024-12-31"),
            Period("10", "2", "2025-01-01", null),
            Period("20", "3", "2024-01-01", null)
        };

        Assert.Equal("1", DomainQueryService.FindTeamPeriod(
            history,
            "10",
            new DateOnly(2024, 12, 31))?.String("team_id"));
        Assert.Equal("2", DomainQueryService.FindTeamPeriod(
            history,
            "10",
            new DateOnly(2025, 1, 1))?.String("team_id"));
        Assert.Equal("2", DomainQueryService.FindTeamPeriod(
            history,
            "10",
            new DateOnly(2026, 8, 12))?.String("team_id"));
        Assert.Null(DomainQueryService.FindTeamPeriod(
            history,
            "10",
            new DateOnly(2023, 12, 31)));
    }

    [Fact]
    public void TeamHistoryGroupsResultsAndKeepsHistoricalDateRange()
    {
        var results = new[]
        {
            Result("1", "AON", "AON Racing", "2026-06-01"),
            Result("1", "AON", "AON Racing", "2026-07-01"),
            Result("2", "YT2", "YT Mob", "2025-05-01")
        };

        var history = DomainQueryService.BuildTeamHistory(results);

        Assert.Equal(2, history.Count);
        var aon = history[0]!.AsObject();
        Assert.Equal("AON", aon["code"]!.GetValue<string>());
        Assert.Equal(2, aon["results_count"]!.GetValue<int>());
        Assert.Equal("2026-06-01", aon["first_result_date"]!.GetValue<string>());
        Assert.Equal("2026-07-01", aon["last_result_date"]!.GetValue<string>());
    }

    [Fact]
    public void MissingHistoricalPeriodUsesNearestKnownTeam()
    {
        var history = new[]
        {
            Period("10", "1", "2021-08-14", "2023-12-31"),
            Period("10", "2", "2024-01-01", null),
            Period("20", "3", "2020-01-01", null)
        };

        Assert.Equal("1", DomainQueryService.FindTeamPeriodOrNearest(
            history,
            "10",
            new DateOnly(2021, 6, 12))?.String("team_id"));
        Assert.Equal("2", DomainQueryService.FindTeamPeriodOrNearest(
            history,
            "10",
            new DateOnly(2026, 1, 1))?.String("team_id"));
        Assert.Null(DomainQueryService.FindTeamPeriodOrNearest(
            history,
            "30",
            new DateOnly(2021, 6, 12)));
    }

    private static JsonObject Result(string teamId, string code, string name, string date)
    {
        return new JsonObject
        {
            ["teams"] = new JsonObject
            {
                ["id"] = teamId,
                ["code"] = code,
                ["name"] = name
            },
            ["sessions"] = new JsonObject
            {
                ["session_date"] = date
            }
        };
    }

    private static JsonObject Period(
        string riderId,
        string teamId,
        string validFrom,
        string? validTo) => new()
    {
        ["rider_id"] = riderId,
        ["team_id"] = teamId,
        ["valid_from"] = validFrom,
        ["valid_to"] = validTo
    };
}
