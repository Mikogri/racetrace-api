namespace RaceTrace.Api.Services;

internal enum PaceSessionType
{
    Final,
    SemiFinal,
    Qualifying1,
    Qualifying2,
    Qualifying,
    Seeding,
    TimedTraining,
    Training
}

internal sealed record PaceRiderRun(
    string RiderId,
    IReadOnlyList<double?> SectorTimes,
    double? TopSpeed = null,
    int? FinishRank = null,
    double? FinishTimeSeconds = null,
    double? HistoricalExpectedPercentile = null,
    bool IsClassified = true,
    string? Status = null);

internal sealed record PaceSessionData(
    PaceSessionType SessionType,
    IReadOnlyList<PaceRiderRun> Runs,
    int? CompetitionClassPriority = null);

internal sealed record PaceFieldSummary(
    int Count,
    double Median,
    double LogMedian,
    double LogMadScale,
    double LinearMadScale,
    double? BestValue = null,
    double? SecondBestValue = null);

internal sealed record PacePrecomputedSector(
    int SectorIndex,
    double? TimeSeconds,
    int? Rank,
    PaceFieldSummary Statistics);

internal sealed record PacePrecomputedSessionData(
    string SessionId,
    string CourseKey,
    string? CategoryId,
    PaceSessionType SessionType,
    PaceRiderRun Run,
    int ClassifiedFieldSize,
    IReadOnlyList<PacePrecomputedSector> Sectors,
    PaceFieldSummary? SpeedStatistics,
    int? SpeedRank,
    PaceFieldSummary? FinishStatistics,
    IReadOnlyList<PaceFieldSummary?> CumulativeStatistics,
    int? CompetitionClassPriority = null,
    DateTimeOffset? SessionDate = null)
{
    // Prepared for the complete session/category, before selecting any rider.
    public PaceFinishReference? PaceFinish { get; init; }
    internal (IReadOnlyList<PacePrecomputedSector> Sectors, PaceOptions Options,
        IReadOnlyList<SectorAnomaly> Values)? PreparedAnomalies { get; init; }
    internal (PaceRiderRun Run, IReadOnlyList<double?> Times)? PreparedCumulativeTimes { get; init; }
}

internal sealed record PaceFinishReference(double? TimeSeconds, double RemovedLossSeconds, PaceFieldSummary Statistics);

internal enum PaceSectorStatus
{
    Valid,
    Incident,
    RecoveredAfterIncident,
    AfterIncident,
    Missing,
    InvalidTiming,
    PossibleIncident
}

internal sealed record PaceSectorResult(
    int SectorIndex,
    PaceSectorStatus Status,
    double? Time,
    int? Rank,
    double? Percentile,
    double? PerformanceZ,
    double? Score)
{
    public SectorAnomaly? Anomaly { get; init; }
}

internal sealed record RiderSessionPaceResult(
    string RiderId,
    PaceSessionType SessionType,
    double? Pace,
    double? SectorPace,
    double? SpeedScore,
    double? FullRunTimeScore,
    double Confidence,
    double SessionWeight,
    bool IsRepresentative,
    int? IncidentSectorIndex,
    IReadOnlyList<PaceSectorResult> Sectors,
    double SectorCoverage = 1.0,
    double? EvidenceAdjustedSectorPace = null,
    double DominanceBonus = 0,
    string? NoResultReason = null,
    int? CompetitionClassPriority = null,
    DateTimeOffset? SessionDate = null);

internal sealed record RiderSessionPaceObservation(
    string SessionId,
    string RiderId,
    RiderSessionPaceResult Result);

internal sealed record RiderPaceResult(
    string RiderId,
    double? Pace,
    double? RawPace,
    double Confidence,
    int UsedSessions,
    int UsedFinals = 0,
    double TotalEvidence = 0,
    double FinalEvidence = 0,
    string ConfidenceStatus = "Low");

internal sealed record WeightedScore(double Score, double Weight);

internal sealed record WeightedPace(double Pace, double Weight);

internal sealed record StagePaceResult(
    double Pace,
    double EvidenceWeight,
    double RoundWeight,
    int UsedSessions);
