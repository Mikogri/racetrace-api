using System.Globalization;
using System.Text.Json.Nodes;
using UCI_Stats_API.Models;
using UCI_Stats_API.Services;

namespace UCI_Stats_API.Features.Riders;

internal static class RiderChartStatistics
{
    internal static RiderChartStatisticsDto Build(
        IEnumerable<JsonObject> results,
        IReadOnlyList<RiderRoundResultDto> rounds,
        IReadOnlyList<RiderTrackStatisticsDto> tracks)
    {
        // Match the classified sessions rendered by the rider Results chart.
        var classified = results.Where(result =>
            result.String("id") is not null &&
            result.Object("sessions")?.String("id") is not null &&
            result.Object("sessions")?.Object("rounds")?.String("id") is not null &&
            result.Bool("is_classified") == true && result.Int("position") > 0 &&
            HasClassifiedStatus(result.String("status"))).ToArray();
        var points = new Dictionary<string, decimal>();
        decimal cumulative = 0;
        foreach (var round in rounds.OrderBy(round =>
            $"{round.RoundStartDate:yyyy-MM-dd}-{round.RoundNumber.GetValueOrDefault():00}-{round.RoundId}", StringComparer.Ordinal))
        {
            cumulative += round.AwardedPoints;
            points[round.RoundId.ToString(CultureInfo.InvariantCulture)] = cumulative;
        }
        var positionsByTrack = classified.Where(result => !IsFinalSession(result.Object("sessions")!))
            .GroupBy(result => result.Object("sessions")?.Object("rounds")?.String("track_id") ??
                result.Object("sessions")?.Object("rounds")?.Object("tracks")?.String("id") ?? "")
            .ToDictionary(group => group.Key, group => group.Average(result => (decimal)result.Int("position")!.Value));
        var trackAverages = tracks.ToDictionary(
            track => track.TrackId.ToString(CultureInfo.InvariantCulture),
            track => positionsByTrack.TryGetValue(track.TrackId.ToString(CultureInfo.InvariantCulture), out var average)
                ? (decimal?)average : track.AverageQualifyingPosition);
        return new RiderChartStatisticsDto(
            classified.Select(result => (decimal?)result.Int("position")).Average(), points, trackAverages);
    }

    private static string Normalize(string? value) =>
        new((value ?? "").Where(character => char.IsAsciiLetterOrDigit(character)).Select(char.ToUpperInvariant).ToArray());

    private static bool HasClassifiedStatus(string? status) => Normalize(status) is not
        ("" or "DNS" or "DIDNOTSTART" or "DNF" or "DIDNOTFINISH" or "DSQ" or "DISQUALIFIED" or
         "DNQ" or "NOTQUALIFIED" or "CANCELLED" or "CANCELED" or "MISSING");

    private static bool IsFinalSession(JsonObject session) =>
        Normalize(session.String("session_type") ?? session.String("name")) is "F" or "FINAL" or "FINALS" or "FINALRUN";
}
