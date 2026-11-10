using System.Text.Json.Nodes;
using RaceTrace.Api.Features.Results;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class StageTwoResponseNormalizationTests
{
    [Fact]
    public void RoundExposesCompetitionClassAndPriority()
    {
        var round = new JsonObject
        {
            ["id"] = "10",
            ["competition_levels"] = new JsonObject
            {
                ["id"] = "2",
                ["code"] = "world_cup",
                ["name"] = "World Cup",
                ["priority"] = 2,
            },
        };

        ResultEmbedMapping.NormalizeRoundEmbed(round);

        Assert.Equal("world_cup", round["competition_class"]!["code"]!.GetValue<string>());
        Assert.Equal(2, round["competition_class_priority"]!.GetValue<int>());
        Assert.False(round.ContainsKey("competition_level_id"));
        Assert.False(round.ContainsKey("competition_levels"));
    }

    [Fact]
    public void ResultExposesAwardedPointsAndAppliedRuleDiagnostics()
    {
        var result = new JsonObject
        {
            ["id"] = "100",
            ["result_points"] = new JsonArray
            {
                new JsonObject
                {
                    ["points_awarded"] = 50,
                    ["points_system_id"] = "7",
                    ["point_system_stage_id"] = "8",
                    ["point_rule_id"] = "9",
                },
            },
        };

        ResultEmbedMapping.NormalizeResultEmbeds([result]);

        Assert.Equal(50m, result["awarded_points"]!.GetValue<decimal>());
        Assert.Equal("7", result["applied_point_system_id"]!.GetValue<string>());
        Assert.Equal("8", result["applied_point_system_stage_id"]!.GetValue<string>());
        Assert.Equal("9", result["applied_point_rule_id"]!.GetValue<string>());
    }
}
