namespace UCI_Stats_API.Features.Fantasy.Import;

public sealed class FantasyImportOptions
{
    public string SnapshotDirectory { get; set; } = "App_Data/Fantasy/prices";
    public int TimeoutSeconds { get; set; } = 25;
    public int StaleAfterHours { get; set; } = 24;
    public int MinimumRiderCount { get; set; } = 6;
    public Dictionary<string, string> RiderMappings { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string[]> NameAliases { get; set; } = new(StringComparer.Ordinal);
}

public sealed record PinkbikeHistoricalRound(int PinkbikeRoundNumber, string Name, DateOnly Date, decimal? Price, int Points);

public sealed record PinkbikeFantasyRider(
    string ExternalId,
    string? RiderId,
    string Name,
    string Gender,
    decimal Price,
    int SeasonPoints,
    bool InjuryFlag,
    DateTimeOffset FetchedAtUtc,
    string MappingStatus,
    string? CountryName,
    IReadOnlyList<PinkbikeHistoricalRound> HistoricalRounds);

public sealed record FantasyPriceSnapshot(
    string Id,
    int Season,
    string? UpcomingRoundId,
    DateTimeOffset FetchedAtUtc,
    IReadOnlyList<PinkbikeFantasyRider> Riders,
    string SourceUrl,
    string AcquisitionMethod);

public sealed record FantasyPriceStatus(FantasyPriceSnapshot? Snapshot, bool IsStale, string? LastSyncError, DateTimeOffset? LastSyncAttemptUtc);

public sealed record FantasyRiderIdentity(string RiderId, string Name, string? Gender);
