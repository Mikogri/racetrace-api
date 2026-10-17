using System.Text.Json.Nodes;

namespace RaceTrace.Api.Models;

public sealed record PublicPagedResponse<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages,
    bool HasPreviousPage,
    bool HasNextPage)
{
    public static PublicPagedResponse<T> Create(
        IReadOnlyList<T> items,
        int page,
        int pageSize,
        int totalItems)
    {
        var totalPages = totalItems == 0 ? 0 : (int)Math.Ceiling(totalItems / (double)pageSize);
        return new PublicPagedResponse<T>(
            items,
            page,
            pageSize,
            totalItems,
            totalPages,
            page > 1,
            page < totalPages);
    }
}

public sealed record ConditionDto(
    long? Id,
    string? Code,
    string? Name);

public sealed record TrackFilterOptionDto(
    long TrackId,
    string? TrackName,
    long? VenueId,
    string? VenueName,
    string? CountryCode,
    int? LengthM,
    decimal? AvgSlope);
