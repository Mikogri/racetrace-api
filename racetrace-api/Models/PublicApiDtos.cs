using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace RaceTrace.Api.Models;

public sealed record PublicPagedResponse<T>(
    [property: JsonPropertyName("items")] IReadOnlyList<T> Items,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pageSize")] int PageSize,
    [property: JsonPropertyName("totalItems")] int TotalItems,
    [property: JsonPropertyName("totalPages")] int TotalPages,
    [property: JsonPropertyName("hasPreviousPage")] bool HasPreviousPage,
    [property: JsonPropertyName("hasNextPage")] bool HasNextPage)
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
    [property: JsonPropertyName("id")] long? Id,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("name")] string? Name);

public sealed record TrackFilterOptionDto(
    [property: JsonPropertyName("trackId")] long TrackId,
    [property: JsonPropertyName("trackName")] string? TrackName,
    [property: JsonPropertyName("venueId")] long? VenueId,
    [property: JsonPropertyName("venueName")] string? VenueName,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: JsonPropertyName("lengthM")] int? LengthM,
    [property: JsonPropertyName("avgSlope")] decimal? AvgSlope);
