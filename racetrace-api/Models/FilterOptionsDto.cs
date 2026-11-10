namespace RaceTrace.Api.Models;

public sealed record FilterOptionsDto
{
    public required IReadOnlyList<int> Seasons { get; init; }
    public required IReadOnlyList<ContextTrackOptionDto> Tracks { get; init; }
    public required IReadOnlyList<ContextNamedOptionDto> Conditions { get; init; }
    public required IReadOnlyList<ContextNamedOptionDto> CompetitionClasses { get; init; }
}

public sealed record ContextTrackOptionDto(
    long Id,
    string? Name,
    string? Venue,
    string? CountryCode
);

public sealed record ContextNamedOptionDto(long Id, string Name);
