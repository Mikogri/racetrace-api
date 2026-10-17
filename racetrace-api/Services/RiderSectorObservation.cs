namespace RaceTrace.Api.Services;

internal sealed record RiderSectorObservation(
    string RiderId,
    string SessionId,
    string? CategoryCode,
    int SectorIndex,
    int SectorCount,
    double Score);
