using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Features.Ranking;

internal static class RankingResponseProjection
{
    internal static JsonObject[] SortRows(
        IReadOnlyList<JsonObject> rows,
        string sort,
        bool descending
    )
    {
        IOrderedEnumerable<JsonObject> ordered;
        if (
            sort
            is "rating"
                or "pace"
                or "consistency"
                or "confidence"
                or "finals_count"
                or "quali_rate"
                or "points"
                or "avg_position"
                or "wins"
                or "podiums"
                or "top_10"
                or "top_20"
                or "top_30"
                or "starts"
                or "final_starts"
        )
        {
            ordered = descending
                ? rows.OrderByDescending(row => row.Decimal(sort) ?? decimal.MinValue)
                : rows.OrderBy(row => row.Decimal(sort) ?? decimal.MaxValue);
        }
        else if (sort == "rank")
        {
            ordered = descending
                ? rows.OrderByDescending(row => row.Int("rank") ?? int.MinValue)
                : rows.OrderBy(row => row.Int("rank") ?? int.MaxValue);
        }
        else
        {
            ordered = descending
                ? rows.OrderByDescending(
                    row => row.String(sort) ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase
                )
                : rows.OrderBy(
                    row => row.String(sort) ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase
                );
        }

        return ordered
            .ThenBy(
                row => row.String("rider_name") ?? string.Empty,
                StringComparer.OrdinalIgnoreCase
            )
            .ThenBy(row => PublicDtoMapping.Long(row.String("rider_id")))
            .ToArray();
    }

    internal static void AssignCanonicalPositions(
        IReadOnlyList<JsonObject> rows,
        string requestedSort
    )
    {
        var sort = requestedSort == "rank" ? "points" : requestedSort;
        var bestIsDescending =
            sort is not ("avg_position" or "rider_name" or "nation" or "team_name");
        var canonical = SortRows(rows, sort, bestIsDescending);
        for (var index = 0; index < canonical.Length; index++)
        {
            canonical[index]["rank"] = index + 1;
        }
    }

    internal static RankingRiderDto ToPublicDto(JsonObject row, JsonObject? previousRow = null)
    {
        var rider = row.Object("rider") ?? new JsonObject();
        var team = rider.Array("teams")?.OfType<JsonObject>().FirstOrDefault();
        return new RankingRiderDto(
            row.Int("rank") ?? 0,
            PublicDtoMapping.Long(row.String("rider_id")),
            rider.String("uci_id"),
            rider.String("first_name"),
            rider.String("last_name"),
            rider.DisplayName(),
            rider.String("nation"),
            PublicDtoMapping.NullableLong(team?.String("id")),
            team?.String("name") ?? row.String("team_name"),
            row.Decimal("points") ?? 0m,
            row.Decimal("rating"),
            row.Decimal("pace"),
            row.Decimal("consistency"),
            row.Decimal("confidence") ?? 0m,
            row.Int("finals_count") ?? 0,
            Math.Round((row.Decimal("quali_rate") ?? 0m) / 100m, 4, MidpointRounding.AwayFromZero),
            rider.String("profile_image_url")
        )
        {
            ConsistencyEvidence = row.Decimal("consistency_confidence") ?? 0m,
            PositionChange = previousRow is null
                ? null
                : (previousRow.Int("rank") ?? 0) - (row.Int("rank") ?? 0),
        };
    }
}
