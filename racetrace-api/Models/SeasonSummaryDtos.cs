using System.Text.Json.Serialization;

namespace RaceTrace.Api.Models;

public sealed record SeasonSummaryRoundDto(
    [property: JsonPropertyName("roundId")] long RoundId,
    [property: JsonPropertyName("season")] int Season,
    [property: JsonPropertyName("roundNumber")] int? RoundNumber,
    [property: JsonPropertyName("startDate")] DateOnly? StartDate,
    [property: JsonPropertyName("endDate")] DateOnly? EndDate,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("trackId")] long? TrackId,
    [property: JsonPropertyName("trackName")] string? TrackName,
    [property: JsonPropertyName("venueName")] string? VenueName,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: JsonPropertyName("trackLengthMeters")] decimal? TrackLengthMeters,
    [property: JsonPropertyName("averageSlope")] decimal? AverageSlope,
    [property: JsonPropertyName("competitionClassId")] long? CompetitionClassId,
    [property: JsonPropertyName("competitionClassCode")] string? CompetitionClassCode,
    [property: JsonPropertyName("competitionClassName")] string? CompetitionClassName,
    [property: JsonPropertyName("competitionClassPriority")] int? CompetitionClassPriority);

public sealed record SeasonSummaryResultDto(
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("resultId")] long ResultId,
    [property: JsonPropertyName("riderId")] long RiderId,
    [property: JsonPropertyName("fullName")] string FullName,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: JsonPropertyName("teamId")] long? TeamId,
    [property: JsonPropertyName("teamName")] string? TeamName,
    [property: JsonPropertyName("time")] decimal? Time,
    [property: JsonPropertyName("gapToLeader")] decimal? GapToLeader);

public sealed record SeasonProgressDto(
    [property: JsonPropertyName("roundsCount")] int RoundsCount,
    [property: JsonPropertyName("completedRoundsCount")] int CompletedRoundsCount,
    [property: JsonPropertyName("finalWinnersCount")] int FinalWinnersCount,
    [property: JsonPropertyName("categoriesCount")] int CategoriesCount);

public sealed record SeasonTopRatedRiderDto(
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("riderId")] long RiderId,
    [property: JsonPropertyName("fullName")] string FullName,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: JsonPropertyName("teamId")] long? TeamId,
    [property: JsonPropertyName("teamName")] string? TeamName,
    [property: JsonPropertyName("rating")] decimal Rating,
    [property: JsonPropertyName("pace")] decimal? Pace,
    [property: JsonPropertyName("consistency")] decimal? Consistency,
    [property: JsonPropertyName("confidence")] decimal Confidence);

public sealed record SeasonStandingRiderDto(
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("riderId")] long RiderId,
    [property: JsonPropertyName("fullName")] string FullName,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: JsonPropertyName("teamId")] long? TeamId,
    [property: JsonPropertyName("teamName")] string? TeamName,
    [property: JsonPropertyName("points")] decimal Points);

public sealed record SeasonSummaryResponseDto(
    [property: JsonPropertyName("season")] int Season,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("nextRound")] SeasonSummaryRoundDto? NextRound,
    [property: JsonPropertyName("lastCompletedRound")] SeasonSummaryRoundDto? LastCompletedRound,
    [property: JsonPropertyName("lastRoundResults")] IReadOnlyList<SeasonSummaryResultDto> LastRoundResults,
    [property: JsonPropertyName("seasonProgress")] SeasonProgressDto SeasonProgress,
    [property: JsonPropertyName("seasonTopRated")] IReadOnlyList<SeasonTopRatedRiderDto> SeasonTopRated,
    [property: JsonPropertyName("currentStandings")] IReadOnlyList<SeasonStandingRiderDto> CurrentStandings);

public sealed record PodiumPredictionRiderDto(
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("riderId")] long RiderId,
    [property: JsonPropertyName("fullName")] string FullName,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: JsonPropertyName("teamId")] long? TeamId,
    [property: JsonPropertyName("teamName")] string? TeamName,
    [property: JsonPropertyName("podiumProbability")] decimal PodiumProbability,
    [property: JsonPropertyName("predictionScore")] decimal PredictionScore,
    [property: JsonPropertyName("currentSeasonRating")] decimal? CurrentSeasonRating,
    [property: JsonPropertyName("lastTrackPace")] decimal? LastTrackPace,
    [property: JsonPropertyName("previousSeasonRating")] decimal? PreviousSeasonRating,
    [property: JsonPropertyName("confidence")] decimal Confidence)
{
    [JsonPropertyName("winProbability")]
    public decimal WinProbability { get; init; }

    [JsonPropertyName("top5Probability")]
    public decimal Top5Probability { get; init; }

    [JsonPropertyName("expectedPosition")]
    public decimal ExpectedPosition { get; init; }

    [JsonPropertyName("expectedPerformance")]
    public decimal ExpectedPerformance { get; init; }

    [JsonPropertyName("performanceUncertainty")]
    public decimal PerformanceUncertainty { get; init; }

    [JsonPropertyName("incidentProbability")]
    public decimal IncidentProbability { get; init; }

    [JsonPropertyName("context")]
    public PodiumPredictionContextDto Context { get; init; } = new(0, 0, 0, 0, 0);
}

public sealed record PodiumPredictionContextDto(
    [property: JsonPropertyName("trackAffinity")] decimal TrackAffinity,
    [property: JsonPropertyName("conditionsAffinity")] decimal ConditionsAffinity,
    [property: JsonPropertyName("recentForm")] decimal RecentForm,
    [property: JsonPropertyName("pressure")] decimal Pressure,
    [property: JsonPropertyName("pressureResponse")] decimal PressureResponse);

public sealed record PodiumPredictionResponseDto(
    [property: JsonPropertyName("season")] int Season,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("round")] SeasonSummaryRoundDto? Round,
    [property: JsonPropertyName("items")] IReadOnlyList<PodiumPredictionRiderDto> Items)
{
    [JsonPropertyName("modelVersion")]
    public string ModelVersion { get; init; } = "ridge-monte-carlo-v1";
}
