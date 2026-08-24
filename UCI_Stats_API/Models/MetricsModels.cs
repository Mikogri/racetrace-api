using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace UCI_Stats_API.Models;

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
    string? Status)
{
    public IReadOnlyList<int> ConditionIds { get; init; } = ConditionId is null ? [] : [ConditionId.Value];

    public IReadOnlyList<int> SeasonYears { get; init; } = SeasonYear is null ? [] : [SeasonYear.Value];

    public IReadOnlyList<string> TrackIds { get; init; } = string.IsNullOrWhiteSpace(TrackId) ? [] : [TrackId];

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
    [property: JsonPropertyName("raw_rating")]
    decimal? RawRating,
    decimal? Pace,
    [property: JsonPropertyName("raw_pace")]
    decimal? RawPace,
    decimal PaceConfidence,
    int PaceUsedSessions,
    decimal? Consistency,
    decimal ConsistencyConfidence,
    int ConsistencyUsedRuns,
    int ConsistencyUsedSectors)
{
    [JsonPropertyName("consistency_evidence")]
    public decimal ConsistencyEvidence => ConsistencyConfidence;

    [JsonPropertyName("pace_confidence_status")]
    public string PaceConfidenceStatus { get; init; } = "Low";

    [JsonPropertyName("pace_used_finals")]
    public int PaceUsedFinals { get; init; }

    [JsonPropertyName("pace_total_evidence")]
    public decimal PaceTotalEvidence { get; init; }

    [JsonPropertyName("pace_final_evidence")]
    public decimal PaceFinalEvidence { get; init; }

    [JsonPropertyName("result_score")]
    public decimal? ResultScore { get; init; }

    [JsonPropertyName("result_score_confidence")]
    public decimal ResultScoreConfidence { get; init; }

    [JsonPropertyName("result_score_used_sessions")]
    public int ResultScoreUsedSessions { get; init; }
}

public sealed record RiderMetricValues(
    decimal? Rating,
    [property: JsonPropertyName("raw_rating")]
    decimal? RawRating,
    decimal? Pace,
    [property: JsonPropertyName("raw_pace")]
    decimal? RawPace,
    [property: JsonPropertyName("pace_confidence")]
    decimal PaceConfidence,
    [property: JsonPropertyName("pace_used_sessions")]
    int PaceUsedSessions,
    decimal? Consistency,
    [property: JsonPropertyName("consistency_confidence")]
    decimal ConsistencyConfidence,
    [property: JsonPropertyName("consistency_used_runs")]
    int ConsistencyUsedRuns,
    [property: JsonPropertyName("consistency_used_sectors")]
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
    decimal? BestSpeed)
{
    [JsonPropertyName("consistency_evidence")]
    public decimal ConsistencyEvidence => ConsistencyConfidence;

    [JsonPropertyName("pace_confidence_status")]
    public string PaceConfidenceStatus { get; init; } = "Low";

    [JsonPropertyName("pace_used_finals")]
    public int PaceUsedFinals { get; init; }

    [JsonPropertyName("pace_total_evidence")]
    public decimal PaceTotalEvidence { get; init; }

    [JsonPropertyName("pace_final_evidence")]
    public decimal PaceFinalEvidence { get; init; }

    [JsonPropertyName("result_score")]
    public decimal? ResultScore { get; init; }

    [JsonPropertyName("result_score_confidence")]
    public decimal ResultScoreConfidence { get; init; }

    [JsonPropertyName("result_score_used_sessions")]
    public int ResultScoreUsedSessions { get; init; }
}
