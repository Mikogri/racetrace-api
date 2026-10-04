using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace RaceTrace.Api.Models;

public sealed record RankingRiderDto(
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("riderId")] long RiderId,
    [property: JsonPropertyName("uciId")] string? UciId,
    [property: JsonPropertyName("firstName")] string? FirstName,
    [property: JsonPropertyName("lastName")] string? LastName,
    [property: JsonPropertyName("fullName")] string FullName,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: JsonPropertyName("teamId")] long? TeamId,
    [property: JsonPropertyName("teamName")] string? TeamName,
    [property: JsonPropertyName("totalPoints")] decimal TotalPoints,
    [property: JsonPropertyName("rating")] decimal? Rating,
    [property: JsonPropertyName("pace")] decimal? Pace,
    [property: JsonPropertyName("consistency")] decimal? Consistency,
    [property: JsonPropertyName("confidence")] decimal Confidence,
    [property: JsonPropertyName("finalsCount")] int FinalsCount,
    [property: JsonPropertyName("qualifyingRate")] decimal QualifyingRate,
    [property: JsonPropertyName("profileImageUrl")] string? ProfileImageUrl)
{
    [JsonPropertyName("consistencyEvidence")]
    public decimal ConsistencyEvidence { get; init; }

    [JsonPropertyName("positionChange")]
    public int? PositionChange { get; init; }
}

public sealed record RankingSummaryDto(
    [property: JsonPropertyName("sessionsCount")] int SessionsCount,
    [property: JsonPropertyName("finalsCount")] int FinalsCount,
    [property: JsonPropertyName("ridersCount")] int RidersCount);

public sealed record RankingResponseDto(
    [property: JsonPropertyName("items")] IReadOnlyList<RankingRiderDto> Items,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pageSize")] int PageSize,
    [property: JsonPropertyName("totalItems")] int TotalItems,
    [property: JsonPropertyName("totalPages")] int TotalPages,
    [property: JsonPropertyName("hasPreviousPage")] bool HasPreviousPage,
    [property: JsonPropertyName("hasNextPage")] bool HasNextPage,
    [property: JsonPropertyName("summary")] RankingSummaryDto Summary,
    [property: JsonPropertyName("comparisonBasis")] string? ComparisonBasis = null);
