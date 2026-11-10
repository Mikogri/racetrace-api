using System.Text.Json.Nodes;
using RaceTrace.Api.Features.Riders;

namespace RaceTrace.Api.Tests;

public sealed class RiderTeamHistoryMappingTests
{
    [Fact]
    public void OverviewPreservesRawIdsNullsAndOrderWithoutMutatingInput()
    {
        var row = JsonNode
            .Parse(
                """
                {"id":"12","rider_id":7,"team_id":8,"valid_from":"2025-04-10","valid_to":null,
                 "source_round_id":null,"teams":{"id":8,"name":"Factory","code":"FW5"}}
                """
            )!
            .AsObject();
        var before = row.ToJsonString();
        var actual = RiderTeamHistoryMapper.ToPublicTeamHistoryJson([row]);
        var expected = JsonNode.Parse(
            """
            [{"id":"12","riderId":7,"teamId":8,"teamName":"Factory","teamCode":"FW5",
              "validFrom":"2025-04-10","validTo":null,"isCurrent":true,"sourceRoundId":null}]
            """
        );
        Assert.True(JsonNode.DeepEquals(expected, actual));
        Assert.Equal(before, row.ToJsonString());
    }

    [Fact]
    public void LegacyEmbeddingRetainsAdditionalTeamFieldsAndSortsNewestFirst()
    {
        var older = JsonNode
            .Parse(
                """
                {"id":1,"team_id":8,"valid_from":"2023-01-01","valid_to":"2024-01-01",
                 "teams":{"id":8,"name":"Factory","extra":"preserve"}}
                """
            )!
            .AsObject();
        var newer = JsonNode
            .Parse(
                """
                {"id":2,"team_id":9,"valid_from":"2025-01-01","valid_to":null}
                """
            )!
            .AsObject();
        var actual = RiderTeamHistoryMapper.ToTeamHistoryJson([older, newer]);
        Assert.Equal(9, actual[0]!["id"]!.GetValue<int>());
        Assert.Equal("preserve", actual[1]!["extra"]!.GetValue<string>());
        Assert.False(actual[1]!["is_current"]!.GetValue<bool>());
    }
}
