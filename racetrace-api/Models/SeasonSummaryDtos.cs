namespace RaceTrace.Api.Models;

public sealed record SeasonSummaryRoundDto(
    long RoundId,
    int Season,
    int? RoundNumber,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string Status,
    long? TrackId,
    string? TrackName,
    string? VenueName,
    string? CountryCode,
    decimal? TrackLengthMeters,
    decimal? AverageSlope,
    long? CompetitionClassId,
    string? CompetitionClassCode,
    string? CompetitionClassName,
    int? CompetitionClassPriority
);

public sealed record SeasonSummaryResultDto(
    int Position,
    long ResultId,
    long RiderId,
    string FullName,
    string? CountryCode,
    long? TeamId,
    string? TeamName,
    decimal? Time,
    decimal? GapToLeader
);

public sealed record SeasonProgressDto(
    int RoundsCount,
    int CompletedRoundsCount,
    int FinalWinnersCount,
    int CategoriesCount
);

public sealed record SeasonTopRatedRiderDto(
    int Position,
    long RiderId,
    string FullName,
    string? CountryCode,
    long? TeamId,
    string? TeamName,
    decimal Rating,
    decimal? Pace,
    decimal? Consistency,
    decimal Confidence
);

public sealed record SeasonStandingRiderDto(
    int Position,
    long RiderId,
    string FullName,
    string? CountryCode,
    long? TeamId,
    string? TeamName,
    decimal Points
);

public sealed record SeasonSummaryResponseDto(
    int Season,
    string Category,
    SeasonSummaryRoundDto? NextRound,
    SeasonSummaryRoundDto? LastCompletedRound,
    IReadOnlyList<SeasonSummaryResultDto> LastRoundResults,
    SeasonProgressDto SeasonProgress,
    IReadOnlyList<SeasonTopRatedRiderDto> SeasonTopRated,
    IReadOnlyList<SeasonStandingRiderDto> CurrentStandings
);

public sealed record PodiumPredictionRiderDto(
    int Position,
    long RiderId,
    string FullName,
    string? CountryCode,
    long? TeamId,
    string? TeamName,
    decimal PodiumProbability,
    decimal PredictionScore,
    decimal? CurrentSeasonRating,
    decimal? LastTrackPace,
    decimal? PreviousSeasonRating,
    decimal Confidence
)
{
    public decimal WinProbability { get; init; }

    public decimal Top5Probability { get; init; }

    public decimal ExpectedPosition { get; init; }

    public decimal ExpectedPerformance { get; init; }

    public decimal PerformanceUncertainty { get; init; }

    public decimal IncidentProbability { get; init; }

    public PodiumPredictionContextDto Context { get; init; } = new(0, 0, 0, 0, 0);
}

public sealed record PodiumPredictionContextDto(
    decimal TrackAffinity,
    decimal ConditionsAffinity,
    decimal RecentForm,
    decimal Pressure,
    decimal PressureResponse
);

public sealed record PodiumPredictionResponseDto(
    int Season,
    string Category,
    SeasonSummaryRoundDto? Round,
    IReadOnlyList<PodiumPredictionRiderDto> Items
)
{
    public string ModelVersion { get; init; } = "ridge-monte-carlo-v1";
}
