namespace RaceTrace.Api.Models;

public sealed record FantasyRiderDto(
    string PinkbikeExternalId,
    string? RiderId,
    string Name,
    string? CountryCode,
    [property: Infrastructure.ApiContractValues("male", "female")] string Gender,
    decimal? Price,
    int? PinkbikeSeasonPoints,
    bool InjuryFlag,
    decimal? ExpectedFinalPoints,
    decimal? ExpectedQ1Bonus,
    decimal? ExpectedFantasyPoints,
    decimal? PredictedPosition,
    decimal? Confidence,
    bool IsRecommended,
    IReadOnlyList<string> Warnings);

public sealed record FantasyRoundDto(
    string RoundId,
    int? RoundNumber,
    string? Venue,
    string? TrackName,
    string? StartDate);

public sealed record FantasyRecommendationDto(
    IReadOnlyList<FantasyRiderDto> Riders,
    decimal TotalPrice,
    decimal RemainingBudget,
    decimal ExpectedFantasyPoints);

public sealed record FantasyCurrentDto(
    string Status,
    int Season,
    FantasyRoundDto? Round,
    IReadOnlyList<FantasyRiderDto> Riders,
    FantasyRecommendationDto? Recommendation,
    IReadOnlyList<string> Warnings,
    DateTimeOffset? FetchedAtUtc,
    string? PriceSnapshotId,
    string SourceUrl,
    string PredictionEngineVersion,
    string FantasyProjectionVersion,
    FantasyCalculationDto? Calculation = null);

public sealed record FantasyCalculationDto(
    string AlgorithmVersion,
    string? ConfigurationHash,
    string? DataRevision,
    DateTimeOffset ComputedAt,
    string ModelVersion,
    DateTimeOffset? TrainingDataCutoff,
    DateTimeOffset? PredictionAsOf,
    int? SimulationSeed,
    int? SimulationIterations,
    string CalculationFingerprint);

public sealed record FantasyPlacementDto(
    int Position,
    int? MenPoints,
    int? WomenPoints);
public sealed record FantasyBonusDto(int Position, int Points);
public sealed record FantasyRulesDto(
    int Season,
    string Version,
    decimal Budget,
    int TeamSize,
    int Men,
    int Women,
    IReadOnlyList<FantasyPlacementDto> FinalPlacementPoints,
    IReadOnlyList<FantasyBonusDto> Q1Bonus,
    bool DynamicPricing,
    string SourceUrl);
