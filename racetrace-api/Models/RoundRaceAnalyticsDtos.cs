namespace RaceTrace.Api.Models;

// Explicit names preserve the public analytics contract despite the legacy global snake_case policy.
public sealed record RoundRaceAnalyticsDto(
    string MethodVersion,
    IReadOnlyList<SectorEfficiencyDto> SectorEfficiency,
    ChaosIndexDto Chaos,
    QualiFinalCorrelationDto Correlation,
    WinningMarginDto? WinningMargin,
    RunEfficiencyDto? RunEfficiency,
    PerformanceDistributionDto Distribution);

public sealed record SectorEfficiencyDto(
    int SectorIndex,
    int RidersCount,
    decimal? PositionsPerSecond);
public sealed record ChaosComponentDto(
    string Key,
    decimal? Score,
    decimal Weight,
    int SampleSize);
public sealed record ChaosIndexDto(
    decimal? Score,
    int Starters,
    int DnfCount,
    int IncidentCount,
    IReadOnlyList<ChaosComponentDto> Components);
public sealed record CorrelationRiderDto(
    long RiderId,
    string FullName,
    int PreFinalPosition,
    int FinalPosition);
public sealed record QualiFinalCorrelationDto(
    string? PreFinalSession,
    decimal? Spearman,
    IReadOnlyList<CorrelationRiderDto> Points);
public sealed record SectorContributionDto(
    int SectorIndex,
    decimal GapSeconds,
    decimal? SharePercent);
public sealed record WinningMarginDto(
    long WinnerId,
    string WinnerName,
    long RunnerUpId,
    string RunnerUpName,
    decimal MarginSeconds,
    IReadOnlyList<SectorContributionDto> Sectors);
public sealed record RunEfficiencyDto(
    long RiderId,
    string FullName,
    int SectorsCount,
    int MeasuredSectors,
    int SectorsWon,
    int SectorsTop3,
    decimal? AverageSectorPosition,
    int? SectorsFor80PercentGain)
{
    public decimal? SectorsWonPercent => MeasuredSectors > 0 ? Math.Round(100m * SectorsWon / MeasuredSectors, 1) : null;

    public decimal? SectorsTop3Percent => MeasuredSectors > 0 ? Math.Round(100m * SectorsTop3 / MeasuredSectors, 1) : null;
}
public sealed record PerformanceBinDto(
    int From,
    int To,
    int Count);
public sealed record PerformanceOutlierDto(
    long RiderId,
    string FullName,
    decimal Pace);
public sealed record PerformanceDistributionDto(
    int RidersCount,
    int ClassifiedCount,
    decimal? Median,
    IReadOnlyList<PerformanceBinDto> Bins,
    IReadOnlyList<PerformanceOutlierDto> Outliers);
