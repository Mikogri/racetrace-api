using System.Text.Json.Serialization;

namespace RaceTrace.Api.Models;

public sealed record SeasonOverviewResponseDto(
    int Season,
    [property: JsonPropertyName("nextRound")] SeasonSummaryRoundDto? NextRound,
    [property: JsonPropertyName("lastCompletedRound")] SeasonSummaryRoundDto? LastCompletedRound,
    SeasonProgressDto Progress);

public sealed record RoundProfileResultDto(
    int Position,
    [property: JsonPropertyName("resultId")] long ResultId,
    [property: JsonPropertyName("riderId")] long RiderId,
    [property: JsonPropertyName("fullName")] string FullName,
    [property: JsonPropertyName("countryCode")] string? CountryCode,
    [property: JsonPropertyName("teamId")] long? TeamId,
    [property: JsonPropertyName("teamName")] string? TeamName,
    [property: JsonPropertyName("raceTime")] decimal? RaceTime,
    [property: JsonPropertyName("gapToLeader")] decimal? GapToLeader,
    [property: JsonPropertyName("topSpeed")] decimal? TopSpeed);

public sealed record FieldSpreadPointDto(
    int Position,
    [property: JsonPropertyName("raceTime")] decimal RaceTime,
    [property: JsonPropertyName("gapToLeader")] decimal GapToLeader,
    [property: JsonPropertyName("gapPercent")] decimal GapPercent)
{
    [JsonPropertyName("previousPosition")] public int PreviousPosition { get; init; }
    [JsonPropertyName("incrementalSpread")] public decimal? IncrementalSpread { get; init; }
    [JsonPropertyName("incrementalSpreadPercent")] public decimal? IncrementalSpreadPercent { get; init; }
}

public sealed record FieldSpreadDto(
    [property: JsonPropertyName("fieldSize")] int FieldSize,
    string Classification,
    [property: JsonPropertyName("p10GapPercent")] decimal? P10GapPercent,
    IReadOnlyList<FieldSpreadPointDto> Points);

public sealed record TrackFingerprintSectorDto(
    [property: JsonPropertyName("sectorIndex")] int SectorIndex,
    string Label,
    [property: JsonPropertyName("ridersCount")] int RidersCount,
    [property: JsonPropertyName("scoreSpread")] decimal ScoreSpread,
    decimal Decisiveness,
    [property: JsonPropertyName("relativeImpact")] decimal RelativeImpact);

public sealed record TrackFingerprintDto(
    [property: JsonPropertyName("mostDecisiveSector")] int? MostDecisiveSector,
    IReadOnlyList<TrackFingerprintSectorDto> Sectors)
{
    [JsonPropertyName("positionSpread")]
    public IReadOnlyList<FieldSpreadPointDto> PositionSpread { get; init; } = [];

    [JsonPropertyName("positionSpreadFullPosition")]
    public int PositionSpreadFullPosition { get; init; }
}

public sealed record RoundProfileResponseDto(
    int Season,
    string Category,
    SeasonSummaryRoundDto? Round,
    [property: JsonPropertyName("sessionId")] long? SessionId,
    IReadOnlyList<RoundProfileResultDto> Results,
    [property: JsonPropertyName("fieldSpread")] FieldSpreadDto FieldSpread,
    [property: JsonPropertyName("trackFingerprint")] TrackFingerprintDto TrackFingerprint)
{
    public RoundRaceAnalyticsDto? Analytics { get; init; }
}

public sealed record PressurePerformancePointDto(
    [property: JsonPropertyName("roundId")] long RoundId,
    int? Season,
    [property: JsonPropertyName("roundNumber")] int? RoundNumber,
    DateOnly? Date,
    [property: JsonPropertyName("venueName")] string? VenueName,
    [property: JsonPropertyName("roundName")] string? RoundName,
    [property: JsonPropertyName("preFinalSession")] string? PreFinalSession,
    [property: JsonPropertyName("preFinalPace")] decimal? PreFinalPace,
    [property: JsonPropertyName("qualifyingPace")] decimal? QualifyingPace,
    [property: JsonPropertyName("finalPace")] decimal? FinalPace,
    decimal? Delta);

public sealed record PressurePerformanceSummaryDto(
    [property: JsonPropertyName("qualifyingPace")] decimal? QualifyingPace,
    [property: JsonPropertyName("finalPace")] decimal? FinalPace,
    decimal? Delta,
    string Tendency,
    [property: JsonPropertyName("pairedRounds")] int PairedRounds);

public sealed record PressurePerformanceResponseDto(
    [property: JsonPropertyName("riderId")] long RiderId,
    [property: JsonPropertyName("pressurePerformance")] decimal? PressurePerformance,
    [property: JsonPropertyName("pressureTrend")] string PressureTrend,
    [property: JsonPropertyName("sampleCount")] int SampleCount,
    string Evidence,
    PressurePerformanceSummaryDto Summary,
    IReadOnlyList<PressurePerformancePointDto> Points);
