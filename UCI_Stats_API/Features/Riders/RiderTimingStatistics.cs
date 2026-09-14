using System.Text.Json.Nodes;
using UCI_Stats_API.Models;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Features.Riders;

/// <summary>Computes rider timing statistics from already-loaded rows; no database access.</summary>
internal static class RiderTimingStatistics
{
    internal static IReadOnlyList<JsonObject> BuildSectorRows(
        IReadOnlyList<JsonObject> splits,
        IReadOnlyList<JsonObject>? results = null)
    {
        var rows = new List<JsonObject>(splits.Count);
        foreach (var group in splits.GroupBy(split => split.String("result_id") ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            int? previous = 0;
            foreach (var split in group.OrderBy(row => row.Int("split_index") ?? int.MaxValue))
            {
                var cumulative = split.Int("race_time");
                var sectorTime = cumulative is not null && previous is not null && cumulative >= previous
                    ? cumulative - previous
                    : null;
                var row = split.DeepClone().AsObject();
                row["sector_time_ms"] = sectorTime;
                rows.Add(row);
                previous = cumulative;
            }
        }

        if (results is null)
        {
            return rows;
        }

        var resultsById = results
            .Where(result => !string.IsNullOrWhiteSpace(result.String("id")))
            .ToDictionary(result => result.String("id")!, StringComparer.OrdinalIgnoreCase);
        var resultSessionIds = resultsById.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.String("session_id") ?? pair.Value.Object("sessions")?.String("id") ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);
        var maximumSplitBySession = splits
            .Where(split => resultSessionIds.ContainsKey(split.String("result_id") ?? string.Empty))
            .GroupBy(split => resultSessionIds.GetValueOrDefault(split.String("result_id") ?? string.Empty))
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToDictionary(
                group => group.Key!,
                group => group.Max(split => split.Int("split_index") ?? 0),
                StringComparer.OrdinalIgnoreCase);

        foreach (var resultGroup in rows
            .GroupBy(row => row.String("result_id") ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToArray())
        {
            if (!resultsById.TryGetValue(resultGroup.Key, out var result) ||
                !maximumSplitBySession.TryGetValue(resultSessionIds[resultGroup.Key], out var maximumSplitIndex))
            {
                continue;
            }

            var tail = resultGroup.FirstOrDefault(row => row.Int("split_index") == maximumSplitIndex);
            var tailTimeMs = tail?.Int("race_time");
            var finishTimeMs = result.Int("race_time_ms");
            if (tailTimeMs is not > 0 || finishTimeMs is not > 0 || finishTimeMs <= tailTimeMs)
            {
                continue;
            }

            rows.Add(new JsonObject
            {
                ["id"] = null,
                ["result_id"] = resultGroup.Key,
                ["split_index"] = maximumSplitIndex + 1,
                ["race_time"] = finishTimeMs,
                ["speed"] = null,
                ["sector_time_ms"] = finishTimeMs.Value - tailTimeMs.Value
            });
        }

        return rows;
    }

    internal static int CountTimingWins(
        IReadOnlyList<JsonObject> timingRows,
        IReadOnlyList<JsonObject> results,
        IReadOnlySet<string> targetResultIds,
        bool useSectorTimes)
    {
        var sessionByResultId = results
            .Where(result => !string.IsNullOrWhiteSpace(result.String("id")))
            .ToDictionary(
                result => result.String("id")!,
                result => result.String("session_id") ?? result.Object("sessions")?.String("id") ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
        var valueField = useSectorTimes ? "sector_time_ms" : "race_time";
        var rankField = useSectorTimes ? "sector_rank" : "split_rank";
        var wins = 0;
        foreach (var group in timingRows
            .Where(row => row.Int(valueField) is > 0)
            .GroupBy(row => new
            {
                SessionId = sessionByResultId.GetValueOrDefault(row.String("result_id") ?? string.Empty),
                Index = row.Int("split_index")
            })
            .Where(group => !string.IsNullOrWhiteSpace(group.Key.SessionId) && group.Key.Index is not null))
        {
            if (group.Any(row => row.Int(rankField) is > 0))
            {
                wins += group.Count(row =>
                    targetResultIds.Contains(row.String("result_id") ?? string.Empty) &&
                    row.Int(rankField) == 1);
                continue;
            }

            var best = group.Min(row => row.Int(valueField)!.Value);
            wins += group.Count(row =>
                targetResultIds.Contains(row.String("result_id") ?? string.Empty) &&
                row.Int(valueField) == best);
        }

        return wins;
    }

}
