using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class PaceOptimizationTests
{
    [Fact]
    public void OverallEvidenceUsesEachSessionWeightAndFinalConfidenceSeparately()
    {
        var calculator = new DownhillPaceCalculator();
        var final = new RiderSessionPaceResult("target", PaceSessionType.Final, 90, 90,
            null, null, 50, 4, true, null, []);
        var qualifying = final with
        {
            SessionType = PaceSessionType.Qualifying,
            Confidence = 25,
            SessionWeight = 1.5
        };
        var ignoredRider = final with { RiderId = "other", Confidence = 100 };

        var result = calculator.CalculatePace("target", [final, qualifying, ignoredRider]);

        Assert.Equal(2.375, result.TotalEvidence, precision: 12);
        Assert.Equal(0.5, result.FinalEvidence, precision: 12);
        Assert.Equal(100 * (1 - Math.Exp(-2.375 / 4)), result.Confidence, precision: 12);
        Assert.Equal(2, result.UsedSessions);
        Assert.Equal(1, result.UsedFinals);

        var weighted = calculator.CalculatePace("target",
            [final with { CompetitionClassPriority = 1 },
                qualifying with { CompetitionClassPriority = 2 }],
            competitionClassWeighting: CompetitionClassWeighting.FromPriorities([1, 2]));
        Assert.Equal(4.375, weighted.TotalEvidence, precision: 12);
        Assert.Equal(1, weighted.FinalEvidence, precision: 12);
    }

    [Fact]
    public void RejectsNonFiniteAndZeroDivisorConfiguration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DownhillPaceCalculator(new PaceOptions { TempoPower = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DownhillPaceCalculator(new PaceOptions { TempoPower = double.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DownhillPaceCalculator(new PaceOptions { FullEvidenceSectorCount = double.PositiveInfinity }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DownhillPaceCalculator(new PaceOptions { SessionWeights = new() { Final = double.NaN } }));
    }
}
