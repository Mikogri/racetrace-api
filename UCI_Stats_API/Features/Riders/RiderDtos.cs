using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace UCI_Stats_API.Models;

public sealed record RiderTeamHistoryDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("riderId")] long RiderId,
    [property: JsonPropertyName("teamId")] long TeamId,
    [property: JsonPropertyName("teamName")] string? TeamName,
    [property: JsonPropertyName("teamCode")] string? TeamCode,
    [property: JsonPropertyName("validFrom")] DateOnly ValidFrom,
    [property: JsonPropertyName("validTo")] DateOnly? ValidTo,
    [property: JsonPropertyName("isCurrent")] bool IsCurrent,
    [property: JsonPropertyName("sourceRoundId")] long? SourceRoundId);

public sealed record SessionPositionDto(
    [property: JsonPropertyName("roundId")] long RoundId,
    [property: JsonPropertyName("sessionId")] long SessionId,
    [property: JsonPropertyName("sessionType")] string SessionType,
    [property: JsonPropertyName("position")] int? Position);

public sealed record SpeedGroupDto(
    [property: JsonPropertyName("topSpeed")] decimal? TopSpeed,
    [property: JsonPropertyName("averageSpeed")] decimal? AverageSpeed);

public sealed record RiderSpeedDto(
    [property: JsonPropertyName("qualification")] SpeedGroupDto Qualification,
    [property: JsonPropertyName("final")] SpeedGroupDto Final);

public sealed record AverageSectorTimeDto(
    [property: JsonPropertyName("sectorIndex")] int SectorIndex,
    [property: JsonPropertyName("averageTime")] decimal AverageTime,
    [property: JsonPropertyName("samplesCount")] int SamplesCount);

public sealed record RiderStatisticsDto(
    [property: JsonPropertyName("riderId")] long RiderId,
    [property: JsonPropertyName("uciId")] string? UciId,
    [property: JsonPropertyName("firstName")] string? FirstName,
    [property: JsonPropertyName("lastName")] string? LastName,
    [property: JsonPropertyName("fullName")] string FullName,
    [property: JsonPropertyName("profileImageUrl")] string? ProfileImageUrl,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: JsonPropertyName("gender")] string? Gender,
    [property: JsonPropertyName("teamId")] long? TeamId,
    [property: JsonPropertyName("teamName")] string? TeamName,
    [property: JsonPropertyName("teamCode")] string? TeamCode,
    [property: JsonPropertyName("currentCategory")] string? CurrentCategory,
    [property: JsonPropertyName("age")] int? Age,
    [property: JsonPropertyName("rating")] decimal? Rating,
    [property: JsonPropertyName("pace")] decimal? Pace,
    [property: JsonPropertyName("consistency")] decimal? Consistency,
    [property: JsonPropertyName("confidence")] decimal Confidence,
    [property: JsonPropertyName("averagePosition")] decimal? AveragePosition,
    [property: JsonPropertyName("totalPoints")] decimal TotalPoints,
    [property: JsonPropertyName("rankingPosition")] int? RankingPosition,
    [property: JsonPropertyName("qualifyingPositions")] IReadOnlyList<SessionPositionDto> QualifyingPositions,
    [property: JsonPropertyName("finalPositions")] IReadOnlyList<SessionPositionDto> FinalPositions,
    [property: JsonPropertyName("startsCount")] int StartsCount,
    [property: JsonPropertyName("winsCount")] int WinsCount,
    [property: JsonPropertyName("podiumsCount")] int PodiumsCount,
    [property: JsonPropertyName("top5Count")] int Top5Count,
    [property: JsonPropertyName("top10Count")] int Top10Count,
    [property: JsonPropertyName("wonSplitsCount")] int WonSplitsCount,
    [property: JsonPropertyName("wonSectorsCount")] int WonSectorsCount,
    [property: JsonPropertyName("preFinalWonSplitsCount")] int PreFinalWonSplitsCount,
    [property: JsonPropertyName("preFinalWonSectorsCount")] int PreFinalWonSectorsCount,
    [property: JsonPropertyName("finalWonSplitsCount")] int FinalWonSplitsCount,
    [property: JsonPropertyName("finalWonSectorsCount")] int FinalWonSectorsCount,
    [property: JsonPropertyName("speed")] RiderSpeedDto Speed,
    [property: JsonPropertyName("averageQualifyingPosition")] decimal? AverageQualifyingPosition,
    [property: JsonPropertyName("averageQualifying1Position")] decimal? AverageQualifying1Position,
    [property: JsonPropertyName("averageQualifying2Position")] decimal? AverageQualifying2Position,
    [property: JsonPropertyName("averageSemiFinalPosition")] decimal? AverageSemiFinalPosition,
    [property: JsonPropertyName("averageFinalPosition")] decimal? AverageFinalPosition,
    [property: JsonPropertyName("averageSectorTimes")] IReadOnlyList<AverageSectorTimeDto> AverageSectorTimes,
    [property: JsonPropertyName("averageFinishTime")] decimal? AverageFinishTime);

