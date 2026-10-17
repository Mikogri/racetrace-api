using RaceTrace.Api.Features.Metrics;
using RaceTrace.Api.Features.Riders;
using System.Text.Json.Nodes;
using RaceTrace.Api.Infrastructure;
using RaceTrace.Api.Models;

using RaceTrace.Api.Services;

namespace RaceTrace.Api.Features.Riders;

internal static class RiderStatisticsProjection
{
    internal static SessionPositionDto ToSessionPosition(JsonObject result)
    {
        var session = result.Object("sessions");
        return new SessionPositionDto(
            PublicDtoMapping.Long(session?.String("round_id") ?? session?.Object("rounds")?.String("id")),
            PublicDtoMapping.Long(result.String("session_id") ?? session?.String("id")),
            PublicApiCodes.ToPublicSessionType(session?.String("session_type")) ?? "unknown",
            result.Int("position"));
    }

    internal static JsonObject[] DistinctSessions(IEnumerable<JsonObject> results)
    {
        return results
            .Select(result => result.Object("sessions"))
            .Where(session => session?.String("id") is not null)
            .Select(session => session!)
            .GroupBy(session => session.String("id")!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    internal static RiderPerformanceMetricValues PerformanceFromRankingRow(JsonObject row) =>
        new(
            row.Decimal("rating"),
            row.Decimal("raw_rating"),
            row.Decimal("pace"),
            row.Decimal("raw_pace"),
            row.Decimal("pace_confidence") ?? 0m,
            row.Int("pace_used_sessions") ?? 0,
            row.Decimal("consistency"),
            row.Decimal("consistency_confidence") ?? 0m,
            row.Int("consistency_used_runs") ?? 0,
            row.Int("consistency_used_sectors") ?? 0)
        {
            PaceConfidenceStatus = row.String("pace_confidence_status") ?? "Low",
            PaceUsedFinals = row.Int("pace_used_finals") ?? 0,
            PaceTotalEvidence = row.Decimal("pace_total_evidence") ?? 0m,
            PaceFinalEvidence = row.Decimal("pace_final_evidence") ?? 0m,
            ResultScore = row.Decimal("result_score"),
            ResultScoreConfidence = row.Decimal("result_score_confidence") ?? 0m,
            ResultScoreUsedSessions = row.Int("result_score_used_sessions") ?? 0
        };

    internal static RiderStatisticsDto EmptyPublicStatistics(
        string riderId,
        CurrentRiderPresentation presentation) =>
        new(
            PublicDtoMapping.Long(riderId),
            presentation.Rider.String("uci_id"),
            presentation.Rider.String("first_name"),
            presentation.Rider.String("last_name"),
            presentation.Rider.DisplayName(),
            presentation.Rider.String("profile_image_url"),
            presentation.Rider.String("nation"),
            presentation.Rider.String("gender"),
            PublicDtoMapping.NullableLong(presentation.Team?.String("id")),
            presentation.Team?.String("name"),
            presentation.Team?.String("code"),
            presentation.CurrentCategory,
            presentation.Age,
            null,
            null,
            null,
            0m,
            null,
            0m,
            null,
            [],
            [],
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            new RiderSpeedDto(
                new SpeedGroupDto(null, null),
                new SpeedGroupDto(null, null)),
            null,
            null,
            null,
            null,
            null,
            [],
            null);

    internal static string? SessionType(JsonObject result) => result.Object("sessions")?.String("session_type");

    internal static string ResultRoundId(JsonObject result)
    {
        var session = result.Object("sessions");
        return session?.String("round_id") ?? session?.Object("rounds")?.String("id") ?? string.Empty;
    }

    internal static DateOnly? ResultDate(JsonObject result)
    {
        var session = result.Object("sessions");
        return PublicDtoMapping.Date(session?.String("session_date") ?? session?.Object("rounds")?.String("start_date"));
    }

    internal static decimal? AveragePosition(IEnumerable<JsonObject> results)
    {
        var positions = results
            .Select(result => result.Int("position"))
            .Where(position => position is > 0)
            .Select(position => (decimal)position!.Value)
            .ToArray();
        return positions.Length == 0 ? null : Math.Round(positions.Average(), 2);
    }

    internal static decimal? AveragePositionForType(IEnumerable<JsonObject> results, string publicSessionType)
    {
        return AveragePosition(results.Where(result =>
            string.Equals(
                PublicApiCodes.ToPublicSessionType(SessionType(result)),
                publicSessionType,
                StringComparison.OrdinalIgnoreCase)));
    }

    internal static SpeedGroupDto BuildSpeedGroup(
        IReadOnlyList<JsonObject> results,
        decimal[] splitSpeeds)
    {
        var topSpeeds = results
            .Select(result => result.Decimal("top_speed"))
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();
        return new SpeedGroupDto(
            topSpeeds.Length == 0 ? null : topSpeeds.Max(),
            splitSpeeds.Length == 0 ? null : Math.Round(splitSpeeds.Average(), 3));
    }

    internal static bool IsCompleted(JsonObject result)
    {
        var status = result.String("status");
        return result.Int("race_time_ms") is > 0 &&
            !string.Equals(status, "DNF", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "DNS", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "DSQ", StringComparison.OrdinalIgnoreCase);
    }

    internal static decimal? AverageSeconds(IEnumerable<int?> milliseconds)
    {
        var values = milliseconds.Where(value => value is > 0).Select(value => value!.Value).ToArray();
        return values.Length == 0 ? null : Math.Round(values.Average(value => (decimal)value) / 1000m, 3);
    }

    internal static RiderRoundResultDto ToRoundResult(
        string roundId,
        JsonObject[] results,
        RiderPerformanceMetricValues? performance)
    {
        var session = results[0].Object("sessions");
        var round = session?.Object("rounds");
        var track = round?.Object("tracks");
        var competitionClass = round?.Object("competition_levels") ?? round?.Object("competition_class");
        var condition = round?.Object("conditions") ?? session?.Object("conditions");
        var trackId = PublicDtoMapping.NullableLong(round?.String("track_id") ?? track?.String("id"));

        int? Position(string sessionType) => results
            .Where(result => string.Equals(PublicApiCodes.ToPublicSessionType(SessionType(result)), sessionType, StringComparison.OrdinalIgnoreCase))
            .Select(result => result.Int("position"))
            .FirstOrDefault(position => position is > 0);

        return new RiderRoundResultDto(
            PublicDtoMapping.Long(roundId),
            round?.Int("round_number"),
            round?.Int("season_year"),
            PublicDtoMapping.NullableLong(round?.String("competition_level_id") ?? round?.String("competition_class_id") ?? competitionClass?.String("id")),
            competitionClass?.String("code"),
            competitionClass?.String("name"),
            PublicDtoMapping.Condition(condition),
            PublicDtoMapping.Date(round?.String("start_date")),
            trackId,
            track?.String("name"),
            trackId,
            track?.String("venue"),
            track?.String("country_code"),
            performance?.Rating,
            performance?.Pace,
            performance?.Consistency,
            performance?.PaceConfidence ?? 0m,
            Position("qualifying"),
            Position("qualifying_1"),
            Position("qualifying_2"),
            Position("semi_final"),
            Position("final"),
            results.Sum(result => result.Decimal("awarded_points") ?? 0m));
    }

    internal static RiderTrackStatisticsDto ToTrackStatistics(
        string trackId,
        JsonObject[] results,
        RiderPerformanceMetricValues? performance)
    {
        var track = results[0].Object("sessions")?.Object("rounds")?.Object("tracks");
        var id = PublicDtoMapping.Long(trackId);
        return new RiderTrackStatisticsDto(
            id,
            track?.String("name"),
            id,
            track?.String("venue"),
            track?.String("country_code"),
            performance?.Rating,
            performance?.Pace,
            performance?.Consistency,
            performance?.PaceConfidence ?? 0m,
            AveragePositionForType(results, "qualifying"),
            AveragePositionForType(results, "qualifying_1"),
            AveragePositionForType(results, "qualifying_2"),
            AveragePositionForType(results, "semi_final"),
            AveragePositionForType(results, "final"),
            results.Length);
    }

    internal static RiderRoundResultDto[] SortRoundResults(
        IReadOnlyList<RiderRoundResultDto> rows,
        string sort,
        bool descending)
    {
        Func<RiderRoundResultDto, IComparable?> key = sort switch
        {
            "roundNumber" => row => row.RoundNumber,
            "season" => row => row.Season,
            "rating" => row => row.Rating,
            "pace" => row => row.Pace,
            "consistency" => row => row.Consistency,
            "confidence" => row => row.Confidence,
            "finalPosition" => row => row.FinalPosition,
            "awardedPoints" => row => row.AwardedPoints,
            _ => row => row.RoundStartDate
        };
        return (descending ? rows.OrderByDescending(key) : rows.OrderBy(key))
            .ThenBy(row => row.RoundId)
            .ToArray();
    }

    internal static RiderTrackStatisticsDto[] SortTrackResults(
        IReadOnlyList<RiderTrackStatisticsDto> rows,
        string sort,
        bool descending)
    {
        Func<RiderTrackStatisticsDto, IComparable?> key = sort switch
        {
            "trackName" => row => row.TrackName,
            "countryCode" => row => row.CountryCode,
            "rating" => row => row.Rating,
            "pace" => row => row.Pace,
            "consistency" => row => row.Consistency,
            "confidence" => row => row.Confidence,
            "averageFinalPosition" => row => row.AverageFinalPosition,
            "startsCount" => row => row.StartsCount,
            _ => row => row.VenueName
        };
        return (descending ? rows.OrderByDescending(key) : rows.OrderBy(key))
            .ThenBy(row => row.TrackId)
            .ToArray();
    }

    internal sealed record CurrentRiderPresentation(
        JsonObject Rider,
        JsonObject? Team,
        string? CurrentCategory,
        int? Age);
}
