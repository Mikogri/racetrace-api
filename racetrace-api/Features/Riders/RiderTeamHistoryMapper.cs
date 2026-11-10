using System.Text.Json.Nodes;
using RaceTrace.Api.Features.Metrics;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Features.Riders;

/// <summary>Preserves both legacy embedded-team and public overview wire shapes, including ID types and nulls.</summary>
internal static class RiderTeamHistoryMapper
{
    internal static JsonArray BuildTeamHistory(IEnumerable<JsonObject> results) =>
        BuildTeamHistory(results.Select(RankingReadResult.FromLegacy));

    internal static JsonArray BuildTeamHistory(IEnumerable<RankingReadResult> results)
    {
        var teams = results
            .Where(result => result.Object("teams") is not null)
            .GroupBy(
                result => result.Object("teams")!.String("id") ?? string.Empty,
                StringComparer.OrdinalIgnoreCase
            )
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group =>
            {
                var rows = group.ToArray();
                var team = rows[0].Object("teams")!.DeepClone().AsObject();
                var dates = rows.Select(ResultDate)
                    .Where(date => !string.IsNullOrWhiteSpace(date))
                    .Select(date => date!)
                    .OrderBy(date => date, StringComparer.Ordinal)
                    .ToArray();

                team["results_count"] = rows.Length;
                team["first_result_date"] = dates.FirstOrDefault();
                team["last_result_date"] = dates.LastOrDefault();
                return team;
            })
            .OrderByDescending(team => team.String("last_result_date"), StringComparer.Ordinal)
            .ThenBy(team => team.String("name"), StringComparer.OrdinalIgnoreCase)
            .Select(team => (JsonNode)team)
            .ToArray();

        return new JsonArray(teams);
    }

    internal static string? ResultDate(JsonObject result) =>
        ResultDate(RankingReadResult.FromLegacy(result));

    private static string? ResultDate(RankingReadResult result)
    {
        var session = result.Object("sessions");
        return session?.String("session_date") ?? session?.Object("rounds")?.String("start_date");
    }

    internal static JsonArray ToTeamHistoryJson(IEnumerable<JsonObject> history)
    {
        return new JsonArray(
            history
                .OrderByDescending(row => row.String("valid_from"), StringComparer.Ordinal)
                .Select(row =>
                {
                    var team =
                        row.Object("teams")?.DeepClone().AsObject()
                        ?? new JsonObject { ["id"] = row["team_id"]?.DeepClone() };
                    team["history_id"] = row["id"]?.DeepClone();
                    team["valid_from"] = row["valid_from"]?.DeepClone();
                    team["valid_to"] = row["valid_to"]?.DeepClone();
                    team["is_current"] = row["valid_to"] is null;
                    team["source_round_id"] = row["source_round_id"]?.DeepClone();
                    return (JsonNode)team;
                })
                .ToArray()
        );
    }

    internal static JsonArray ToPublicTeamHistoryJson(IEnumerable<JsonObject> history) =>
        new(
            history
                .Select(row =>
                    (JsonNode)
                        new JsonObject
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
                        }
                )
                .ToArray()
        );
}
