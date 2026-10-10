namespace RaceTrace.Api.Configuration;

public sealed class ReadOptimizationOptions
{
    // Retained configuration name: enables source-package reuse, not stored calculator outputs.
    public bool PreparedRiderDetails { get; set; }
    public bool TypedRanking { get; set; }
}
