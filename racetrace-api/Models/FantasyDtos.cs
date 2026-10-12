using System.Text.Json.Serialization;

namespace RaceTrace.Api.Models;

public sealed record FantasyRiderDto(
    [property: JsonPropertyName("pinkbikeExternalId")] string PinkbikeExternalId,
    [property: JsonPropertyName("riderId")] string? RiderId,
    string Name,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: Infrastructure.ApiContractValues("male", "female")] string Gender,
    decimal? Price,
    [property: JsonPropertyName("pinkbikeSeasonPoints")] int? PinkbikeSeasonPoints,
    [property: JsonPropertyName("injuryFlag")] bool InjuryFlag,
    [property: JsonPropertyName("expectedFinalPoints")] decimal? ExpectedFinalPoints,
    [property: JsonPropertyName("expectedQ1Bonus")] decimal? ExpectedQ1Bonus,
    [property: JsonPropertyName("expectedFantasyPoints")] decimal? ExpectedFantasyPoints,
    [property: JsonPropertyName("predictedPosition")] decimal? PredictedPosition,
    decimal? Confidence,
    [property: JsonPropertyName("isRecommended")] bool IsRecommended,
    IReadOnlyList<string> Warnings);

public sealed record FantasyRoundDto(
    [property: JsonPropertyName("roundId")] string RoundId,
    [property: JsonPropertyName("roundNumber")] int? RoundNumber,
    string? Venue,
    [property: JsonPropertyName("trackName")] string? TrackName,
    [property: JsonPropertyName("startDate")] string? StartDate);

public sealed record FantasyRecommendationDto(
    IReadOnlyList<FantasyRiderDto> Riders,
    [property: JsonPropertyName("totalPrice")] decimal TotalPrice,
    [property: JsonPropertyName("remainingBudget")] decimal RemainingBudget,
    [property: JsonPropertyName("expectedFantasyPoints")] decimal ExpectedFantasyPoints);

public sealed record FantasyCurrentDto(
    string Status,
    int Season,
    FantasyRoundDto? Round,
    IReadOnlyList<FantasyRiderDto> Riders,
    FantasyRecommendationDto? Recommendation,
    IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("fetchedAtUtc")] DateTimeOffset? FetchedAtUtc,
    [property: JsonPropertyName("priceSnapshotId")] string? PriceSnapshotId,
    [property: JsonPropertyName("sourceUrl")] string SourceUrl,
    [property: JsonPropertyName("predictionEngineVersion")] string PredictionEngineVersion,
    [property: JsonPropertyName("fantasyProjectionVersion")] string FantasyProjectionVersion,
    FantasyCalculationDto? Calculation = null);

public sealed record FantasyCalculationDto(
    [property: JsonPropertyName("algorithmVersion")] string AlgorithmVersion,
    [property: JsonPropertyName("configurationHash")] string? ConfigurationHash,
    [property: JsonPropertyName("dataRevision")] string? DataRevision,
    [property: JsonPropertyName("computedAt")] DateTimeOffset ComputedAt,
    [property: JsonPropertyName("modelVersion")] string ModelVersion,
    [property: JsonPropertyName("trainingDataCutoff")] DateTimeOffset? TrainingDataCutoff,
    [property: JsonPropertyName("predictionAsOf")] DateTimeOffset? PredictionAsOf,
    [property: JsonPropertyName("simulationSeed")] int? SimulationSeed,
    [property: JsonPropertyName("simulationIterations")] int? SimulationIterations,
    [property: JsonPropertyName("calculationFingerprint")] string CalculationFingerprint);

public sealed record FantasyPlacementDto(
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("menPoints")] int? MenPoints,
    [property: JsonPropertyName("womenPoints")] int? WomenPoints);
public sealed record FantasyBonusDto([property: JsonPropertyName("position")] int Position, [property: JsonPropertyName("points")] int Points);
public sealed record FantasyRulesDto(
    [property: JsonPropertyName("season")] int Season,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("budget")] decimal Budget,
    [property: JsonPropertyName("teamSize")] int TeamSize,
    [property: JsonPropertyName("men")] int Men,
    [property: JsonPropertyName("women")] int Women,
    [property: JsonPropertyName("finalPlacementPoints")] IReadOnlyList<FantasyPlacementDto> FinalPlacementPoints,
    [property: JsonPropertyName("q1Bonus")] IReadOnlyList<FantasyBonusDto> Q1Bonus,
    [property: JsonPropertyName("dynamicPricing")] bool DynamicPricing,
    [property: JsonPropertyName("sourceUrl")] string SourceUrl);
