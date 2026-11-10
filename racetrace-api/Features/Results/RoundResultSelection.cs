using System.Text.Json.Nodes;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.Features.Results;

internal static class RoundResultSelection
{
    internal static string[] Categories(string category) =>
        category.ToLowerInvariant() switch
        {
            "open_men" => ["elite_men", "junior_men"],
            "open_women" => ["elite_women", "junior_women"],
            _ => [category],
        };

    internal static JsonObject[] Select(IEnumerable<JsonObject> rows, bool bestOnly)
    {
        var ordered = rows.OrderBy(row => SessionResultMapper.IsCompletedResult(row) ? 0 : 1)
            .ThenBy(row => row.Int("race_time_ms") ?? int.MaxValue)
            .ThenBy(row => row.String("id"), StringComparer.Ordinal)
            .ToArray();
        var selected = (
            bestOnly
                ? ordered
                    .Where(SessionResultMapper.IsCompletedResult)
                    .DistinctBy(row => row.String("rider_id") ?? row.String("id"))
                : ordered
        )
            .Select(row => (JsonObject)row.DeepClone())
            .ToArray();
        int? previousTime = null;
        var position = 0;
        for (var index = 0; index < selected.Length; index++)
        {
            var row = selected[index];
            if (!SessionResultMapper.IsCompletedResult(row))
            {
                row["position"] = null;
                continue;
            }
            var time = row.Int("race_time_ms");
            if (time != previousTime)
                position = index + 1;
            row["position"] = position;
            previousTime = time;
        }
        return selected;
    }
}
