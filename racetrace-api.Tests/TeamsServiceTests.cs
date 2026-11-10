using System.Text.Json.Nodes;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class TeamsServiceTests
{
    [Fact]
    public void TeamsContainRidersCategoriesAndAverageRating()
    {
        var teams = new[]
        {
            new JsonObject
            {
                ["id"] = "10",
                ["code"] = "AON",
                ["name"] = "AON Racing",
            },
        };
        var results = new[]
        {
            Result("10", "1", "Max", "Alran", "1", "EM", "Elite Men"),
            Result("10", "1", "Max", "Alran", "1", "EM", "Elite Men"),
            Result("10", "1", "Max", "Alran", "2", "MJ", "Junior Men"),
            Result("10", "2", "Till", "Alran", "1", "EM", "Elite Men"),
        };
        var performance = new Dictionary<string, RiderPerformanceMetricValues>
        {
            ["1"] = Metric(90.12m),
            ["2"] = Metric(70.00m),
        };

        TeamsService.DecorateTeams(teams, results, performance);

        Assert.Equal(2, teams[0]["riders_count"]!.GetValue<int>());
        Assert.Equal(80.06m, teams[0]["average_rating"]!.GetValue<decimal>());
        var riders = teams[0]["riders"]!.AsArray();
        Assert.Equal(2, riders.Count);
        Assert.Equal("Max Alran", riders[0]!["display_name"]!.GetValue<string>());
        Assert.Equal(2, riders[0]!["categories"]!.AsArray().Count);
        Assert.Equal("EM", riders[0]!["categories"]![0]!["code"]!.GetValue<string>());
    }

    [Fact]
    public void TeamWithoutResultsHasEmptyRidersAndNullAverage()
    {
        var team = new JsonObject { ["id"] = "10" };

        TeamsService.DecorateTeams(
            [team],
            [],
            new Dictionary<string, RiderPerformanceMetricValues>()
        );

        Assert.Empty(team["riders"]!.AsArray());
        Assert.Equal(0, team["riders_count"]!.GetValue<int>());
        Assert.Null(team["average_rating"]);
    }

    [Fact]
    public void CurrentTeamMembershipComesFromHistoryNotOldResultTeam()
    {
        var teams = new[]
        {
            new JsonObject { ["id"] = "10" },
            new JsonObject { ["id"] = "20" },
        };
        var allResults = new[]
        {
            Result("10", "1", "Max", "Alran", "1", "EM", "Elite Men"),
            Result("20", "1", "Max", "Alran", "1", "EM", "Elite Men"),
        };
        var assignments = new[]
        {
            new JsonObject
            {
                ["rider_id"] = "1",
                ["team_id"] = "20",
                ["valid_from"] = "2026-01-01",
            },
        };

        TeamsService.DecorateTeamsFromHistory(
            teams,
            assignments,
            allResults,
            new Dictionary<string, RiderPerformanceMetricValues> { ["1"] = Metric(90m) }
        );

        Assert.Equal(0, teams[0]["riders_count"]!.GetValue<int>());
        Assert.Equal(1, teams[1]["riders_count"]!.GetValue<int>());
        Assert.Equal("Max Alran", teams[1]["riders"]![0]!["display_name"]!.GetValue<string>());
    }

    private static JsonObject Result(
        string teamId,
        string riderId,
        string firstName,
        string lastName,
        string categoryId,
        string categoryCode,
        string categoryName
    )
    {
        return new JsonObject
        {
            ["team_id"] = teamId,
            ["rider_id"] = riderId,
            ["riders"] = new JsonObject
            {
                ["id"] = riderId,
                ["uci_id"] = $"100{riderId}",
                ["first_name"] = firstName,
                ["last_name"] = lastName,
            },
            ["sessions"] = new JsonObject
            {
                ["categories"] = new JsonObject
                {
                    ["id"] = categoryId,
                    ["code"] = categoryCode,
                    ["name"] = categoryName,
                },
            },
        };
    }

    private static RiderPerformanceMetricValues Metric(decimal rating)
    {
        return new RiderPerformanceMetricValues(rating, rating, 80m, 78m, 1m, 5, 80m, 1m, 5, 20);
    }
}
