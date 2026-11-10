namespace RaceTrace.Api.Models;

public sealed record RoundProfileResultDto(
    int Position,
    long ResultId,
    long RiderId,
    string FullName,
    string? CountryCode,
    long? TeamId,
    string? TeamName,
    decimal? RaceTime,
    decimal? GapToLeader,
    decimal? TopSpeed
);

public sealed record FieldSpreadPointDto(
    int Position,
    decimal RaceTime,
    decimal GapToLeader,
    decimal GapPercent
)
{
    public int PreviousPosition { get; init; }
    public decimal? IncrementalSpread { get; init; }
    public decimal? IncrementalSpreadPercent { get; init; }
}

public sealed record FieldSpreadDto(
    int FieldSize,
    string Classification,
    decimal? P10GapPercent,
    IReadOnlyList<FieldSpreadPointDto> Points
);

public sealed record TrackFingerprintSectorDto(
    int SectorIndex,
    string Label,
    int RidersCount,
    decimal ScoreSpread,
    decimal Decisiveness,
    decimal RelativeImpact
);

public sealed record TrackFingerprintDto(
    int? MostDecisiveSector,
    IReadOnlyList<TrackFingerprintSectorDto> Sectors
)
{
    public IReadOnlyList<FieldSpreadPointDto> PositionSpread { get; init; } = [];

    public int PositionSpreadFullPosition { get; init; }
}

public sealed record RoundProfileResponseDto(
    int Season,
    string Category,
    SeasonSummaryRoundDto? Round,
    long? SessionId,
    IReadOnlyList<RoundProfileResultDto> Results,
    FieldSpreadDto FieldSpread,
    TrackFingerprintDto TrackFingerprint
)
{
    public RoundRaceAnalyticsDto? Analytics { get; init; }
}
