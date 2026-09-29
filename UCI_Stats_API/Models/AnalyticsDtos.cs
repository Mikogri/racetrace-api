using System.Text.Json.Serialization;

namespace UCI_Stats_API.Models;

public sealed record SeasonOverviewResponseDto(
    [property: JsonPropertyName("season")] int Season,
    [property: JsonPropertyName("nextRound")] SeasonSummaryRoundDto? NextRound,
    [property: JsonPropertyName("lastCompletedRound")] SeasonSummaryRoundDto? LastCompletedRound,
    [property: JsonPropertyName("progress")] SeasonProgressDto Progress);

public sealed record RoundProfileResultDto(
    [property: JsonPropertyName("position")] int Position,
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
    [property: JsonPropertyName("position")] int Position,
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
    [property: JsonPropertyName("classification")] string Classification,
    [property: JsonPropertyName("p10GapPercent")] decimal? P10GapPercent,
    [property: JsonPropertyName("points")] IReadOnlyList<FieldSpreadPointDto> Points);

public sealed record TrackFingerprintSectorDto(
    [property: JsonPropertyName("sectorIndex")] int SectorIndex,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("ridersCount")] int RidersCount,
    [property: JsonPropertyName("scoreSpread")] decimal ScoreSpread,
    [property: JsonPropertyName("decisiveness")] decimal Decisiveness,
    [property: JsonPropertyName("relativeImpact")] decimal RelativeImpact);

public sealed record TrackFingerprintDto(
    [property: JsonPropertyName("mostDecisiveSector")] int? MostDecisiveSector,
    [property: JsonPropertyName("sectors")] IReadOnlyList<TrackFingerprintSectorDto> Sectors)
{
    [JsonPropertyName("positionSpread")]
    public IReadOnlyList<FieldSpreadPointDto> PositionSpread { get; init; } = [];

    [JsonPropertyName("positionSpreadFullPosition")]
    public int PositionSpreadFullPosition { get; init; }
}

public sealed record RoundProfileResponseDto(
    [property: JsonPropertyName("season")] int Season,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("round")] SeasonSummaryRoundDto? Round,
    [property: JsonPropertyName("sessionId")] long? SessionId,
    [property: JsonPropertyName("results")] IReadOnlyList<RoundProfileResultDto> Results,
    [property: JsonPropertyName("fieldSpread")] FieldSpreadDto FieldSpread,
    [property: JsonPropertyName("trackFingerprint")] TrackFingerprintDto TrackFingerprint)
{
    [JsonPropertyName("analytics")]
    public RoundRaceAnalyticsDto? Analytics { get; init; }
}

public sealed record PressurePerformancePointDto(
    [property: JsonPropertyName("roundId")] long RoundId,
    [property: JsonPropertyName("season")] int? Season,
    [property: JsonPropertyName("roundNumber")] int? RoundNumber,
    [property: JsonPropertyName("date")] DateOnly? Date,
    [property: JsonPropertyName("venueName")] string? VenueName,
    [property: JsonPropertyName("roundName")] string? RoundName,
    [property: JsonPropertyName("preFinalSession")] string? PreFinalSession,
    [property: JsonPropertyName("preFinalPace")] decimal? PreFinalPace,
    [property: JsonPropertyName("qualifyingPace")] decimal? QualifyingPace,
    [property: JsonPropertyName("finalPace")] decimal? FinalPace,
    [property: JsonPropertyName("delta")] decimal? Delta);

public sealed record PressurePerformanceSummaryDto(
    [property: JsonPropertyName("qualifyingPace")] decimal? QualifyingPace,
    [property: JsonPropertyName("finalPace")] decimal? FinalPace,
    [property: JsonPropertyName("delta")] decimal? Delta,
    [property: JsonPropertyName("tendency")] string Tendency,
    [property: JsonPropertyName("pairedRounds")] int PairedRounds);

public sealed record PressurePerformanceResponseDto(
    [property: JsonPropertyName("riderId")] long RiderId,
    [property: JsonPropertyName("pressurePerformance")] decimal? PressurePerformance,
    [property: JsonPropertyName("pressureTrend")] string PressureTrend,
    [property: JsonPropertyName("sampleCount")] int SampleCount,
    [property: JsonPropertyName("evidence")] string Evidence,
    [property: JsonPropertyName("summary")] PressurePerformanceSummaryDto Summary,
    [property: JsonPropertyName("points")] IReadOnlyList<PressurePerformancePointDto> Points);
