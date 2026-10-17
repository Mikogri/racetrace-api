namespace RaceTrace.Api.Models;

public sealed record PressurePerformancePointDto(
    long RoundId,
    int? Season,
    int? RoundNumber,
    DateOnly? Date,
    string? VenueName,
    string? RoundName,
    string? PreFinalSession,
    decimal? PreFinalPace,
    decimal? QualifyingPace,
    decimal? FinalPace,
    decimal? Delta);

public sealed record PressurePerformanceSummaryDto(
    decimal? QualifyingPace,
    decimal? FinalPace,
    decimal? Delta,
    string Tendency,
    int PairedRounds);

public sealed record PressurePerformanceResponseDto(
    long RiderId,
    decimal? PressurePerformance,
    string PressureTrend,
    int SampleCount,
    string Evidence,
    PressurePerformanceSummaryDto Summary,
    IReadOnlyList<PressurePerformancePointDto> Points);
