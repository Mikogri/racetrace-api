using System.Text.Json.Nodes;

namespace RaceTrace.Api.Models;

public sealed record RoundListItemDto(
    long RoundId,
    int? RoundNumber,
    int Season,
    string? ContextName,
    string? ImageUrl,
    long TrackId,
    string? TrackName,
    long? VenueId,
    string? VenueName,
    string? CountryCode,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string Status,
    long? CompetitionClassId,
    string? CompetitionClassCode,
    string? CompetitionClassName,
    int? CompetitionClassPriority);
