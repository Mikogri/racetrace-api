using System.Text.Json.Serialization;

namespace RaceTrace.Api.Models;

public sealed record CompareResponseDto(
    IReadOnlyList<CompareRiderDto> Riders,
    IReadOnlyList<CompareSessionDto> Sessions,
    [property: JsonPropertyName("commonSessions")] IReadOnlyList<CompareSessionDto> CommonSessions,
    CompareSummaryDto Summary);

public sealed record CompareSummaryDto(
    [property: JsonPropertyName("ridersCount")] int RidersCount,
    [property: JsonPropertyName("sessionsCount")] int SessionsCount,
    [property: JsonPropertyName("commonSessionsCount")] int CommonSessionsCount);

public sealed record CompareRiderDto(
    [property: JsonPropertyName("riderId")] long RiderId,
    [property: JsonPropertyName("uciId")] string? UciId,
    [property: JsonPropertyName("firstName")] string? FirstName,
    [property: JsonPropertyName("lastName")] string? LastName,
    [property: JsonPropertyName("fullName")] string FullName,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: JsonPropertyName("profileImageUrl")] string? ProfileImageUrl,
    int? Age,
    [property: JsonPropertyName("currentCategory")] string? CurrentCategory,
    IReadOnlyList<string> Categories,
    [property: JsonPropertyName("teamId")] long? TeamId,
    [property: JsonPropertyName("teamName")] string? TeamName,
    [property: JsonPropertyName("teamCode")] string? TeamCode,
    [property: JsonPropertyName("winsCount")] int WinsCount,
    [property: JsonPropertyName("podiumsCount")] int PodiumsCount,
    [property: JsonPropertyName("top5Count")] int Top5Count,
    [property: JsonPropertyName("top10Count")] int Top10Count,
    decimal? Rating,
    decimal? Pace,
    decimal? Consistency,
    decimal Confidence,
    [property: JsonPropertyName("wonSplitsCount")] int WonSplitsCount,
    [property: JsonPropertyName("wonSectorsCount")] int WonSectorsCount,
    [property: JsonPropertyName("averageFinalPosition")] decimal? AverageFinalPosition,
    [property: JsonPropertyName("averagePreFinalPosition")] decimal? AveragePreFinalPosition,
    [property: JsonPropertyName("averageFinishGapToBestSelected")] decimal? AverageFinishGapToBestSelected,
    [property: JsonPropertyName("finishGapSamplesCount")] int FinishGapSamplesCount,
    [property: JsonPropertyName("averageSectorGapsToBestSelected")] IReadOnlyList<CompareAverageSectorGapDto> AverageSectorGapsToBestSelected,
    [property: JsonPropertyName("totalPoints")] decimal TotalPoints,
    [property: JsonPropertyName("pointsProgress")] IReadOnlyList<ComparePointsProgressDto> PointsProgress,
    [property: JsonPropertyName("roundMetrics")] IReadOnlyList<CompareRoundMetricsDto> RoundMetrics)
{
    [JsonPropertyName("consistencyEvidence")]
    public decimal ConsistencyEvidence { get; init; }

    [JsonPropertyName("sectorGapSummary")]
    public IReadOnlyList<CompareSectorGapSummaryDto> SectorGapSummary { get; init; } = [];

    [JsonPropertyName("resultsAveragePosition")]
    public decimal? ResultsAveragePosition { get; init; }
}

public sealed record CompareSectorGapSummaryDto(
    [property: JsonPropertyName("sectorIndex")] int SectorIndex,
    [property: JsonPropertyName("averageGap")] decimal? AverageGap,
    [property: JsonPropertyName("samplesCount")] int SamplesCount);

public sealed record ComparePointsProgressDto(
    [property: JsonPropertyName("resultId")] long ResultId,
    [property: JsonPropertyName("sessionId")] long SessionId,
    [property: JsonPropertyName("roundId")] long RoundId,
    DateOnly? Date,
    [property: JsonPropertyName("sessionType")] string? SessionType,
    string? Category,
    [property: JsonPropertyName("awardedPoints")] decimal AwardedPoints,
    [property: JsonPropertyName("cumulativePoints")] decimal CumulativePoints);

public sealed record CompareRoundMetricsDto(
    [property: JsonPropertyName("roundId")] long RoundId,
    int? Season,
    [property: JsonPropertyName("roundNumber")] int? RoundNumber,
    DateOnly? Date,
    [property: JsonPropertyName("trackId")] long? TrackId,
    [property: JsonPropertyName("trackName")] string? TrackName,
    [property: JsonPropertyName("venueName")] string? VenueName,
    [property: JsonPropertyName("competitionClassCode")] string? CompetitionClassCode,
    decimal? Rating,
    decimal? Pace,
    decimal? Consistency,
    decimal Confidence)
{
    [JsonPropertyName("consistencyEvidence")]
    public decimal ConsistencyEvidence { get; init; }
}

public sealed record CompareAverageSectorGapDto(
    [property: JsonPropertyName("trackId")] long? TrackId,
    [property: JsonPropertyName("trackName")] string? TrackName,
    [property: JsonPropertyName("sectorIndex")] int SectorIndex,
    [property: JsonPropertyName("isFinishSector")] bool IsFinishSector,
    [property: JsonPropertyName("averageGap")] decimal AverageGap,
    [property: JsonPropertyName("samplesCount")] int SamplesCount);

public sealed record CompareSessionDto(
    [property: JsonPropertyName("sessionId")] long SessionId,
    [property: JsonPropertyName("roundId")] long RoundId,
    int? Season,
    [property: JsonPropertyName("roundNumber")] int? RoundNumber,
    [property: JsonPropertyName("sessionName")] string? SessionName,
    [property: JsonPropertyName("sessionType")] string? SessionType,
    string? Category,
    [property: JsonPropertyName("sessionDate")] DateOnly? SessionDate,
    [property: JsonPropertyName("trackId")] long? TrackId,
    [property: JsonPropertyName("trackName")] string? TrackName,
    [property: JsonPropertyName("venueName")] string? VenueName,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    ConditionDto? Condition,
    [property: JsonPropertyName("competitionClassId")] long? CompetitionClassId,
    [property: JsonPropertyName("competitionClassCode")] string? CompetitionClassCode,
    [property: JsonPropertyName("competitionClassName")] string? CompetitionClassName,
    [property: JsonPropertyName("isCommon")] bool IsCommon,
    IReadOnlyList<CompareSessionResultDto> Results);

public sealed record CompareSessionResultDto(
    [property: JsonPropertyName("resultId")] long ResultId,
    [property: JsonPropertyName("riderId")] long RiderId,
    int? Position,
    string? Status,
    [property: JsonPropertyName("isClassified")] bool IsClassified,
    [property: JsonPropertyName("teamId")] long? TeamId,
    [property: JsonPropertyName("teamName")] string? TeamName,
    [property: JsonPropertyName("finalTime")] decimal? FinalTime,
    [property: JsonPropertyName("gapToBestSelected")] decimal? GapToBestSelected,
    IReadOnlyList<CompareSectorResultDto> Sectors);

public sealed record CompareSectorResultDto(
    [property: JsonPropertyName("sectorIndex")] int SectorIndex,
    [property: JsonPropertyName("isFinishSector")] bool IsFinishSector,
    decimal? Time,
    [property: JsonPropertyName("gapToBestSelected")] decimal? GapToBestSelected,
    [property: JsonPropertyName("positionAmongSelected")] int? PositionAmongSelected);
