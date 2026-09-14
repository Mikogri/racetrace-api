using System.Text.Json.Nodes;
using UCI_Stats_API.Models;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Features.Results;

/// <summary>Boundary mapping: stored timing values are milliseconds, public timing DTOs expose seconds.</summary>
internal static class SessionResultMapper
{
    internal static SessionResultDto ToPublicResult(
        JsonObject result,
        string timeView,
        int? rankingPosition,
        decimal? trackLengthM = null)
    {
        var rider = result.Object("riders") ?? new JsonObject();
        var team = result.Object("teams");
        var timingRows = result.Array(timeView)?.OfType<JsonObject>().ToArray() ?? [];
        var times = timingRows.Select(row =>
        {
            var index = row.Int("split_index") ?? 0;
            var timeMs = timeView == TimingModeSectors ? row.Int("sector_time_ms") : row.Int("race_time_ms");
            var gapMs = timeView == TimingModeSectors
                ? row.Int("sector_gap_to_best_ms") ?? row.Int("sector_gap_to_leader_ms")
                : row.Int("split_gap_to_best_ms") ?? row.Int("split_gap_to_leader_ms");
            var position = timeView == TimingModeSectors ? row.Int("sector_position") : row.Int("split_position");
            return new ResultTimeDto(
                index,
                $"{(timeView == TimingModeSectors ? "Sector" : "Split")} {index}",
                Seconds(timeMs),
                Seconds(gapMs),
                position);
        }).ToArray();

        return new SessionResultDto(
            rankingPosition,
            ParseLong(result.String("id")),
            ParseLong(result.String("rider_id") ?? rider.String("id")),
            rider.String("uci_id"),
            rider.String("first_name"),
            rider.String("last_name"),
            rider.DisplayName(),
            rider.String("nation"),
            NullableLong(result.String("team_id") ?? team?.String("id")),
            team?.String("name"),
            result.String("status")?.ToLowerInvariant(),
            result.Decimal("top_speed"),
            CalculateAverageSpeedKmh(
                trackLengthM,
                result.Int("race_time_ms")),
            Seconds(result.Int("race_time_ms")),
            Seconds(result.Int("gap_ms")),
            result.Decimal("awarded_points") ?? 0m,
            times);
    }

    internal static decimal? CalculateAverageSpeedKmh(
        decimal? trackLengthM,
        int? raceTimeMs)
    {
        if (trackLengthM is not > 0 || raceTimeMs is not > 0)
        {
            return null;
        }

        return Math.Round(
            trackLengthM.Value * 3600m / raceTimeMs.Value,
            3);
    }

    private const string TimingModeSectors = "sectors";

    internal static decimal? Seconds(int? milliseconds) =>
        milliseconds is null ? null : Math.Round(milliseconds.Value / 1000m, 3);

    internal static long ParseLong(string? value) => long.TryParse(value, out var parsed) ? parsed : 0;

    internal static long? NullableLong(string? value) => long.TryParse(value, out var parsed) ? parsed : null;
    internal static bool IsCompletedResult(JsonObject result)
    {
        var status = result.String("status");
        return result.Int("race_time_ms") is > 0 &&
            (result.Bool("is_classified") == true || result.Int("position") is > 0) &&
            !string.Equals(status, "DNF", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "DNS", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "DSQ", StringComparison.OrdinalIgnoreCase);
    }

}
