using System.Text.Json.Serialization;

namespace RaceTrace.Api.Models;

// Explicit names preserve the public analytics contract despite the legacy global snake_case policy.
public sealed record RoundRaceAnalyticsDto(
    [property: JsonPropertyName("methodVersion")] string MethodVersion,
    [property: JsonPropertyName("sectorEfficiency")] IReadOnlyList<SectorEfficiencyDto> SectorEfficiency,
    ChaosIndexDto Chaos,
    QualiFinalCorrelationDto Correlation,
    [property: JsonPropertyName("winningMargin")] WinningMarginDto? WinningMargin,
    [property: JsonPropertyName("runEfficiency")] RunEfficiencyDto? RunEfficiency,
    PerformanceDistributionDto Distribution);

public sealed record SectorEfficiencyDto(
    [property: JsonPropertyName("sectorIndex")] int SectorIndex,
    [property: JsonPropertyName("ridersCount")] int RidersCount,
    [property: JsonPropertyName("positionsPerSecond")] decimal? PositionsPerSecond);
public sealed record ChaosComponentDto(
    string Key,
    decimal? Score,
    decimal Weight,
    [property: JsonPropertyName("sampleSize")] int SampleSize);
public sealed record ChaosIndexDto(
    decimal? Score,
    int Starters,
    [property: JsonPropertyName("dnfCount")] int DnfCount,
    [property: JsonPropertyName("incidentCount")] int IncidentCount,
    IReadOnlyList<ChaosComponentDto> Components);
public sealed record CorrelationRiderDto(
    [property: JsonPropertyName("riderId")] long RiderId,
    [property: JsonPropertyName("fullName")] string FullName,
    [property: JsonPropertyName("preFinalPosition")] int PreFinalPosition,
    [property: JsonPropertyName("finalPosition")] int FinalPosition);
public sealed record QualiFinalCorrelationDto(
    [property: JsonPropertyName("preFinalSession")] string? PreFinalSession,
    decimal? Spearman,
    IReadOnlyList<CorrelationRiderDto> Points);
public sealed record SectorContributionDto(
    [property: JsonPropertyName("sectorIndex")] int SectorIndex,
    [property: JsonPropertyName("gapSeconds")] decimal GapSeconds,
    [property: JsonPropertyName("sharePercent")] decimal? SharePercent);
public sealed record WinningMarginDto(
    [property: JsonPropertyName("winnerId")] long WinnerId,
    [property: JsonPropertyName("winnerName")] string WinnerName,
    [property: JsonPropertyName("runnerUpId")] long RunnerUpId,
    [property: JsonPropertyName("runnerUpName")] string RunnerUpName,
    [property: JsonPropertyName("marginSeconds")] decimal MarginSeconds,
    IReadOnlyList<SectorContributionDto> Sectors);
public sealed record RunEfficiencyDto(
    [property: JsonPropertyName("riderId")] long RiderId,
    [property: JsonPropertyName("fullName")] string FullName,
    [property: JsonPropertyName("sectorsCount")] int SectorsCount,
    [property: JsonPropertyName("measuredSectors")] int MeasuredSectors,
    [property: JsonPropertyName("sectorsWon")] int SectorsWon,
    [property: JsonPropertyName("sectorsTop3")] int SectorsTop3,
    [property: JsonPropertyName("averageSectorPosition")] decimal? AverageSectorPosition,
    [property: JsonPropertyName("sectorsFor80PercentGain")] int? SectorsFor80PercentGain)
{
    [JsonPropertyName("sectorsWonPercent")]
    public decimal? SectorsWonPercent => MeasuredSectors > 0 ? Math.Round(100m * SectorsWon / MeasuredSectors, 1) : null;

    [JsonPropertyName("sectorsTop3Percent")]
    public decimal? SectorsTop3Percent => MeasuredSectors > 0 ? Math.Round(100m * SectorsTop3 / MeasuredSectors, 1) : null;
}
public sealed record PerformanceBinDto(
    int From,
    int To,
    int Count);
public sealed record PerformanceOutlierDto(
    [property: JsonPropertyName("riderId")] long RiderId,
    [property: JsonPropertyName("fullName")] string FullName,
    decimal Pace);
public sealed record PerformanceDistributionDto(
    [property: JsonPropertyName("ridersCount")] int RidersCount,
    [property: JsonPropertyName("classifiedCount")] int ClassifiedCount,
    decimal? Median,
    IReadOnlyList<PerformanceBinDto> Bins,
    IReadOnlyList<PerformanceOutlierDto> Outliers);
