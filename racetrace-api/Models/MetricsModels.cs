using System.Text.Json.Nodes;

namespace RaceTrace.Api.Models;

public sealed record RiderMetricsContext(
    int? SeasonYear,
    string? CategoryCode,
    DateOnly? FromDate,
    DateOnly? ToDate,
    string? RoundId,
    string? TrackId,
    string? CountryCode,
    int? ConditionId,
    string? CompetitionClassCode,
    string? SessionType,
    string? Status
)
{
    public IReadOnlyList<int> ConditionIds { get; init; } =
        ConditionId is null ? [] : [ConditionId.Value];

    public IReadOnlyList<int> SeasonYears { get; init; } =
        SeasonYear is null ? [] : [SeasonYear.Value];

    public IReadOnlyList<string> TrackIds { get; init; } =
        string.IsNullOrWhiteSpace(TrackId) ? [] : [TrackId];

    public string? Venue { get; init; }

    public string? TrackName { get; init; }

    public IReadOnlyList<string> VenueNames { get; init; } = [];

    public IReadOnlyList<string> CountryCodes { get; init; } = [];

    public IReadOnlyList<int> CompetitionClassIds { get; init; } = [];

    public IReadOnlyList<string> CompetitionClassCodes { get; init; } = [];

    public IReadOnlyList<string> CategoryCodes { get; init; } = [];

    public IReadOnlyList<string> SessionTypes { get; init; } = [];

    public IReadOnlyList<string> ConditionNames { get; init; } = [];
}

public sealed record RiderPerformanceMetricValues(
    decimal? Rating,
    decimal? RawRating,
    decimal? Pace,
    decimal? RawPace,
    decimal PaceConfidence,
    int PaceUsedSessions,
    decimal? Consistency,
    decimal ConsistencyConfidence,
    int ConsistencyUsedRuns,
    int ConsistencyUsedSectors
)
{
    public decimal ConsistencyEvidence => ConsistencyConfidence;

    public string PaceConfidenceStatus { get; init; } = "Low";

    public int PaceUsedFinals { get; init; }

    public decimal PaceTotalEvidence { get; init; }

    public decimal PaceFinalEvidence { get; init; }

    public decimal? ResultScore { get; init; }

    public decimal ResultScoreConfidence { get; init; }

    public int ResultScoreUsedSessions { get; init; }
}

public sealed record RiderMetricValues(
    decimal? Rating,
    decimal? RawRating,
    decimal? Pace,
    decimal? RawPace,
    decimal PaceConfidence,
    int PaceUsedSessions,
    decimal? Consistency,
    decimal ConsistencyConfidence,
    int ConsistencyUsedRuns,
    int ConsistencyUsedSectors,
    decimal HighestRating,
    decimal Points,
    int? Rank,
    int Wins,
    int Podiums,
    int Top10,
    int Top20,
    int Top30,
    int Starts,
    int Finished,
    int Dnf,
    int Dns,
    int Dsq,
    decimal? AvgPosition,
    decimal? AvgGapMs,
    decimal? AvgRaceTimeMs,
    JsonObject? BestResult,
    decimal? BestSpeed
)
{
    public decimal ConsistencyEvidence => ConsistencyConfidence;

    public string PaceConfidenceStatus { get; init; } = "Low";

    public int PaceUsedFinals { get; init; }

    public decimal PaceTotalEvidence { get; init; }

    public decimal PaceFinalEvidence { get; init; }

    public decimal? ResultScore { get; init; }

    public decimal ResultScoreConfidence { get; init; }

    public int ResultScoreUsedSessions { get; init; }
}
