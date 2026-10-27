using RaceTrace.Api.Services;

namespace RaceTrace.Api.Tests;

public sealed class SectorIncidentEvidenceTests
{
    [Fact]
    public void OneExceptionalSectorDoesNotTurnTheRestOfTheRunIntoIncidents()
    {
        var result = SectorIncidentDetectionTests.Calculate([40, 50, 50, 50, 50]);
        Assert.Null(result.IncidentSectorIndex);
        Assert.All(result.Sectors, s => Assert.NotNull(s.Score));
    }

    [Fact]
    public void OrdinarySectorVariationIsNotAnIncidentInAWideField()
    {
        var sectors = Enumerable.Range(0, 5).Select(i => new PacePrecomputedSector(i,
            i == 2 ? 54 : 50, 15,
            new PaceFieldSummary(30, 50, Math.Log(50), .12, 1, 45))).ToArray();
        Assert.All(SectorIncidentDetector.Analyze(sectors, new PaceOptions()),
            a => Assert.Equal(SectorAnomalyKind.Normal, a.Kind));
    }

    [Fact]
    public void PartialTimingDoesNotTreatOneModeratelyFasterSectorAsReliableBaseline()
    {
        var result = SectorIncidentDetectionTests.Calculate([40, 50, null, null, null]);
        Assert.Null(result.IncidentSectorIndex);
    }

    [Fact]
    public void ExtremeLossCanBeDetectedWithOnlyOneOtherMeasuredSector()
    {
        var result = SectorIncidentDetectionTests.Calculate([50, 250, null, null, null]);
        Assert.Equal(1, result.IncidentSectorIndex);
        Assert.Null(result.Sectors[1].Score);
        Assert.False(result.Sectors[1].Anomaly!.AllowsFinishCorrection);
    }

    [Fact]
    public void ExtremeFieldOutlierDoesNotRequireAnyOtherMeasuredSector()
    {
        var result = SectorIncidentDetectionTests.Calculate([null, 250, null, null, null]);
        Assert.Equal(1, result.IncidentSectorIndex);
        Assert.False(result.Sectors[1].Anomaly!.AllowsFinishCorrection);
        Assert.Null(result.Pace);
    }

    [Fact]
    public void WinningRanksCannotTurnConsistentlySlowTimesIntoAnIncident()
    {
        var sectors = Enumerable.Range(0, 5).Select(i => new PacePrecomputedSector(i, 80, 1,
            new PaceFieldSummary(30, 50, Math.Log(50), .01, .5, 48))).ToArray();
        Assert.DoesNotContain(SectorIncidentDetector.Analyze(sectors, new PaceOptions()),
            a => a.Kind == SectorAnomalyKind.LikelyIncident);
    }

    [Fact]
    public void WinningRanksCannotExaggerateASmallTerminalTimeLoss()
    {
        var sectors = Enumerable.Range(0, 5).Select(i => new PacePrecomputedSector(i,
            i == 4 ? 53.11 : 50, i == 4 ? 25 : 1,
            new PaceFieldSummary(30, 50, Math.Log(50), .01, .5, 48))).ToArray();
        Assert.DoesNotContain(SectorIncidentDetector.Analyze(sectors, new PaceOptions()),
            a => a.Kind == SectorAnomalyKind.LikelyIncident);
    }

    [Fact]
    public void GradualVariationKeepsEverySectorWithoutPenalizingConfidence()
    {
        var result = SectorIncidentDetectionTests.Calculate([50, 54, 55, 56]);
        Assert.Null(result.IncidentSectorIndex);
        Assert.All(result.Sectors, s => Assert.Equal(PaceSectorStatus.Valid, s.Status));
        Assert.All(result.Sectors, s => Assert.NotNull(s.Score));
    }

    [Theory]
    [MemberData(nameof(ConfirmedLargeLossProfiles))]
    public void ConfirmedIncidentsStillRequireSufficientTimingEvidence(
        object profile, int incidentIndex, bool sufficientEvidence)
    {
        var sectors = (PacePrecomputedSector[])profile;
        var anomalies = SectorIncidentDetector.Analyze(sectors, new PaceOptions());
        Assert.Equal(sufficientEvidence ? SectorAnomalyKind.LikelyIncident : SectorAnomalyKind.Normal,
            anomalies[incidentIndex].Kind);
        if (sufficientEvidence) Assert.True(anomalies[incidentIndex].LossSeconds > 4);
    }

    public static TheoryData<object, int, bool> ConfirmedLargeLossProfiles => new()
    {
        // Joe Breeden, Val di Sole 2025: only S1 and the extreme S2 were measured.
        { Profile([45.508, 248.630, null, null, null],
            [45.728, 58.811, 44.894, 36.9365, 39.468],
            [.013712, .009437, .019341, .021734, .026055],
            [11, 30, null, null, null], [30, 30, 28, 28, 28],
            [44.401, 56.980, 43.364, 35.591, 37.616]), 1, true },
        // Finn Iles, Les Gets 2024: large field MAD must not hide a 97.747 s sector.
        { Profile([36.209, 57.271, 58.542, 97.747, 36.856],
            [36.161, 60.508, 62.457, 46.024, 35.046],
            [.021855, .024882, .118687, .310359, .058087],
            [17, 2, 5, 31, 17], [31, 31, 31, 31, 20],
            [34.927, 56.518, 57.195, 34.943, 33.310]), 3, true },
        // Nina Hoffmann, Leogang 2024: several weak sectors suppress rank evidence.
        { Profile([53.528, 57.129, 43.436, 73.413, 46.826],
            [40.0205, 53.1285, 43.5015, 51.7215, 41.3595],
            [.045724, .027566, .013689, .059822, .036254],
            [12, 12, 4, 10, 11], [12, 12, 12, 12, 12],
            [38.729, 50.792, 41.973, 49.255, 39.125]), 3, true },
        // Confirmed crash, but S4 is near the median of an extremely unstable field.
        // Do not manufacture a strong timing anomaly just to match the source label.
        { Profile([37.055, 66.308, 66.989, 71.301, 38.180],
            [39.1135, 72.2645, 71.0895, 68.8075, 38.9945],
            [.032187, .064922, .056794, .344451, .074347],
            [1, 1, 1, 7, 5], [12, 12, 12, 12, 12],
            [37.055, 66.308, 66.989, 47.531, 35.753]), 3, false },
        // Finn Iles, Loudenvielle 2025: S1 must not mask the second loss on S3.
        { Profile([45.163, 56.286, 88.922, 24.460, 20.045],
            [37.950, 56.074, 62.713, 22.332, 19.475],
            [.018596, .010932, .018077, .016691, .018076],
            [26, 18, 26, 27, 26], [27, 27, 27, 27, 27],
            [36.657, 54.590, 60.497, 21.079, 18.303]), 2, true }
    };

    private static PacePrecomputedSector[] Profile(double?[] times, double[] medians,
        double[] deviations, int?[] ranks, int[] counts, double[] best) =>
        times.Select((time, i) => new PacePrecomputedSector(i, time, ranks[i],
            new(counts[i], medians[i], Math.Log(medians[i]), deviations[i], 1, best[i]))).ToArray();
}
