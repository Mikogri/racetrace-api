using RaceTrace.Api.Features.Fantasy;
using RaceTrace.Api.Infrastructure.Calculations;

namespace RaceTrace.Api.Tests;

public sealed class FantasyConfigurationTests
{
    [Fact]
    public void ManifestIncludesEffectiveFantasySimulationAndCalibrationSettings()
    {
        var manifest = System.Text.Json.JsonSerializer.SerializeToNode(AlgorithmCatalog.Current.Manifest["fantasy"].Configuration)!;
        Assert.NotNull(manifest["model"]);
        Assert.Equal(10_000, manifest["model"]!["HistoricalIterations"]!.GetValue<int>());
        Assert.Equal(50_000, manifest["model"]!["ForecastIterations"]!.GetValue<int>());
        Assert.Equal(100, manifest["model"]!["BlendSteps"]!.GetValue<int>());
    }
}
