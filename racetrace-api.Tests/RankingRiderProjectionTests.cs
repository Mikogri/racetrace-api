using System.Text.Json.Nodes;
using RaceTrace.Api.Features.Ranking;
using RaceTrace.Api.Features.Riders;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class RankingRiderProjectionTests
{
    [Fact]
    public void CanonicalRanksRemainIndependentOfDisplayDirectionAndBreakTiesByNumericId()
    {
        JsonObject[] rows =
        [
            new()
            {
                ["rider_id"] = "10",
                ["rider_name"] = "Same",
                ["points"] = 100,
            },
            new()
            {
                ["rider_id"] = "2",
                ["rider_name"] = "Same",
                ["points"] = 100,
            },
            new()
            {
                ["rider_id"] = "3",
                ["rider_name"] = "Other",
                ["points"] = 50,
            },
        ];

        RankingResponseProjection.AssignCanonicalPositions(rows, "rank");
        var displayed = RankingResponseProjection.SortRows(rows, "rank", descending: true);

        Assert.Equal(new[] { "3", "10", "2" }, displayed.Select(row => row.String("rider_id")));
        Assert.Equal(new int?[] { 3, 2, 1 }, displayed.Select(row => row.Int("rank")));
    }

    [Fact]
    public void RankingProjectionRetainsParticipationFractionAndPositionChange()
    {
        var row = new JsonObject
        {
            ["rider_id"] = "2",
            ["rank"] = 3,
            ["quali_rate"] = 66.67m,
            ["rider"] = new JsonObject { ["first_name"] = "Ada", ["last_name"] = "Rider" },
        };

        var result = RankingResponseProjection.ToPublicDto(row, new JsonObject { ["rank"] = 5 });

        Assert.Equal(2, result.PositionChange);
        Assert.Equal(0.6667m, result.QualifyingRate);
        Assert.Equal(3, result.Position);
    }

    [Fact]
    public void RoundProjectionUsesFirstPositiveSessionPositionAndSumsAllSessionPoints()
    {
        JsonObject Result(int position, decimal points) =>
            new()
            {
                ["position"] = position,
                ["awarded_points"] = points,
                ["sessions"] = new JsonObject { ["session_type"] = "FINAL" },
            };

        var result = RiderStatisticsProjection.ToRoundResult(
            "9",
            [Result(0, 5), Result(2, 10)],
            null
        );

        Assert.Equal(9, result.RoundId);
        Assert.Equal(2, result.FinalPosition);
        Assert.Equal(15m, result.AwardedPoints);
        Assert.Null(result.Rating);
        Assert.Equal(0m, result.Confidence);
    }

    [Fact]
    public void TrackProjectionAveragesOnlyPositivePositionsOfTheRequestedSessionType()
    {
        JsonObject Result(string type, int position) =>
            new()
            {
                ["position"] = position,
                ["sessions"] = new JsonObject { ["session_type"] = type },
            };
        JsonObject[] results =
        [
            Result("FINAL", 0),
            Result("FINAL", 2),
            Result("FINAL", 5),
            Result("QUALI", 1),
        ];

        Assert.Equal(3.5m, RiderStatisticsProjection.AveragePositionForType(results, "final"));
        Assert.Null(RiderStatisticsProjection.AveragePositionForType(results, "semi_final"));
    }
}