public sealed record RiderRoundResultDto(
    [property: JsonPropertyName("roundId")] long RoundId,
    [property: JsonPropertyName("roundNumber")] int? RoundNumber,
    [property: JsonPropertyName("season")] int? Season,
    [property: JsonPropertyName("competitionClassId")] long? CompetitionClassId,
    [property: JsonPropertyName("competitionClassCode")] string? CompetitionClassCode,
    [property: JsonPropertyName("competitionClassName")] string? CompetitionClassName,
    [property: JsonPropertyName("condition")] ConditionDto? Condition,
    [property: JsonPropertyName("roundStartDate")] DateOnly? RoundStartDate,
    [property: JsonPropertyName("trackId")] long? TrackId,
    [property: JsonPropertyName("trackName")] string? TrackName,
    [property: JsonPropertyName("venueId")] long? VenueId,
    [property: JsonPropertyName("venueName")] string? VenueName,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: JsonPropertyName("rating")] decimal? Rating,
    [property: JsonPropertyName("pace")] decimal? Pace,
    [property: JsonPropertyName("consistency")] decimal? Consistency,
    [property: JsonPropertyName("confidence")] decimal Confidence,
    [property: JsonPropertyName("qualifyingPosition")] int? QualifyingPosition,
    [property: JsonPropertyName("qualifying1Position")] int? Qualifying1Position,
    [property: JsonPropertyName("qualifying2Position")] int? Qualifying2Position,
    [property: JsonPropertyName("semiFinalPosition")] int? SemiFinalPosition,
    [property: JsonPropertyName("finalPosition")] int? FinalPosition,
    [property: JsonPropertyName("awardedPoints")] decimal AwardedPoints);

public sealed record RiderTrackStatisticsDto(
    [property: JsonPropertyName("trackId")] long TrackId,
    [property: JsonPropertyName("trackName")] string? TrackName,
    [property: JsonPropertyName("venueId")] long? VenueId,
    [property: JsonPropertyName("venueName")] string? VenueName,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: JsonPropertyName("rating")] decimal? Rating,
    [property: JsonPropertyName("pace")] decimal? Pace,
    [property: JsonPropertyName("consistency")] decimal? Consistency,
    [property: JsonPropertyName("confidence")] decimal Confidence,
    [property: JsonPropertyName("averageQualifyingPosition")] decimal? AverageQualifyingPosition,
    [property: JsonPropertyName("averageQualifying1Position")] decimal? AverageQualifying1Position,
    [property: JsonPropertyName("averageQualifying2Position")] decimal? AverageQualifying2Position,
    [property: JsonPropertyName("averageSemiFinalPosition")] decimal? AverageSemiFinalPosition,
    [property: JsonPropertyName("averageFinalPosition")] decimal? AverageFinalPosition,
    [property: JsonPropertyName("startsCount")] int StartsCount);

public sealed record RiderOverviewDto(
    [property: JsonPropertyName("statistics")] RiderStatisticsDto Statistics,
    [property: JsonPropertyName("roundResults")] IReadOnlyList<RiderRoundResultDto> RoundResults,
    [property: JsonPropertyName("trackStatistics")] IReadOnlyList<RiderTrackStatisticsDto> TrackStatistics,
    [property: JsonPropertyName("sessionResults")] JsonArray SessionResults,
    [property: JsonPropertyName("pressurePerformance")] JsonNode? PressurePerformance,
    [property: JsonPropertyName("teamHistory")] JsonArray TeamHistory)
{
    [JsonPropertyName("chartStatistics")]
    public RiderChartStatisticsDto ChartStatistics { get; init; } = new(null, new Dictionary<string, decimal>(), new Dictionary<string, decimal?>());
}

public sealed record RiderChartStatisticsDto(
    [property: JsonPropertyName("resultsAveragePosition")] decimal? ResultsAveragePosition,
    [property: JsonPropertyName("cumulativePointsByRound")] IReadOnlyDictionary<string, decimal> CumulativePointsByRound,
    [property: JsonPropertyName("preFinalAverageByTrack")] IReadOnlyDictionary<string, decimal?> PreFinalAverageByTrack);
