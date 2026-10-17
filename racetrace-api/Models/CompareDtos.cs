namespace RaceTrace.Api.Models;

public sealed record CompareResponseDto(
    IReadOnlyList<CompareRiderDto> Riders,
    IReadOnlyList<CompareSessionDto> Sessions,
    IReadOnlyList<CompareSessionDto> CommonSessions,
    CompareSummaryDto Summary);

public sealed record CompareSummaryDto(
    int RidersCount,
    int SessionsCount,
    int CommonSessionsCount);

public sealed record CompareRiderDto(
    long RiderId,
    string? UciId,
    string? FirstName,
    string? LastName,
    string FullName,
    string? CountryCode,
    string? ProfileImageUrl,
    int? Age,
    string? CurrentCategory,
    IReadOnlyList<string> Categories,
    long? TeamId,
    string? TeamName,
    string? TeamCode,
    int WinsCount,
    int PodiumsCount,
    int Top5Count,
    int Top10Count,
    decimal? Rating,
    decimal? Pace,
    decimal? Consistency,
    decimal Confidence,
    int WonSplitsCount,
    int WonSectorsCount,
    decimal? AverageFinalPosition,
    decimal? AveragePreFinalPosition,
    decimal? AverageFinishGapToBestSelected,
    int FinishGapSamplesCount,
    IReadOnlyList<CompareAverageSectorGapDto> AverageSectorGapsToBestSelected,
    decimal TotalPoints,
    IReadOnlyList<ComparePointsProgressDto> PointsProgress,
    IReadOnlyList<CompareRoundMetricsDto> RoundMetrics)
{
    public decimal ConsistencyEvidence { get; init; }

    public IReadOnlyList<CompareSectorGapSummaryDto> SectorGapSummary { get; init; } = [];

    public decimal? ResultsAveragePosition { get; init; }
}

public sealed record CompareSectorGapSummaryDto(
    int SectorIndex,
    decimal? AverageGap,
    int SamplesCount);

public sealed record ComparePointsProgressDto(
    long ResultId,
    long SessionId,
    long RoundId,
    DateOnly? Date,
    string? SessionType,
    string? Category,
    decimal AwardedPoints,
    decimal CumulativePoints);

public sealed record CompareRoundMetricsDto(
    long RoundId,
    int? Season,
    int? RoundNumber,
    DateOnly? Date,
    long? TrackId,
    string? TrackName,
    string? VenueName,
    string? CompetitionClassCode,
    decimal? Rating,
    decimal? Pace,
    decimal? Consistency,
    decimal Confidence)
{
    public decimal ConsistencyEvidence { get; init; }
}

public sealed record CompareAverageSectorGapDto(
    long? TrackId,
    string? TrackName,
    int SectorIndex,
    bool IsFinishSector,
    decimal AverageGap,
    int SamplesCount);

public sealed record CompareSessionDto(
    long SessionId,
    long RoundId,
    int? Season,
    int? RoundNumber,
    string? SessionName,
    string? SessionType,
    string? Category,
    DateOnly? SessionDate,
    long? TrackId,
    string? TrackName,
    string? VenueName,
    string? CountryCode,
    ConditionDto? Condition,
    long? CompetitionClassId,
    string? CompetitionClassCode,
    string? CompetitionClassName,
    bool IsCommon,
    IReadOnlyList<CompareSessionResultDto> Results);

public sealed record CompareSessionResultDto(
    long ResultId,
    long RiderId,
    int? Position,
    string? Status,
    bool IsClassified,
    long? TeamId,
    string? TeamName,
    decimal? FinalTime,
    decimal? GapToBestSelected,
    IReadOnlyList<CompareSectorResultDto> Sectors);

public sealed record CompareSectorResultDto(
    int SectorIndex,
    bool IsFinishSector,
    decimal? Time,
    decimal? GapToBestSelected,
    int? PositionAmongSelected);
