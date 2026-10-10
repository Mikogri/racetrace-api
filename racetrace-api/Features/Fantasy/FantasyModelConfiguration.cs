namespace RaceTrace.Api.Features.Fantasy;

/// <summary>Existing simulation/calibration constants, retained verbatim and included in calculation identity.</summary>
public sealed record FantasyModelConfiguration
{
    public static FantasyModelConfiguration Current { get; } = new();
    public int HistoricalIterations { get; init; } = 10_000;
    public int ForecastIterations { get; init; } = 50_000;
    public int MinimumCalibrationEvents { get; init; } = 2;
    public int BlendSteps { get; init; } = 100;
    public double LikelihoodFloor { get; init; } = 1e-12;
    public decimal ProbabilityTolerance { get; init; } = 0.000000001m;
}
