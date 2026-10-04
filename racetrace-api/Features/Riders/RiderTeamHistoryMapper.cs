using System.Text.Json.Nodes;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Features.Riders;

/// <summary>Preserves both legacy embedded-team and public overview wire shapes, including ID types and nulls.</summary>
internal static class RiderTeamHistoryMapper
{
    internal static JsonArray ToTeamHistoryJson(IEnumerable<JsonObject> history)
    {
        return new JsonArray(history
            .OrderByDescending(row => row.String("valid_from"), StringComparer.Ordinal)
            .Select(row =>
            {
                var team = row.Object("teams")?.DeepClone().AsObject() ??
                    new JsonObject
                    {
                        ["id"] = row["team_id"]?.DeepClone()
                    };
                team["history_id"] = row["id"]?.DeepClone();
                team["valid_from"] = row["valid_from"]?.DeepClone();
                team["valid_to"] = row["valid_to"]?.DeepClone();
                team["is_current"] = row["valid_to"] is null;
                team["source_round_id"] = row["source_round_id"]?.DeepClone();
                return (JsonNode)team;
            })
            .ToArray());
    }

    internal static JsonArray ToPublicTeamHistoryJson(IEnumerable<JsonObject> history) =>
        new JsonArray(history.Select(row => (JsonNode)new JsonObject
        {
            ["id"] = row["id"]?.DeepClone(),
            ["riderId"] = row["rider_id"]?.DeepClone(),
            ["teamId"] = row["team_id"]?.DeepClone(),
            ["teamName"] = row.Object("teams")?.String("name"),
            ["teamCode"] = row.Object("teams")?.String("code"),
            ["validFrom"] = row["valid_from"]?.DeepClone(),
            ["validTo"] = row["valid_to"]?.DeepClone(),
            ["isCurrent"] = row["valid_to"] is null,
            ["sourceRoundId"] = row["source_round_id"]?.DeepClone(),
        }).ToArray());

}
