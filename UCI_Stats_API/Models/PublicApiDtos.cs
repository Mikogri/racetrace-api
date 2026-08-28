using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace UCI_Stats_API.Models;

public sealed record PublicPagedResponse<T>(
    [property: JsonPropertyName("items")] IReadOnlyList<T> Items,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pageSize")] int PageSize,
    [property: JsonPropertyName("totalItems")] int TotalItems,
    [property: JsonPropertyName("totalPages")] int TotalPages,
    [property: JsonPropertyName("hasPreviousPage")] bool HasPreviousPage,
    [property: JsonPropertyName("hasNextPage")] bool HasNextPage)
{
    public static PublicPagedResponse<T> Create(
        IReadOnlyList<T> items,
        int page,
        int pageSize,
        int totalItems)
    {
        var totalPages = totalItems == 0 ? 0 : (int)Math.Ceiling(totalItems / (double)pageSize);
        return new PublicPagedResponse<T>(
            items,
            page,
            pageSize,
            totalItems,
            totalPages,
            page > 1,
            page < totalPages);
    }
}

public sealed record RankingRiderDto(
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("riderId")] long RiderId,
    [property: JsonPropertyName("uciId")] string? UciId,
    [property: JsonPropertyName("firstName")] string? FirstName,
    [property: JsonPropertyName("lastName")] string? LastName,
    [property: JsonPropertyName("fullName")] string FullName,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: JsonPropertyName("teamId")] long? TeamId,
    [property: JsonPropertyName("teamName")] string? TeamName,
    [property: JsonPropertyName("totalPoints")] decimal TotalPoints,
    [property: JsonPropertyName("rating")] decimal? Rating,
    [property: JsonPropertyName("pace")] decimal? Pace,
    [property: JsonPropertyName("consistency")] decimal? Consistency,
    [property: JsonPropertyName("confidence")] decimal Confidence,
    [property: JsonPropertyName("finalsCount")] int FinalsCount,
    [property: JsonPropertyName("qualifyingRate")] decimal QualifyingRate,
    [property: JsonPropertyName("profileImageUrl")] string? ProfileImageUrl)
{
    [JsonPropertyName("consistencyEvidence")]
    public decimal ConsistencyEvidence { get; init; }
}

public sealed record RankingSummaryDto(
    [property: JsonPropertyName("sessionsCount")] int SessionsCount,
    [property: JsonPropertyName("finalsCount")] int FinalsCount,
    [property: JsonPropertyName("ridersCount")] int RidersCount);

public sealed record RankingResponseDto(
    [property: JsonPropertyName("items")] IReadOnlyList<RankingRiderDto> Items,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pageSize")] int PageSize,
    [property: JsonPropertyName("totalItems")] int TotalItems,
    [property: JsonPropertyName("totalPages")] int TotalPages,
    [property: JsonPropertyName("hasPreviousPage")] bool HasPreviousPage,
    [property: JsonPropertyName("hasNextPage")] bool HasNextPage,
    [property: JsonPropertyName("summary")] RankingSummaryDto Summary);

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

public sealed record RoundListItemDto(
    [property: JsonPropertyName("roundId")] long RoundId,
    [property: JsonPropertyName("roundNumber")] int? RoundNumber,
    [property: JsonPropertyName("season")] int Season,
    [property: JsonPropertyName("contextName")] string? ContextName,
    [property: JsonPropertyName("imageUrl")] string? ImageUrl,
    [property: JsonPropertyName("trackId")] long TrackId,
    [property: JsonPropertyName("trackName")] string? TrackName,
    [property: JsonPropertyName("venueId")] long? VenueId,
    [property: JsonPropertyName("venueName")] string? VenueName,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: JsonPropertyName("startDate")] DateOnly? StartDate,
    [property: JsonPropertyName("endDate")] DateOnly? EndDate,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("competitionClassId")] long? CompetitionClassId,
    [property: JsonPropertyName("competitionClassCode")] string? CompetitionClassCode,
    [property: JsonPropertyName("competitionClassName")] string? CompetitionClassName,
    [property: JsonPropertyName("competitionClassPriority")] int? CompetitionClassPriority);

