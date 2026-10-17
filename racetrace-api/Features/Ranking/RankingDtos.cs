using System.Text.Json.Nodes;

namespace RaceTrace.Api.Models;

public sealed record RankingRiderDto(
    int Position,
    long RiderId,
    string? UciId,
    string? FirstName,
    string? LastName,
    string FullName,
    string? CountryCode,
    long? TeamId,
    string? TeamName,
    decimal TotalPoints,
    decimal? Rating,
    decimal? Pace,
    decimal? Consistency,
    decimal Confidence,
    int FinalsCount,
    decimal QualifyingRate,
    string? ProfileImageUrl)
{
    public decimal ConsistencyEvidence { get; init; }

    public int? PositionChange { get; init; }
}

public sealed record RankingSummaryDto(
    int SessionsCount,
    int FinalsCount,
    int RidersCount);

public sealed record RankingResponseDto(
    IReadOnlyList<RankingRiderDto> Items,
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages,
    bool HasPreviousPage,
    bool HasNextPage,
    RankingSummaryDto Summary,
    [property: Infrastructure.ApiContractValues("previousRound", "defaultFilters", "previousFilters")] string? ComparisonBasis = null);
