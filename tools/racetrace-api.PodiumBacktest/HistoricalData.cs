using System.Globalization;
using System.Text.Json.Nodes;
using RaceTrace.Api.Models;
using RaceTrace.Api.Services;

namespace RaceTrace.Api.PodiumBacktest;

internal sealed record HistoricalRound(
    JsonObject Round,
    RankingDataBundle Data,
    IReadOnlyList<RiderSessionPaceObservation> Observations
)
{
    public string Id => Round.String("id")!;
    public int Year => Round.Int("season_year")!.Value;
    public int Number => Round.Int("round_number") ?? 0;
    public DateTimeOffset Start =>
        DateTimeOffset.Parse(Round.String("start_date")!, CultureInfo.InvariantCulture);
    public string TrackId => Round.String("track_id")!;
    public bool WorldCup => Round.Object("competition_levels")?.String("code") == "world_cup";
    public string Venue => Round.Object("tracks")?.String("venue") ?? TrackId;
}

internal static class HistoricalData
{
    public static RiderMetricsContext Context(string? roundId = null) =>
        new(null, null, null, null, roundId, null, null, null, null, null, null)
        {
            CategoryCodes = ["EM", "EW"],
        };

    public static string Category(JsonObject session) =>
        session.Object("round_categories")?.Object("categories")?.String("code")
        ?? session.Object("categories")?.String("code")
        ?? "";

    public static DateTimeOffset SessionDate(JsonObject session) =>
        DateTimeOffset.TryParse(
            session.String("session_date"),
            CultureInfo.InvariantCulture,
            out var date
        )
            ? date
            : throw new InvalidOperationException(
                $"Session {session.String("id")} has no date; cannot guarantee cutoff."
            );

    public static RankingDataBundle Combine(
        IEnumerable<HistoricalRound> rounds,
        string category,
        DateTimeOffset cutoff
    )
    {
        // Filter both the round and session dates. Never select history from target results.
        var selected = rounds.Where(round => round.Start < cutoff).ToArray();
        var sessions = selected
            .SelectMany(round => round.Data.Sessions)
            .Where(session => Category(session) == category && SessionDate(session) < cutoff)
            .ToArray();
        var ids = sessions.Select(session => session.String("id")!).ToHashSet();
        var results = selected
            .SelectMany(round => round.Data.Results)
            .Where(result => ids.Contains(result.String("session_id")!))
            .ToArray();
        var resultIds = results.Select(result => result.String("id")!).ToHashSet();
        return new RankingDataBundle(
            sessions,
            results,
            selected
                .SelectMany(round => round.Data.FieldResults)
                .Where(result => ids.Contains(result.String("session_id")!))
                .ToArray(),
            selected
                .SelectMany(round => round.Data.Splits)
                .Where(split => resultIds.Contains(split.String("result_id")!))
                .ToArray(),
            selected
                .SelectMany(round => round.Data.PointsByResultId)
                .Where(pair => resultIds.Contains(pair.Key))
                .ToDictionary()
        );
    }

    public static IReadOnlyList<RiderSessionPaceObservation> Observations(
        IEnumerable<HistoricalRound> rounds,
        RankingDataBundle history
    )
    {
        var ids = history.Sessions.Select(session => session.String("id")!).ToHashSet();
        return rounds
            .SelectMany(round => round.Observations)
            .Where(value => ids.Contains(value.SessionId))
            .ToArray();
    }
}