public sealed record ConditionDto(
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("name")] string? Name);

public sealed record SessionSummaryDto(
    [property: JsonPropertyName("sessionId")] long SessionId,
    [property: JsonPropertyName("roundId")] long RoundId,
    [property: JsonPropertyName("sessionType")] string? SessionType,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("condition")] ConditionDto? Condition,
    [property: JsonPropertyName("completedRidersCount")] int CompletedRidersCount,
    [property: JsonPropertyName("startedRidersCount")] int StartedRidersCount,
    [property: JsonPropertyName("splitCount")] int SplitCount,
    [property: JsonPropertyName("sectorCount")] int SectorCount,
    [property: JsonPropertyName("timeView")] string TimeView);

public sealed record ResultTimeDto(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("time")] decimal? Time,
    [property: JsonPropertyName("gapToLeader")] decimal? GapToLeader,
    [property: JsonPropertyName("position")] int? Position)
{
    [JsonPropertyName("gapToBest")]
    public decimal? GapToBest => GapToLeader;
}

public sealed record SessionResultDto(
    [property: JsonPropertyName("position")] int? Position,
    [property: JsonPropertyName("resultId")] long? ResultId,
    [property: JsonPropertyName("riderId")] long? RiderId,
    [property: JsonPropertyName("uciId")] string? UciId,
    [property: JsonPropertyName("firstName")] string? FirstName,
    [property: JsonPropertyName("lastName")] string? LastName,
    [property: JsonPropertyName("fullName")] string FullName,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: JsonPropertyName("teamId")] long? TeamId,
    [property: JsonPropertyName("teamName")] string? TeamName,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("topSpeed")] decimal? TopSpeed,
    [property: JsonPropertyName("averageSpeed")] decimal? AverageSpeed,
    [property: JsonPropertyName("finalTime")] decimal? FinalTime,
    [property: JsonPropertyName("finalGapToLeader")] decimal? FinalGapToLeader,
    [property: JsonPropertyName("awardedPoints")] decimal AwardedPoints,
    [property: JsonPropertyName("times")] IReadOnlyList<ResultTimeDto> Times)
{
    [JsonPropertyName("isPerfectRun")]
    public bool IsPerfectRun { get; init; }

    [JsonPropertyName("finalGapToBest")]
    public decimal? FinalGapToBest => FinalGapToLeader;
}

public sealed record SessionResultsResponseDto(
    [property: JsonPropertyName("session")] SessionSummaryDto Session,
    [property: JsonPropertyName("items")] IReadOnlyList<SessionResultDto> Items,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pageSize")] int PageSize,
    [property: JsonPropertyName("totalItems")] int TotalItems,
    [property: JsonPropertyName("totalPages")] int TotalPages,
    [property: JsonPropertyName("hasPreviousPage")] bool HasPreviousPage,
    [property: JsonPropertyName("hasNextPage")] bool HasNextPage);

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
    [property: JsonPropertyName("teamHistory")] JsonArray TeamHistory);

public sealed record CommonSessionDto(
    [property: JsonPropertyName("sessionId")] long SessionId,
    [property: JsonPropertyName("roundId")] long RoundId,
    [property: JsonPropertyName("sessionType")] string? SessionType,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("sessionDate")] DateOnly? SessionDate,
    [property: JsonPropertyName("riderIds")] IReadOnlyList<long> RiderIds);

public sealed record TrackFilterOptionDto(
    [property: JsonPropertyName("trackId")] long TrackId,
    [property: JsonPropertyName("trackName")] string? TrackName,
    [property: JsonPropertyName("venueId")] long? VenueId,
    [property: JsonPropertyName("venueName")] string? VenueName,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: JsonPropertyName("lengthM")] int? LengthM,
    [property: JsonPropertyName("avgSlope")] decimal? AvgSlope);
