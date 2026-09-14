using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace UCI_Stats_API.Models;

public sealed record RoundListItemDto(
    [property: JsonPropertyName("roundId")] long RoundId,
    [property: JsonPropertyName("roundNumber")] int? RoundNumber,
    [property: JsonPropertyName("season")] int Season,
    [property: JsonPropertyName("contextName")] string? ContextName,
    [property: JsonPropertyName("imageUrl")] string? ImageUrl,
    [property: JsonPropertyName("trackId")] long TrackId,
    [property: JsonPropertyName("trackName")] string? TrackName,
    [property: JsonPropertyName("venueId")] long? VenueId,
    [property: JsonPropertyName("venueName")] string? VenueName,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: JsonPropertyName("startDate")] DateOnly? StartDate,
    [property: JsonPropertyName("endDate")] DateOnly? EndDate,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("competitionClassId")] long? CompetitionClassId,
    [property: JsonPropertyName("competitionClassCode")] string? CompetitionClassCode,
    [property: JsonPropertyName("competitionClassName")] string? CompetitionClassName,
    [property: JsonPropertyName("competitionClassPriority")] int? CompetitionClassPriority);
