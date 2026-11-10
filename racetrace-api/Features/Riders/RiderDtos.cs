using System.Text.Json.Nodes;

namespace RaceTrace.Api.Models;

public sealed record RiderTeamHistoryDto(
    long Id,
    long RiderId,
    long TeamId,
    string? TeamName,
    string? TeamCode,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    bool IsCurrent,
    long? SourceRoundId
);

public sealed record SessionPositionDto(
    long RoundId,
    long SessionId,
    string SessionType,
    int? Position
);

public sealed record SpeedGroupDto(decimal? TopSpeed, decimal? AverageSpeed);

public sealed record RiderSpeedDto(SpeedGroupDto Qualification, SpeedGroupDto Final);

public sealed record AverageSectorTimeDto(int SectorIndex, decimal AverageTime, int SamplesCount);

public sealed record RiderStatisticsDto(
    long RiderId,
    string? UciId,
    string? FirstName,
    string? LastName,
    string FullName,
    string? ProfileImageUrl,
    string? CountryCode,
    string? Gender,
    long? TeamId,
    string? TeamName,
    string? TeamCode,
    string? CurrentCategory,
    int? Age,
    decimal? Rating,
    decimal? Pace,
    decimal? Consistency,
    decimal Confidence,
    decimal? AveragePosition,
    decimal TotalPoints,
    int? RankingPosition,
    IReadOnlyList<SessionPositionDto> QualifyingPositions,
    IReadOnlyList<SessionPositionDto> FinalPositions,
    int StartsCount,
    int WinsCount,
    int PodiumsCount,
    int Top5Count,
    int Top10Count,
    int WonSplitsCount,
    int WonSectorsCount,
    int PreFinalWonSplitsCount,
    int PreFinalWonSectorsCount,
    int FinalWonSplitsCount,
    int FinalWonSectorsCount,
    RiderSpeedDto Speed,
    decimal? AverageQualifyingPosition,
    decimal? AverageQualifying1Position,
    decimal? AverageQualifying2Position,
    decimal? AverageSemiFinalPosition,
    decimal? AverageFinalPosition,
    IReadOnlyList<AverageSectorTimeDto> AverageSectorTimes,
    decimal? AverageFinishTime
);

public sealed record RiderRoundResultDto(
    long RoundId,
    int? RoundNumber,
    int? Season,
    long? CompetitionClassId,
    string? CompetitionClassCode,
    string? CompetitionClassName,
    ConditionDto? Condition,
    DateOnly? RoundStartDate,
    long? TrackId,
    string? TrackName,
    long? VenueId,
    string? VenueName,
    string? CountryCode,
    decimal? Rating,
    decimal? Pace,
    decimal? Consistency,
    decimal Confidence,
    int? QualifyingPosition,
    int? Qualifying1Position,
    int? Qualifying2Position,
    int? SemiFinalPosition,
    int? FinalPosition,
    decimal AwardedPoints
);

public sealed record RiderTrackStatisticsDto(
    long TrackId,
    string? TrackName,
    long? VenueId,
    string? VenueName,
    string? CountryCode,
    decimal? Rating,
    decimal? Pace,
    decimal? Consistency,
    decimal Confidence,
    decimal? AverageQualifyingPosition,
    decimal? AverageQualifying1Position,
    decimal? AverageQualifying2Position,
    decimal? AverageSemiFinalPosition,
    decimal? AverageFinalPosition,
    int StartsCount
);

public sealed record RiderOverviewDto(
    RiderStatisticsDto Statistics,
    IReadOnlyList<RiderRoundResultDto> RoundResults,
    IReadOnlyList<RiderTrackStatisticsDto> TrackStatistics,
    JsonArray SessionResults,
    JsonNode? PressurePerformance,
    JsonArray TeamHistory
)
{
    public RiderChartStatisticsDto ChartStatistics { get; init; } =
        new(null, new Dictionary<string, decimal>(), new Dictionary<string, decimal?>());
}

public sealed record RiderChartStatisticsDto(
    decimal? ResultsAveragePosition,
    IReadOnlyDictionary<string, decimal> CumulativePointsByRound,
    IReadOnlyDictionary<string, decimal?> PreFinalAverageByTrack
)
{
    public IReadOnlyDictionary<string, decimal?> CumulativeRatingByRound { get; init; } =
        new Dictionary<string, decimal?>();
}
