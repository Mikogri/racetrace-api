namespace RaceTrace.Api.Models;

public sealed record PointAwardCalculationResult(
    string SessionId,
    bool PointSystemApplied,
    int ResultsProcessed,
    int PointRowsWritten,
    int TotalPoints,
    string? PointSystemCode,
    string? Warning
);
