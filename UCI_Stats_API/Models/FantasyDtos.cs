using System.Text.Json.Serialization;

namespace UCI_Stats_API.Models;

public sealed record FantasyRiderDto(
    [property: JsonPropertyName("pinkbikeExternalId")] string PinkbikeExternalId,
    [property: JsonPropertyName("riderId")] string? RiderId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: JsonPropertyName("gender")] string Gender,
    [property: JsonPropertyName("price")] decimal? Price,
    [property: JsonPropertyName("pinkbikeSeasonPoints")] int? PinkbikeSeasonPoints,
    [property: JsonPropertyName("injuryFlag")] bool InjuryFlag,
    [property: JsonPropertyName("expectedFinalPoints")] decimal? ExpectedFinalPoints,
    [property: JsonPropertyName("expectedQ1Bonus")] decimal? ExpectedQ1Bonus,
    [property: JsonPropertyName("expectedFantasyPoints")] decimal? ExpectedFantasyPoints,
    [property: JsonPropertyName("predictedPosition")] decimal? PredictedPosition,
    [property: JsonPropertyName("confidence")] decimal? Confidence,
    [property: JsonPropertyName("isRecommended")] bool IsRecommended,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);

public sealed record FantasyRoundDto(
    [property: JsonPropertyName("roundId")] string RoundId,
    [property: JsonPropertyName("roundNumber")] int? RoundNumber,
    [property: JsonPropertyName("venue")] string? Venue,
    [property: JsonPropertyName("trackName")] string? TrackName,
    [property: JsonPropertyName("startDate")] string? StartDate);

public sealed record FantasyRecommendationDto(
    [property: JsonPropertyName("riders")] IReadOnlyList<FantasyRiderDto> Riders,
    [property: JsonPropertyName("totalPrice")] decimal TotalPrice,
    [property: JsonPropertyName("remainingBudget")] decimal RemainingBudget,
    [property: JsonPropertyName("expectedFantasyPoints")] decimal ExpectedFantasyPoints);

public sealed record FantasyCurrentDto(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("season")] int Season,
    [property: JsonPropertyName("round")] FantasyRoundDto? Round,
    [property: JsonPropertyName("riders")] IReadOnlyList<FantasyRiderDto> Riders,
    [property: JsonPropertyName("recommendation")] FantasyRecommendationDto? Recommendation,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings,
    [property: JsonPropertyName("fetchedAtUtc")] DateTimeOffset? FetchedAtUtc,
    [property: JsonPropertyName("priceSnapshotId")] string? PriceSnapshotId,
    [property: JsonPropertyName("sourceUrl")] string SourceUrl,
    [property: JsonPropertyName("predictionEngineVersion")] string PredictionEngineVersion,
    [property: JsonPropertyName("fantasyProjectionVersion")] string FantasyProjectionVersion);

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
