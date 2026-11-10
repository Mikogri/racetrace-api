namespace RaceTrace.Api.Services;

internal sealed record ConsistencyRunInput(
    string RunId,
    string CourseKey,
    PaceSessionType SessionType,
    IReadOnlyList<ConsistencySectorTimeInput> Sectors,
    int? FinishRank = null,
    int? FieldSize = null,
    bool IsClassified = false,
    int? CompetitionClassPriority = null,
    double? ProjectedFinishPositionScore = null,
    string? Status = null,
    bool HasIncident = false
);

internal sealed record ConsistencySectorTimeInput(
    string SectorKey,
    double? TimeSeconds,
    bool IsOutlier = false,
    bool IsInvalidTiming = false
);

internal sealed record SectorConsistencyResult(
    string CourseKey,
    string SectorKey,
    double Consistency,
    double? RelativeVariation,
    int TotalRuns,
    int CompletedRuns
);

internal sealed record RiderConsistencyResult(
    double? Consistency,
    double Confidence,
    int UsedRuns,
    int UsedSectors,
    IReadOnlyList<SectorConsistencyResult> Sectors
)
{
    public double? IncidentReliability { get; init; }

    public double? FinalQualification { get; init; }

    public double? QualificationQuality { get; init; }

    public double? PositionRepeatability { get; init; }
}

internal sealed record ConsistencyOutlierProfile(
    IReadOnlySet<int> SectorIndexes,
    double? ProjectedFinishPositionScore
);
